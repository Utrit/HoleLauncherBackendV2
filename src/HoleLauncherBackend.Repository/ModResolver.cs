using System.Collections.Concurrent;
using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Repository;

internal sealed class ModResolver(ModrinthClient client)
{
    private const int MaxAttempts = 4_000;
    private const int MaxProjects = 256;

    public async Task<RepositoryArtifact[]?> ResolveAsync(RepositoryModResolution request, CancellationToken ct, Action<string, string[]>? report = null,
        ResolutionCache? shared = null, Action<string>? resolvingDependency = null)
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

        var cache = shared ?? CreateCache(request.Minecraft, request.Loader, ct);
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
            var metadata = await cache.ProjectAsync(project);
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

        async Task<string> ProjectLabelAsync(string project) =>
            roots.GetValueOrDefault(project)?.Card.Title ?? (await cache.ProjectAsync(project))?.Title ?? project;

        async Task<string> VersionLabelAsync(string? id) =>
            id is null ? "—" : (await cache.VersionAsync(id))?.VersionNumber ?? id;

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
                if (report is not null && selectedDepth >= deepestFailure)
                    Fail("build.dependency_version_conflict", await ProjectLabelAsync(next.Project),
                        await VersionLabelAsync(next.Version), already.Version.VersionNumber, next.RequiredBy ?? "—");
                return null;
            }

            if (chosen.Count == MaxProjects)
                return null;

            var candidates = await cache.VersionsAsync(next.Project) ?? [];

            var compatible = candidates.Where(v => next.Version is null || next.Version == v.Id)
                .OrderByDescending(v => v.VersionType == "release").ThenByDescending(v => v.DatePublished).ToArray();
            if (compatible.Length == 0 && report is not null && selectedDepth >= deepestFailure)
                Fail("build.mod_version_missing", await ProjectLabelAsync(next.Project), await VersionLabelAsync(next.Version),
                    request.Minecraft, request.Loader, next.RequiredBy ?? "—");
            foreach (var candidate in compatible)
            {
                var dependencies = new List<Requirement>();
                var valid = true;
                foreach (var dep in candidate.Dependencies.Where(dep => dep.DependencyType == "required"))
                {
                    var dependencyProject = dep.ProjectId;
                    if (dependencyProject is null && dep.VersionId is { } versionId)
                    {
                        dependencyProject = (await cache.VersionAsync(versionId))?.ProjectId;
                    }

                    if (dependencyProject is null)
                    {
                        valid = false;
                        break;
                    }
                    var title = await ProjectLabelAsync(next.Project);
                    if (resolvingDependency is not null)
                        resolvingDependency(await ProjectLabelAsync(dependencyProject));
                    dependencies.Add(new(dependencyProject, dep.VersionId, title));
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

    public ResolutionCache CreateCache(string minecraft, string loader, CancellationToken ct) =>
        new(client, minecraft, loader, ct);

    internal sealed class ResolutionCache(ModrinthClient client, string minecraft, string loader, CancellationToken ct)
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<ModrinthClient.Version[]?>>> candidates = new();
        private readonly ConcurrentDictionary<string, Lazy<Task<ModrinthClient.Project?>>> projects = new();
        private readonly ConcurrentDictionary<string, Lazy<Task<ModrinthClient.Version?>>> versions = new();

        public Task<ModrinthClient.Version[]?> VersionsAsync(string id) =>
            Read(candidates, id, key => client.VersionsAsync(key, minecraft, loader, ct));

        public Task<ModrinthClient.Project?> ProjectAsync(string id) =>
            Read(projects, id, key => client.ProjectInfoAsync(key, ct));

        public Task<ModrinthClient.Version?> VersionAsync(string id) =>
            Read(versions, id, key => client.VersionInfoAsync(key, ct));

        private static Task<T?> Read<T>(ConcurrentDictionary<string, Lazy<Task<T?>>> cache, string id,
            Func<string, Task<T?>> load) where T : class =>
            cache.GetOrAdd(id, key => new Lazy<Task<T?>>(() => load(key))).Value;
    }

    private sealed record Requirement(string Project, string? Version, string? RequiredBy = null);
    private sealed record Choice(ModrinthClient.Version Version, Requirement[] Dependencies);
}
