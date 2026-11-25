using System.Xml.Linq;
using HoleLauncherBackend.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace HoleLauncherBackend.Repository;

internal sealed class LoaderCatalog(HttpClient http, IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private const string ForgeMetadata = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
    private const string NeoForgeMetadata = "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";

    public async Task<RepositoryGameVersion[]> ListAsync(ModrinthClient modrinth, CancellationToken ct)
    {
        return (await cache.GetOrCreateAsync(nameof(LoaderCatalog), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Lifetime;
            var games = modrinth.GameVersionsAsync(ct);
            var forge = ReadVersionsAsync(ForgeMetadata, ct);
            var neoForge = ReadVersionsAsync(NeoForgeMetadata, ct);
            await Task.WhenAll(games, forge, neoForge);

            var forgeVersions = (await forge).ToLookup(version => version.Split('-', 2)[0]);
            var neoVersions = (await neoForge).ToLookup(version => string.Join('.', version.Split('.').Take(2)));
            var supported = new List<RepositoryGameVersion>();
            foreach (var game in await games)
            {
                var parts = game.Split('.');
                var neoKey = parts[0] == "1" && parts.Length >= 2
                    ? $"{parts[1]}.{(parts.Length == 3 ? parts[2] : "0")}"
                    : string.Join('.', parts.Take(2));
                var supportedLoaders = new List<RepositoryLoader>();
                var forgeForGame = forgeVersions[game]
                    .Select(version => version[(game.Length + 1)..]).Reverse().ToArray();
                if (forgeForGame.Length > 0)
                    supportedLoaders.Add(new("forge", "Forge", forgeForGame));
                var neoForGame = neoVersions[neoKey].Reverse().ToArray();
                if (neoForGame.Length > 0)
                    supportedLoaders.Add(new("neoforge", "NeoForge", neoForGame));
                if (supportedLoaders.Count > 0)
                    supported.Add(new(game, [.. supportedLoaders]));
            }
            return supported.ToArray();
        }))!;
    }

    private async Task<string[]> ReadVersionsAsync(string endpoint, CancellationToken ct)
    {
        await using var stream = await http.GetStreamAsync(endpoint, ct);
        var xml = await XDocument.LoadAsync(stream, LoadOptions.None, ct);
        return [.. xml.Descendants("version").Select(item => item.Value)];
    }
}
