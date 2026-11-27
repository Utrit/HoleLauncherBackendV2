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

        var cache = mods.CreateCache(draft.Minecraft, draft.Loader, ct);
        var seenProjects = new HashSet<string>(StringComparer.Ordinal);
        var work = new List<(int Index, RepositoryFile File)>();
        for (var index = 0; index < draft.Files.Length; index++)
        {
            var file = draft.Files[index];
            if (file is null || file.Card is null || !Enum.IsDefined(file.Type) || file.Folder is null)
                errors.Enqueue(new("build.invalid_file", [file?.Card?.Title ?? ""]));
            else if (file.Folder.Length != 0 && !draft.Folders.Contains(file.Folder, StringComparer.OrdinalIgnoreCase))
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
        if (!validated.IsEmpty)
        {
            var roots = validated.OrderBy(entry => entry.Key).Select(entry => entry.Value).ToArray();
            RepositoryBuildError? dependencyError = null;
            try
            {
                var combined = await mods.ResolveAsync(new(draft.Minecraft, draft.Loader, roots), ct,
                    (key, args) => dependencyError = new(key, args), cache);
                if (combined is null)
                    errors.Enqueue(dependencyError ?? new("repository.dependencies_failed", []));
                else
                    resolved = combined;
            }
            catch (Exception error) when (!ct.IsCancellationRequested &&
                error is HttpRequestException or JsonException or OperationCanceledException)
            {
                errors.Enqueue(new("build.upstream", []));
            }
        }

        var files = resolved.Concat(storage.OrderBy(entry => entry.Key).Select(entry => entry.Value)).ToArray();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in files)
        {
            if (paths.Any(path => path.StartsWith(artifact.Path + "/", StringComparison.OrdinalIgnoreCase) ||
                    artifact.Path.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)) || !paths.Add(artifact.Path) ||
                draft.Folders.Any(folder => folder is not null && (folder.Equals(artifact.Path, StringComparison.OrdinalIgnoreCase) ||
                    folder.StartsWith(artifact.Path + "/", StringComparison.OrdinalIgnoreCase))))
                errors.Enqueue(new("build.path_conflict", [artifact.Path]));
        }
        if (!errors.IsEmpty)
            throw new BuildRejectedException(errors.ToArray());
        return new(id, draft, files, false);
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
