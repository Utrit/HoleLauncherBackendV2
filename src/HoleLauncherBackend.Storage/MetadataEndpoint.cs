using System.Text.Json;
using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Storage;

internal sealed class MetadataEndpoint : IStorageEndpoint
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/files/{domain}/{fileId}/metadata", GetMetadata);
        routes.MapGet("/files/{domain}/{fileId}/sha512", GetHashAsync);
    }

    private static async Task<IResult> GetHashAsync(string domain, string fileId, FileStore storage,
        CancellationToken cancellation)
    {
        if (!StorageAddress.IsValidDomain(domain) || !StorageAddress.TryParseFileId(fileId, out var id))
            return Results.BadRequest();

        await using var stream = storage.Open(domain, id, "metadata.json");
        if (stream is null)
            return Results.NotFound();

        var metadata = await JsonSerializer.DeserializeAsync<StorageFileMetadata>(stream, Json, cancellation);
        return metadata is null ? Results.NotFound() : Results.Text(metadata.Sha512);
    }

    private static IResult GetMetadata(string domain, string fileId, FileStore storage)
    {
        if (!StorageAddress.IsValidDomain(domain) || !StorageAddress.TryParseFileId(fileId, out var id))
            return Results.BadRequest();

        var stream = storage.Open(domain, id, "metadata.json");
        return stream is null ? Results.NotFound() : Results.File(stream, "application/json");
    }
}
