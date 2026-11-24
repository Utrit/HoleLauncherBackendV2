using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class MeEndpoint : IAccountEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/me", async (HttpContext context, AccountManagementProxy proxy, CancellationToken cancellation) =>
            AccountManagementProxy.Result(await proxy.SendAsync(context, AccessOperation.Session, cancellation)));
}
