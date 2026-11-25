using System.Net;
using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Repository;

internal sealed class ManifestBuilder(ModResolver mods, IHttpClientFactory clients)
{
    public async Task<RepositoryManifest> BuildAsync(Guid id, RepositoryDraft draft, CancellationToken ct)
    {
        if (id == Guid.Empty || draft.Folders is null || draft.Files is null)
            throw new BuildRejectedException("build.invalid_draft");
        if (draft.Name is not { } name || RepositoryStorage.DomainFor(name) is null)
            throw new BuildRejectedException("repository.upload_name");

        foreach (var folder in draft.Folders)
            if (!RepositoryStorage.IsSafeFolder(folder))
                throw new BuildRejectedException("build.invalid_folder", folder ?? "");

        foreach (var file in draft.Files)
        {
            if (file is null || file.Card is null || !Enum.IsDefined(file.Type) || file.Folder is null)
                throw new BuildRejectedException("build.invalid_file", file?.Card?.Title ?? "");
            if (file.Folder.Length != 0 && !draft.Folders.Contains(file.Folder, StringComparer.OrdinalIgnoreCase))
                throw new BuildRejectedException("build.missing_folder", file.Card.Title, file.Folder);
        }

        var modFiles = draft.Files.Where(file => file.ProjectId is not null).ToArray();
        BuildRejectedException dependencyError = new("repository.dependencies_failed");
        var resolved = await mods.ResolveAsync(new(draft.Minecraft, draft.Loader, modFiles), ct,
            (key, arguments) => dependencyError = new(key, arguments));
        if (resolved is null)
            throw dependencyError;

        var files = new List<RepositoryArtifact>(resolved);
        foreach (var file in draft.Files.Where(file => file.ProjectId is null))
        {
            files.Add(await ResolveStorageAsync(file, ct));
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in files)
        {
            if (paths.Any(path => path.StartsWith(artifact.Path + "/", StringComparison.OrdinalIgnoreCase) ||
                    artifact.Path.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)) || !paths.Add(artifact.Path) ||
                draft.Folders.Any(folder => folder.Equals(artifact.Path, StringComparison.OrdinalIgnoreCase) ||
                    folder.StartsWith(artifact.Path + "/", StringComparison.OrdinalIgnoreCase)))
                throw new BuildRejectedException("build.path_conflict", artifact.Path);
        }
        return new(id, draft, [.. files], false);
    }

    private async Task<RepositoryArtifact> ResolveStorageAsync(RepositoryFile file, CancellationToken ct)
    {
        if (file.StorageId is not { } id || id == Guid.Empty || file.StorageDomain is not { } domain ||
            domain == StorageAddress.UserDataDomain || !StorageAddress.IsPublicDomain(domain) || file.VersionLock is not null)
            throw new BuildRejectedException("build.storage_reference", file.Card.Title);
        using var response = await clients.CreateClient("storage").GetAsync($"files/{domain}/{id:D}/metadata", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new BuildRejectedException("build.storage_missing", file.Card.Title);
        response.EnsureSuccessStatusCode();
        var metadata = await response.Content.ReadFromJsonAsync<StorageFileMetadata>(ct);
        if (metadata?.FileName is not { } name || !RepositoryStorage.IsSafePathSegment(name))
            throw new BuildRejectedException("build.storage_name", file.Card.Title);
        return new(Join(file.Folder, name), file.Type, file.Card, RepositoryStorage.FileUrl(domain, id), metadata.Sha512, metadata.Sha512,
            null, null, [], file.ClientSide ?? true, file.ServerSide ?? true);
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";

}
