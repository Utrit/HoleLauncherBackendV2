using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class StorageDownloadEndpoint : IStorageEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/storage/{domain}/{fileId}", (string domain, string fileId, HttpContext context, StorageProxy storage) =>
            storage.ForwardAsync(domain, fileId, HttpMethod.Get, Permissions.StorageDownload, context));
}
