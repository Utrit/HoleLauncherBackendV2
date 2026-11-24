using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class StatusEndpoint
{
    public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/status", GetStatusAsync);

    private static async Task<IResult> GetStatusAsync(
        HttpContext context, AccountManagementProxy proxy, CancellationToken cancellation)
    {
        context.Response.Headers.CacheControl = "no-store";
        return AccountManagementProxy.Result(await proxy.SendAsync(context, AccessOperation.Status, cancellation));
    }
}
