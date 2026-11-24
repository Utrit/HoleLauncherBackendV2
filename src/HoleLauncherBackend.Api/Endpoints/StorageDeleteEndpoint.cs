using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class StorageDeleteEndpoint : IStorageEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/api/storage/{domain}/{fileId}", (string domain, string fileId, HttpContext context, StorageProxy storage) =>
            storage.ForwardAsync(domain, fileId, HttpMethod.Delete, Permissions.StorageDelete, context));
}
