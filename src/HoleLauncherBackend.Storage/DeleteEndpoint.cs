using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Storage;

internal sealed class DeleteEndpoint : IStorageEndpoint
{
    public void Map(IEndpointRouteBuilder routes) => routes.MapDelete("/files/{domain}/{fileId}", Delete);

    private static IResult Delete(string domain, string fileId, FileStore storage)
    {
        if (!StorageAddress.IsValidDomain(domain) || !StorageAddress.TryParseFileId(fileId, out var id))
            return Results.BadRequest();

        if (domain == StorageAddress.LauncherDomain)
            return Results.Conflict();

        storage.Delete(domain, id);
        return Results.NoContent();
    }
}
