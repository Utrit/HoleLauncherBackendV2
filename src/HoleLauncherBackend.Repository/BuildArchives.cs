using System.IO.Compression;
using System.Security.Cryptography;
using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Repository;

internal sealed class BuildArchives(IHttpClientFactory clients, ModrinthClient mods)
{
    public async Task<Stream> CreateAsync(RepositoryManifest manifest, bool server, CancellationToken ct)
    {
        var zipStream = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite,
            FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var folder in manifest.Build.Folders.Distinct(StringComparer.OrdinalIgnoreCase))
                    zip.CreateEntry(folder + "/");

                foreach (var artifact in manifest.Files)
                {
                    var allowed = server ? artifact.ServerSide : artifact.ClientSide;
                    if (allowed is null && artifact.ProjectId is { } project)
                    {
                        var metadata = await mods.ProjectInfoAsync(project, ct)
                            ?? throw new InvalidDataException($"Missing metadata for {project}.");
                        allowed = server ? metadata.ServerSupported : metadata.ClientSupported;
                    }
                    if (allowed == false)
                        continue;

                    using var response = await OpenFileAsync(artifact, ct);
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(ct);
                    using var hash = SHA512.Create();
                    await using var verified = new CryptoStream(source, hash, CryptoStreamMode.Read, leaveOpen: true);
                    await using var entry = zip.CreateEntry(artifact.Path, CompressionLevel.Fastest).Open();
                    await verified.CopyToAsync(entry, ct);
                    if (!string.Equals(Convert.ToHexString(hash.Hash!), artifact.Sha512, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Invalid content hash for {artifact.Path}.");
                }
            }
            zipStream.Position = 0;
            return zipStream;
        }
        catch
        {
            await zipStream.DisposeAsync();
            throw;
        }
    }

    private Task<HttpResponseMessage> OpenFileAsync(RepositoryArtifact file, CancellationToken ct)
    {
        if (ModrinthAssets.IsCdnUrl(file.Url))
            return clients.CreateClient("modrinth-downloads").GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct);

        var segments = file.Url.Split('/');
        if (segments.Length != 5 || segments[0].Length != 0 || segments[1] != "api" || segments[2] != "storage" ||
            !StorageAddress.IsPublicDomain(segments[3]) || segments[3] == StorageAddress.UserDataDomain ||
            !StorageAddress.TryParseFileId(segments[4], out var id))
            throw new InvalidDataException("Invalid Storage artifact URL.");

        return clients.CreateClient("storage").GetAsync($"files/{segments[3]}/{id:D}", HttpCompletionOption.ResponseHeadersRead, ct);
    }
}
