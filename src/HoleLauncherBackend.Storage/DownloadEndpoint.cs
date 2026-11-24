using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Storage;

internal sealed class DownloadEndpoint : IStorageEndpoint
{
    public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/files/{domain}/{fileId}", Download);

    private static IResult Download(string domain, string fileId, FileStore storage)
    {
        if (!StorageAddress.IsValidDomain(domain) || !StorageAddress.TryParseFileId(fileId, out var id))
            return Results.BadRequest();

        var stream = storage.Open(domain, id, "content");
        return stream is null ? Results.NotFound()
            : Results.File(stream, "application/octet-stream", id.ToString("D"), enableRangeProcessing: true);
    }
}
