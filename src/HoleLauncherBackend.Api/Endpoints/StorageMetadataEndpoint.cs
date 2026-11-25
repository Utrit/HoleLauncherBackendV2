using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class StorageMetadataEndpoint : IStorageEndpoint
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/storage/{domain}/{fileId}/metadata",
            (string domain, string fileId, HttpContext context, StorageProxy storage) =>
                storage.ForwardAsync(domain, fileId, HttpMethod.Get, Permissions.StorageDownload, context, metadataResource: "metadata"));
        routes.MapGet("/api/storage/{domain}/{fileId}/sha512",
            (string domain, string fileId, HttpContext context, StorageProxy storage) =>
                storage.ForwardAsync(domain, fileId, HttpMethod.Get, Permissions.StorageDownload, context, metadataResource: "sha512"));
    }
}
