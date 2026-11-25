using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Repository;

internal sealed class ModResolver(ModrinthClient client)
{
    private const int MaxAttempts = 4_000;
    private const int MaxProjects = 256;

    public async Task<RepositoryArtifact[]?> ResolveAsync(RepositoryModResolution request, CancellationToken ct, Action<string, string[]>? report = null)
    {
        if (string.IsNullOrWhiteSpace(request.Minecraft) || string.IsNullOrWhiteSpace(request.Loader) || request.Files is null || request.Files.Length > MaxProjects)
            return null;

        var roots = new Dictionary<string, RepositoryFile>(StringComparer.Ordinal);
        foreach (var file in request.Files)
        {
            if (file?.ProjectId is not { } project || file.Card is null || file.Folder is null ||
                file.StorageId.HasValue || file.StorageDomain is not null || !roots.TryAdd(project, file))
                return null;
        }

        var versions = new Dictionary<string, ModrinthClient.Version[]>();
        var versionProjects = new Dictionary<string, string?>();
        var required = roots.Select(root => new Requirement(root.Key, root.Value.VersionLock))
            .OrderByDescending(root => root.Version is not null).ToArray();
        var attempts = 0;
        var selectedDepth = 0;
        var deepestFailure = -1;
        void Fail(string key, params string[] arguments)
        {
            if (selectedDepth < deepestFailure)
                return;
            deepestFailure = selectedDepth;
            report?.Invoke(key, arguments);
        }
        var selected = await SolveAsync(required, new());
        if (selected is null)
            return null;

        var mandatory = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots.Where(root => root.Value.Type != RepositoryFileType.Optional))
            MarkMandatory(root.Key);

        var artifacts = new List<RepositoryArtifact>();
        foreach (var (project, choice) in selected)
        {
            var file = choice.Version.Files.FirstOrDefault(file => file.Primary) ?? choice.Version.Files.FirstOrDefault();
            if (file is null || !RepositoryStorage.IsSafePathSegment(file.Filename) || !ModrinthAssets.IsCdnUrl(file.Url) ||
                file.Hashes?.Sha512 is not { Length: 128 } sha512 || !sha512.All(char.IsAsciiHexDigit))
                return null;

            var root = roots.GetValueOrDefault(project);
            var metadata = await client.ProjectInfoAsync(project, ct);
            if (metadata is null)
                return null;
            var card = root?.Card ?? new(metadata.Title, metadata.Description, metadata.IconUrl);
            var folder = root?.EffectiveFolder ?? "mods";
            var type = root?.Type ?? (mandatory.Contains(project) ? RepositoryFileType.Strong : RepositoryFileType.Optional);
            var path = folder.Length == 0 ? file.Filename : $"{folder}/{file.Filename}";
            artifacts.Add(new(path, type, card, file.Url, sha512,
                choice.Version.VersionNumber, project, choice.Version.Id, [.. choice.Dependencies.Select(dep => dep.Project).Distinct()],
                root?.ClientSide ?? metadata.ClientSupported, root?.ServerSide ?? metadata.ServerSupported));
        }
        return [.. artifacts];

        string ProjectLabel(string project) => roots.GetValueOrDefault(project)?.Card.Title ?? project;

        void MarkMandatory(string project)
        {
            if (!mandatory.Add(project))
                return;
            foreach (var dependency in selected[project].Dependencies)
                MarkMandatory(dependency.Project);
        }

        async Task<Dictionary<string, Choice>?> SolveAsync(Requirement[] pending, Dictionary<string, Choice> chosen)
        {
            ct.ThrowIfCancellationRequested();
            selectedDepth = chosen.Count;
            if (pending.Length == 0)
                return chosen;
            if (++attempts > MaxAttempts)
                return null;

            var next = pending[0];
            var rest = pending[1..];
            if (chosen.TryGetValue(next.Project, out var already))
            {
                if (next.Version is null || already.Version.Id == next.Version)
                    return await SolveAsync(rest, chosen);
                Fail("build.dependency_version_conflict", ProjectLabel(next.Project), next.Version, already.Version.Id, next.RequiredBy ?? "");
                return null;
            }

            if (chosen.Count == MaxProjects)
                return null;

            if (!versions.TryGetValue(next.Project, out var candidates))
            {
                candidates = await client.VersionsAsync(next.Project, request.Minecraft, request.Loader, ct) ?? [];
                versions.Add(next.Project, candidates);
            }

            var compatible = candidates.Where(v => next.Version is null || next.Version == v.Id)
                .OrderByDescending(v => v.VersionType == "release").ThenByDescending(v => v.DatePublished).ToArray();
            if (compatible.Length == 0)
                Fail("build.mod_version_missing", ProjectLabel(next.Project), next.Version ?? "—", request.Minecraft, request.Loader, next.RequiredBy ?? "—");
            foreach (var candidate in compatible)
            {
                var dependencies = new List<Requirement>();
                var valid = true;
                foreach (var dep in candidate.Dependencies.Where(dep => dep.DependencyType == "required"))
                {
                    var dependencyProject = dep.ProjectId;
                    if (dependencyProject is null && dep.VersionId is { } versionId)
                    {
                        if (!versionProjects.TryGetValue(versionId, out dependencyProject))
                        {
                            dependencyProject = await client.VersionProjectAsync(versionId, ct);
                            versionProjects.Add(versionId, dependencyProject);
                        }
                    }

                    if (dependencyProject is null)
                    {
                        valid = false;
                        break;
                    }
                    dependencies.Add(new(dependencyProject, dep.VersionId, ProjectLabel(next.Project)));
                }

                if (!valid)
                    continue;

                chosen.Add(next.Project, new(candidate, [.. dependencies]));
                var solved = await SolveAsync([.. rest, .. dependencies], chosen);
                if (solved is not null)
                    return solved;
                chosen.Remove(next.Project);
            }
            return null;
        }
    }

    private sealed record Requirement(string Project, string? Version, string? RequiredBy = null);
    private sealed record Choice(ModrinthClient.Version Version, Requirement[] Dependencies);
}
