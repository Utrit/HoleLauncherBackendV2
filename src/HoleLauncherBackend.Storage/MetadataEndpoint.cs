using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Storage;

internal sealed class MetadataEndpoint : IStorageEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/files/{domain}/{fileId}/metadata", GetMetadata);

    private static IResult GetMetadata(string domain, string fileId, FileStore storage)
    {
        if (!StorageAddress.IsValidDomain(domain) || !StorageAddress.TryParseFileId(fileId, out var id))
            return Results.BadRequest();

        var stream = storage.Open(domain, id, "metadata.json");
        return stream is null ? Results.NotFound() : Results.File(stream, "application/json");
    }
}
