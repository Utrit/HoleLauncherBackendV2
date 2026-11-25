using System.Text.Json;
using System.Net;
using Microsoft.Extensions.Caching.Memory;
using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Repository;

internal sealed class ModrinthClient(HttpClient client, Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task<ModSearchHit[]> SearchAsync(string query, string minecraft, string loader, int offset, CancellationToken ct)
    {
        var facets = Encode(new[] { new[] { "project_type:mod" }, new[] { "versions:" + minecraft }, new[] { "categories:" + loader } });
        var page = await client.GetFromJsonAsync<SearchPage>(
            $"search?query={Uri.EscapeDataString(query)}&facets={facets}&offset={offset}&limit=20", Json, ct);
        return page?.Hits.Select(hit => new ModSearchHit(hit.ProjectId, new(hit.Title, hit.Description, hit.IconUrl))).ToArray() ?? [];
    }

    public Task<Version[]?> VersionsAsync(string id, string minecraft, string loader, CancellationToken ct) =>
        GetAsync<Version[]>(id, $"project/{id}/version?game_versions={Encode(new[] { minecraft })}&loaders={Encode(new[] { loader })}", ct);

    public Task<Project?> ProjectInfoAsync(string id, CancellationToken ct) =>
        cache.GetOrCreateAsync((nameof(Project), id), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
            return await GetAsync<Project>(id, $"project/{id}", ct);
        });

    public async Task<string?> VersionProjectAsync(string id, CancellationToken ct) =>
        (await GetAsync<Version>(id, $"version/{id}", ct))?.ProjectId;

    private async Task<T?> GetAsync<T>(string id, string path, CancellationToken ct) where T : class
    {
        if (id.Length is 0 or > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return null;
        using var response = await client.GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    public sealed record Project(string Id, string ProjectType, string Title, string Description, string? IconUrl, string? ClientSide, string? ServerSide)
    {
        public bool ClientSupported => ClientSide != "unsupported";
        public bool ServerSupported => ServerSide != "unsupported";
    }

    public async Task<string[]> GameVersionsAsync(CancellationToken ct)
    {
        var versions = await client.GetFromJsonAsync<GameVersion[]>("tag/game_version", Json, ct) ?? [];
        return [.. versions.Where(version => version.VersionType == "release" &&
                version.Version.Split('.').All(part => part.Length > 0 && part.All(char.IsAsciiDigit)))
            .OrderByDescending(version => version.Date).Select(version => version.Version)];
    }

    private sealed record GameVersion(string Version, string VersionType, DateTimeOffset Date);

    private static string Encode(string[][] values) => Uri.EscapeDataString(JsonSerializer.Serialize(values));
    private static string Encode(string[] values) => Uri.EscapeDataString(JsonSerializer.Serialize(values));

    private sealed record SearchPage(SearchHit[] Hits);
    private sealed record SearchHit(string ProjectId, string Title, string Description, string? IconUrl);
    public sealed record Version(string Id, string ProjectId, string VersionNumber, string VersionType, DateTimeOffset DatePublished,
        ModFile[] Files, Dependency[] Dependencies);
    public sealed record ModFile(string Filename, string Url, bool Primary, Hashes Hashes);
    public sealed record Hashes(string Sha512);
    public sealed record Dependency(string? ProjectId, string? VersionId, string DependencyType);
}
