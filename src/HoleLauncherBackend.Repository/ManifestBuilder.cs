using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Repository;

internal sealed class ManifestBuilder(ModResolver mods, IHttpClientFactory clients)
{
    private const int ParallelFiles = 6;

    public async Task<RepositoryManifest> BuildAsync(Guid id, RepositoryDraft draft, CancellationToken ct,
        Action<RepositoryBuildProgress>? progress = null)
    {
        if (id == Guid.Empty || draft.Folders is null || draft.Files is null)
            throw new BuildRejectedException("build.invalid_draft");
        if (draft.Name is not { } name || RepositoryStorage.DomainFor(name) is null)
            throw new BuildRejectedException("repository.upload_name");

        var errors = new ConcurrentQueue<RepositoryBuildError>();
        var storage = new ConcurrentDictionary<int, RepositoryArtifact>();
        var validated = new ConcurrentDictionary<int, RepositoryFile>();
        foreach (var folder in draft.Folders)
            if (!RepositoryStorage.IsSafeFolder(folder))
                errors.Enqueue(new("build.invalid_folder", [folder ?? ""]));

        var folders = draft.Folders.Where(RepositoryStorage.IsSafeFolder).ToArray();
        var cache = mods.CreateCache(draft.Minecraft, draft.Loader, ct);
        var seenProjects = new HashSet<string>(StringComparer.Ordinal);
        var work = new List<(int Index, RepositoryFile File)>();
        for (var index = 0; index < draft.Files.Length; index++)
        {
            var file = draft.Files[index];
            if (file is null || file.Card is null || !Enum.IsDefined(file.Type) || file.Folder is null)
                errors.Enqueue(new("build.invalid_file", [file?.Card?.Title ?? ""]));
            else if (file.Folder.Length != 0 && !folders.Contains(file.Folder, StringComparer.OrdinalIgnoreCase))
                errors.Enqueue(new("build.missing_folder", [file.Card.Title, file.Folder]));
            else if (file.ProjectId is { } project && !seenProjects.Add(project))
                errors.Enqueue(new("build.duplicate_mod", [file.Card.Title]));
            else
            {
                work.Add((index, file));
                progress?.Invoke(new(index, file.Card.Title, "Wait"));
            }
        }

        await Parallel.ForEachAsync(work, new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = ParallelFiles
        }, async (item, token) =>
        {
            var (index, file) = item;
            void Status(string state, string? dependency = null) =>
                progress?.Invoke(new(index, file.Card.Title, state, dependency));
            Status("LoadInfo");
            try
            {
                if (file.ProjectId is null)
                    storage[index] = await ResolveStorageAsync(file, token);
                else
                {
                    RepositoryBuildError? issue = null;
                    var dependencies = new HashSet<string>(StringComparer.Ordinal);
                    var artifacts = await mods.ResolveAsync(new(draft.Minecraft, draft.Loader, [file]), token,
                        (key, args) => issue = new(key, args), cache, dependency =>
                        {
                            if (dependencies.Add(dependency))
                                Status("ResolveDep", dependency);
                        });
                    if (artifacts is null)
                        throw new BuildRejectedException([issue ?? new("build.mod_unresolved", [file.Card.Title])]);
                    validated[index] = file;
                }
                Status("Done");
            }
            catch (BuildRejectedException error)
            {
                foreach (var issue in error.Errors)
                    errors.Enqueue(issue);
                Status("Failed");
            }
            catch (Exception error) when (!token.IsCancellationRequested &&
                error is HttpRequestException or JsonException or OperationCanceledException)
            {
                errors.Enqueue(new("build.file_upstream", [file.Card.Title]));
                Status("Failed");
            }
        });

        var resolved = Array.Empty<RepositoryArtifact>();
        var accepted = new List<RepositoryFile>();
        if (!validated.IsEmpty)
        {
            var roots = validated.OrderByDescending(entry => entry.Value.VersionLock is not null)
                .ThenBy(entry => entry.Key).Select(entry => entry.Value).ToArray();
            await IncludeRootsAsync(roots);
        }

        var files = resolved.ToList();
        var paths = resolved.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (index, artifact) in storage.OrderBy(entry => entry.Key))
        {
            if (HasPathConflict(artifact.Path, paths))
            {
                errors.Enqueue(new("build.path_conflict", [artifact.Path]));
                progress?.Invoke(new(index, artifact.Card.Title, "Failed"));
                continue;
            }
            paths.Add(artifact.Path);
            files.Add(artifact);
        }

        ct.ThrowIfCancellationRequested();
        var requested = work.Select(item => item.File).ToArray();
        return new(id, draft with { Folders = folders, Files = requested }, [.. files], false,
            errors.IsEmpty ? null : errors.ToArray());

        async Task IncludeRootsAsync(RepositoryFile[] candidates)
        {
            ct.ThrowIfCancellationRequested();
            RepositoryBuildError? issue = null;
            try
            {
                var artifacts = await mods.ResolveAsync(new(draft.Minecraft, draft.Loader, [.. accepted, .. candidates]), ct,
                    (key, args) => issue = new(key, args), cache);
                if (artifacts is not null)
                {
                    var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var collision = artifacts.FirstOrDefault(file =>
                        HasPathConflict(file.Path, selectedPaths) || !selectedPaths.Add(file.Path));
                    if (collision is null)
                    {
                        accepted.AddRange(candidates);
                        resolved = artifacts;
                        return;
                    }
                    issue = new("build.path_conflict", [collision.Path]);
                }
            }
            catch (Exception error) when (!ct.IsCancellationRequested &&
                error is HttpRequestException or JsonException or OperationCanceledException)
            {
                issue = candidates.Length == 1 ? new("build.file_upstream", [candidates[0].Card.Title]) : null;
            }

            if (candidates.Length == 1)
            {
                errors.Enqueue(issue ?? new("build.mod_unresolved", [candidates[0].Card.Title]));
                var index = Array.IndexOf(draft.Files, candidates[0]);
                progress?.Invoke(new(index, candidates[0].Card.Title, "Failed"));
                return;
            }

            var middle = candidates.Length / 2;
            await IncludeRootsAsync(candidates[..middle]);
            await IncludeRootsAsync(candidates[middle..]);
        }

        bool HasPathConflict(string path, HashSet<string> selected) =>
            selected.Contains(path) ||
            selected.Any(existing => existing.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(existing + "/", StringComparison.OrdinalIgnoreCase)) ||
            folders.Any(folder => folder.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                folder.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));

    }

    private async Task<RepositoryArtifact> ResolveStorageAsync(RepositoryFile file, CancellationToken ct)
    {
        if (file.StorageId is not { } id || id == Guid.Empty || file.StorageDomain is not { } domain ||
            domain == StorageAddress.UserDataDomain || !StorageAddress.IsPublicDomain(domain) || file.VersionLock is not null)
            throw new BuildRejectedException("build.storage_reference", file.Card.Title);
        using var client = clients.CreateClient("storage");
        using var response = await client.GetAsync($"files/{domain}/{id:D}/metadata", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new BuildRejectedException("build.storage_missing", file.Card.Title);
        response.EnsureSuccessStatusCode();
        var metadata = await response.Content.ReadFromJsonAsync<StorageFileMetadata>(ct);
        if (metadata?.FileName is not { } name || !RepositoryStorage.IsSafePathSegment(name))
            throw new BuildRejectedException("build.storage_name", file.Card.Title);
        var path = file.Folder.Length == 0 ? name : $"{file.Folder}/{name}";
        return new(path, file.Type, file.Card, RepositoryStorage.FileUrl(domain, id), metadata.Sha512, metadata.Sha512,
            null, null, [], file.ClientSide ?? true, file.ServerSide ?? true);
    }
}
