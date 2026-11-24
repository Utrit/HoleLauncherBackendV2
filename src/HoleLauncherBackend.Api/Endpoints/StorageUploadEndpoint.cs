namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class StorageUploadEndpoint : IStorageEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/storage/{domain}", (string domain, HttpContext context, StorageProxy storage) =>
            storage.UploadAsync(domain, context));
}
