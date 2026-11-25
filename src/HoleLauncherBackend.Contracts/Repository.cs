using System.Text.Json.Serialization;

namespace HoleLauncherBackend.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<RepositoryFileType>))]
public enum RepositoryFileType { Strong, Soft, Optional }

public sealed record ModCard(string Title, string Description, string? IconUrl);
public sealed record ModSearchHit(string ProjectId, ModCard Card);
public sealed record ModVersionChoice(string Id, string Number);
public sealed record RepositoryFile(string Folder, RepositoryFileType Type, ModCard Card,
    string? ProjectId = null, string? VersionLock = null, string? StorageDomain = null, Guid? StorageId = null, bool? ClientSide = null, bool? ServerSide = null, bool PlaceAtRoot = false)
{
    [JsonIgnore]
    public string EffectiveFolder => Folder.Length == 0 && ProjectId is not null && !PlaceAtRoot ? "mods" : Folder;
}
public sealed record RepositoryDraft(string Name, string Version, string Minecraft, string Loader,
    string LoaderVersion, string[] Folders, RepositoryFile[] Files, Guid[]? AcceptedOffers = null);
public sealed record RepositoryArtifact(string Path, RepositoryFileType Type, ModCard Card, string Url,
    string Sha512, string FileVersion, string? ProjectId, string? ModVersionId, string[] Dependencies, bool? ClientSide = null, bool? ServerSide = null);
public sealed record RepositoryManifest(Guid BuildId, RepositoryDraft Build, RepositoryArtifact[] Files, bool Test);
public sealed record RepositoryRelease(Guid BuildId, string Version, bool Test);
public sealed record RepositoryCatalog(RepositoryManifest[] Versions, RepositoryRelease[] Releases, Dictionary<Guid, long>? HighestVersions = null, RepositoryOffer[]? Offers = null);

public sealed record RepositoryChannel(string Version, bool Test, bool Enabled);
public sealed record RepositoryModResolution(string Minecraft, string Loader, RepositoryFile[] Files);
public sealed record RepositoryGameVersion(string Version, RepositoryLoader[] Loaders);
public sealed record RepositoryLoader(string Id, string Name, string[] Versions);

public static class RepositoryStorage
{
    public static string? DomainFor(string buildName)
    {
        var domain = string.Join('-', buildName.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return domain.Length != 0 && char.IsAsciiLetterOrDigit(domain[0]) &&
            !domain.StartsWith("userdata", StringComparison.Ordinal) && StorageAddress.IsPublicDomain(domain)
            ? domain : null;
    }

    public static bool IsSafeFolder(string? path) =>
        path is not null && path.Split('/').All(IsSafePathSegment);

    public static bool IsSafePathSegment(string segment) =>
        segment.Length is > 0 and <= 255 && !segment.EndsWith('.') && !segment.EndsWith(' ') &&
        !segment.Any(c => char.IsControl(c) || "<>:\"/\\|?*%".Contains(c)) &&
        !ReservedNames.Contains(segment.Split('.')[0]);

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string OfferDomain(Guid accountId) => $"offer-{accountId:N}";

    public static string FileUrl(string domain, Guid fileId) => $"/api/storage/{domain}/{fileId:D}";
}

public sealed record OfferTarget(Guid BuildId, string Version, string Name, string Minecraft, string Loader, bool Stage, bool Testing, string[]? Folders = null);
public sealed record OfferDraft(Guid BuildId, string Version, RepositoryFile[] Files, string? Description = null);
public sealed record OfferSubmission(Guid AccountId, string UserName, OfferDraft Draft);
public sealed record RepositoryOffer(Guid Id, Guid AccountId, string UserName, string Name, string Minecraft, string Loader, OfferFile[] Files,
    bool? Test = null, string? Description = null, string? ImageUrl = null, Guid? BuildId = null, string? BuildVersion = null);
public sealed record OfferFile(Guid Id, RepositoryFile File, bool? Accepted = null);
public sealed record OfferDecision(Guid FileId, bool Accept, Guid[]? FileIds = null);
public sealed record RepositoryBuildError(string Key, string[] Arguments);
