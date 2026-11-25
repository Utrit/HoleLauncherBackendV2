namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class EndpointMapper(HealthEndpoint healthEndpoint, StatusEndpoint statusEndpoint, DraslProxyEndpoint draslProxy, Shared.EndpointMapper<IEndpointRouteBuilder, IAccountEndpoint> accountEndpoints,
    Shared.EndpointMapper<IEndpointRouteBuilder, IStorageEndpoint> storageEndpoints)
{
    public void Map(IEndpointRouteBuilder routes)
    {
        healthEndpoint.Map(routes);
        draslProxy.Map(routes);
        statusEndpoint.Map(routes);
        storageEndpoints.Map(routes);
        routes.MapFallback("/api/storage/{**path}", () => Results.BadRequest());

        var group = routes.MapGroup("/api/auth").AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try
            {
                return await next(context);
            }
            catch (HttpRequestException)
            {
                return Results.StatusCode(503);
            }
            catch (OperationCanceledException) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                return Results.StatusCode(503);
            }
        });

        accountEndpoints.Map(group);
    }
}
