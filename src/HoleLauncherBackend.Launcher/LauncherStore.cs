using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Launcher;

internal sealed class LauncherImportException(string stage, string failure, Exception cause) : Exception(failure, cause)
{
    public string Stage { get; } = stage;
    public string Failure { get; } = failure;
}

internal sealed partial class LauncherStore(IHttpClientFactory clients, IConfiguration configuration, LauncherStorage storage, ILogger<LauncherStore> logger)
{
    private const long MaxFileSize = 160L * 1024 * 1024;
    private const int MaxAddressBytes = 2048;
    private const int MaxRedirects = 4;
    private static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(15);
    private static ReadOnlySpan<byte> Footer => "HOLELAUNCHER-BACKEND-V1"u8;
    private static ReadOnlySpan<byte> FingerprintMarker => "HOLELAUNCHER-SHA512-V1"u8;
    private readonly string root = configuration["Launcher:Root"] ?? "/data/launcher";
    private readonly SemaphoreSlim import = new(1, 1);
    private readonly object published = new();
    private const string WindowsName = "HoleLauncher.exe";
    private const string LinuxName = "HoleLauncher";
    private const string ArchiveName = "HoleLauncher-linux-x64.tar.gz";

    public LauncherRelease? Current
    {
        get { lock (published) return ReadPublished()?.Release; }
    }

    public (Guid FileId, string Fingerprint)? FindArtifact(bool linux)
    {
        lock (published)
        {
            if (ReadPublished() is not { } state)
                return null;
            var artifact = linux ? state.Linux : state.Windows;
            return (artifact.Id, artifact.Fingerprint);
        }
    }

    public async Task<LauncherRelease?> ImportAsync(string input, Guid uploader, CancellationToken cancellation)
    {
        var repository = input?.Trim().TrimEnd('/') ?? "";
        const string github = "https://github.com/";
        if (repository.StartsWith(github, StringComparison.OrdinalIgnoreCase))
            repository = repository[github.Length..];
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repository = repository[..^4];
        if (uploader == Guid.Empty || !RepositoryPattern().IsMatch(repository))
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(ImportTimeout);
        var token = timeout.Token;
        var stage = "configuration";
        await import.WaitAsync(token);
        try
        {
            var address = PublicAddress(configuration["Launcher:PublicOrigin"]);
            stage = "recovery";
            if (!await CleanupPendingAsync())
                throw new IOException("Previous launcher publication cleanup is pending.");

            stage = "github";
            using var http = clients.CreateClient("github");
            var githubToken = configuration["Launcher:GitHubToken"];
            using var lookup = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
            AuthorizeGitHub(lookup, githubToken);
            using var response = await http.SendAsync(lookup, token);
            response.EnsureSuccessStatusCode();
            var release = await response.Content.ReadFromJsonAsync<GitRelease>(token)
                ?? throw new InvalidDataException("Missing GitHub release.");
            var windows = FindAsset(release, "HoleLauncher-win-x64.exe");
            var linux = FindAsset(release, ArchiveName);
            var id = Guid.NewGuid();
            var folder = Directory.CreateTempSubdirectory("holelauncher-").FullName;
            var windowsId = Guid.NewGuid();
            var linuxId = Guid.NewGuid();
            Directory.CreateDirectory(root);
            var marker = Path.Combine(root, id.ToString("N") + ".json");
            try
            {
                await DownloadAsync(http, repository, windows, githubToken, Path.Combine(folder, WindowsName), token);
                var source = Path.Combine(folder, "download.tar.gz");
                await DownloadAsync(http, repository, linux, githubToken, source, token);
                stage = "patch";
                await UnpackLinuxAsync(source, Path.Combine(folder, LinuxName), token);
                File.Delete(source);
                var windowsPath = Path.Combine(folder, WindowsName);
                var windowsHash = await StampAsync(windowsPath, "MZ"u8.ToArray(), address, token);
                var linuxHash = await StampAsync(Path.Combine(folder, LinuxName), new byte[] { 127, 69, 76, 70 }, address, token);
                await PackLinuxAsync(folder, token);

                var state = new Publication(new LauncherRelease(repository, release.Tag),
                    new Artifact(windowsId, windowsHash), new Artifact(linuxId, linuxHash));
                stage = "metadata";
                var pending = new PendingPublication(state, ReadPublished());
                await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(pending), token);
                File.Move(marker, PendingPath);

                stage = "storage";
                await Task.WhenAll(storage.UploadAsync(windowsId, uploader, windowsPath, token),
                    storage.UploadAsync(linuxId, uploader, Path.Combine(folder, ArchiveName), token));
                stage = "metadata";
                await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(state), token);
                lock (published)
                {
                    token.ThrowIfCancellationRequested();
                    File.Move(marker, StatePath, overwrite: true);
                }
                return state.Release;
            }
            finally
            {
                try { await CleanupPendingAsync(); }
                finally
                {
                    File.Delete(marker);
                    Clear(folder);
                }
            }
        }
        catch (Exception error) when (error is Confluent.Kafka.KafkaException or HttpRequestException or IOException or
            UnauthorizedAccessException or InvalidDataException or JsonException ||
            error is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            var failure = error is Confluent.Kafka.KafkaException kafka
                ? kafka.Error.Code.ToString() : error.GetType().Name;
            logger.LogWarning("Launcher import failed during {Stage} ({Failure}): {Reason}", stage, failure, error.Message);
            throw new LauncherImportException(stage, failure, error);
        }
        finally { import.Release(); }
    }

    private string StatePath => Path.Combine(root, "current.json");
    private string PendingPath => Path.Combine(root, "pending.json");

    public async Task RecoverAsync(CancellationToken cancellation)
    {
        await import.WaitAsync(cancellation);
        try { await CleanupPendingAsync(); }
        finally { import.Release(); }
    }

    private async Task<bool> CleanupPendingAsync()
    {
        if (!File.Exists(PendingPath))
            return true;

        var pending = JsonSerializer.Deserialize<PendingPublication>(await File.ReadAllTextAsync(PendingPath))
            ?? throw new InvalidDataException("Missing pending launcher publication.");
        var obsolete = ReadPublished()?.Windows.Id == pending.Next.Windows.Id ? pending.Previous : pending.Next;
        if (obsolete is not null)
        {
            var discarded = await Task.WhenAll(storage.DiscardAsync(obsolete.Windows.Id), storage.DiscardAsync(obsolete.Linux.Id));
            if (!discarded.All(sent => sent))
                return false;
        }

        File.Delete(PendingPath);
        return true;
    }

    private Publication? ReadPublished() => File.Exists(StatePath) &&
        JsonSerializer.Deserialize<Publication>(File.ReadAllText(StatePath)) is { Windows: not null, Linux: not null } state
            ? state : null;

    private static byte[] PublicAddress(string? origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Invalid launcher public origin.");
        var bytes = Encoding.UTF8.GetBytes(uri.AbsoluteUri);
        return bytes.Length <= MaxAddressBytes ? bytes : throw new InvalidDataException("Public origin is too long.");
    }

    private static Asset FindAsset(GitRelease release, string name) =>
        release.Assets.FirstOrDefault(asset => asset.Name == name && asset.Size is > 0 and <= MaxFileSize)
        ?? throw new InvalidDataException("Missing launcher asset.");

    private static void AuthorizeGitHub(HttpRequestMessage request, string? token)
    {
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new("Bearer", token);
    }

    private static async Task DownloadAsync(HttpClient http, string repository, Asset asset, string? githubToken,
        string path, CancellationToken token)
    {
        var url = string.IsNullOrWhiteSpace(githubToken) ? new Uri(asset.Download)
            : new Uri($"https://api.github.com/repos/{repository}/releases/assets/{asset.Id}");
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || url.UserInfo.Length != 0 ||
                url.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com") &&
                !(hop == 0 && url.Host == "api.github.com" && !string.IsNullOrWhiteSpace(githubToken)))
                throw new InvalidDataException("Invalid asset URL.");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (url.Host == "api.github.com")
            {
                AuthorizeGitHub(request, githubToken);
                request.Headers.Accept.ParseAdd("application/octet-stream");
            }
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } redirect)
            {
                url = new Uri(url, redirect);
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxFileSize)
                throw new InvalidDataException("Launcher asset is too large.");
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = File.Create(path);
            await CopyBoundedAsync(input, output, token);
            return;
        }
        throw new InvalidDataException("Too many redirects.");
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        long size = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) != 0)
        {
            size += count;
            if (size > MaxFileSize)
                throw new InvalidDataException("Launcher asset is too large.");
            await destination.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }

    private static async Task UnpackLinuxAsync(string source, string destination, CancellationToken token)
    {
        await using var archive = File.OpenRead(source);
        await using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        await using var reader = new TarReader(gzip);
        var entry = await reader.GetNextEntryAsync(cancellationToken: token);
        if (entry?.Name != LinuxName || entry.EntryType != TarEntryType.RegularFile ||
            entry.Length is <= 0 or > MaxFileSize || entry.DataStream is not { } binary)
            throw new InvalidDataException("Unexpected Linux launcher archive.");
        await using var output = File.Create(destination);
        await CopyBoundedAsync(binary, output, token);
    }

    private static async Task PackLinuxAsync(string folder, CancellationToken token)
    {
        await using var output = File.Create(Path.Combine(folder, ArchiveName));
        await using var gzip = new GZipStream(output, CompressionLevel.Fastest);
        await using var tar = new TarWriter(gzip);
        await using var executable = File.OpenRead(Path.Combine(folder, LinuxName));
        var entry = new PaxTarEntry(TarEntryType.RegularFile, LinuxName)
        {
            DataStream = executable,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
        };
        await tar.WriteEntryAsync(entry, token);
    }

    private static async Task<string> StampAsync(string path, byte[] format, byte[] address, CancellationToken token)
    {
        await using var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
        var signature = new byte[format.Length];
        await file.ReadExactlyAsync(signature, token);
        if (!signature.AsSpan().SequenceEqual(format))
            throw new InvalidDataException("Invalid launcher executable format.");

        var footer = new byte[sizeof(int) + Footer.Length];
        BinaryPrimitives.WriteInt32LittleEndian(footer, address.Length);
        Footer.CopyTo(footer.AsSpan(sizeof(int)));
        var executableLength = file.Seek(0, SeekOrigin.End);
        await file.WriteAsync(address, token);
        await file.WriteAsync(footer, token);
        file.Position = 0;
        var fingerprint = await SHA512.HashDataAsync(file, token);

        file.Position = executableLength;
        await file.WriteAsync(FingerprintMarker.ToArray(), token);
        await file.WriteAsync(fingerprint, token);
        await file.WriteAsync(address, token);
        await file.WriteAsync(footer, token);
        return Convert.ToHexStringLower(fingerprint);
    }

    private static void Clear(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    private sealed record PendingPublication(Publication Next, Publication? Previous);
    private sealed record Artifact(Guid Id, string Fingerprint);
    private sealed record Publication(LauncherRelease Release, Artifact Windows, Artifact Linux);
    private sealed record GitRelease([property: System.Text.Json.Serialization.JsonPropertyName("tag_name")] string Tag, Asset[] Assets);
    private sealed record Asset(long Id, string Name, long Size,
        [property: System.Text.Json.Serialization.JsonPropertyName("browser_download_url")] string Download);
}
