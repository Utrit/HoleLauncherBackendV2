using System.Text.Json.Serialization;

namespace HoleLauncherBackend.Contracts;

public static class StorageAddress
{
    public const string UserDataDomain = "userdata";
    public const string LauncherDomain = "launcher";
    private const string UserDataPrefix = "userdata-";
    public const int MaxOfferImageBytes = 1024 * 1024;
    public const long MaxUploadBytes = 1024L * 1024 * 1024;

    public static bool TryParseFileId(string value, out Guid id) =>
        (value.Length == 36 && Guid.TryParseExact(value, "D", out id)) ||
        Guid.TryParseExact(value.Length == 32 ? value : null, "N", out id);

    public static bool HasUnsafePath(string? rawTarget, string prefix)
    {
        var path = rawTarget.AsSpan();
        var query = path.IndexOf('?');
        if (query >= 0)
            path = path[..query];

        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            path[prefix.Length..].ContainsAny('.', '%', '\\');
    }

    public static bool IsPublicDomain(string domain) =>
        IsValidDomain(domain) && !IsPrivateUserDataDomain(domain);

    public static bool IsStagingDomainFor(string domain, Guid uploader) =>
        domain != UserDataDomain &&
        (IsPublicDomain(domain) || domain == ResolveDomain(UserDataDomain, uploader));

    public static string ResolveDomain(string domain, Guid accountId) =>
        domain == UserDataDomain ? $"{UserDataPrefix}{accountId:N}" : domain;

    public static bool IsPrivateUserDataDomain(string domain) => TryGetUserDataOwner(domain, out _);

    public static bool TryGetUserDataOwner(string domain, out Guid owner) =>
        Guid.TryParseExact(domain.StartsWith(UserDataPrefix, StringComparison.Ordinal)
            ? domain.AsSpan(UserDataPrefix.Length) : default, "N", out owner);

    public static bool IsValidDomain(string domain) =>
        domain.Length is > 0 and <= 64 && domain.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');
}

public sealed record StorageUploadAccepted(Guid OperationId, string Domain, Guid FileId);
public sealed record StorageTransfer(Guid OperationId, bool Complete, bool TimedOut = false, long BytesWritten = 0, DateTimeOffset? ProgressAt = null);
public sealed record StorageDecision(Guid OperationId, string Domain, bool Publish, DateTimeOffset ExpiresAt);
[JsonConverter(typeof(JsonStringEnumConverter<StorageStep>))]
public enum StorageStep { Publish, Revert }
[JsonConverter(typeof(JsonStringEnumConverter<ResultStatus>))]
public enum ResultStatus { Failed, Complete }
public sealed record StorageResult(Guid OperationId, StorageStep Step, ResultStatus Status, string? ErrorMessage = null);

public sealed record StorageFileMetadata(string Domain, Guid FileId, Guid UploadedBy, DateTimeOffset UploadedAt, long Size, string Sha512, string? FileName = null);

public sealed record StoredFile(string Domain, StorageFileMetadata Metadata, string? Label = null);
public sealed record AdminAccountName(Guid AccountId, string UserName);
public sealed record HistoryCleanupRequest(Guid? AccountId, bool All = false);
public sealed record HistoryCleanupResult(int NameEntries, int TextureEntries);
