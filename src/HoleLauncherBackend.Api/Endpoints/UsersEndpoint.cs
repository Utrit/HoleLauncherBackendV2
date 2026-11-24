using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class UsersEndpoint : IAccountEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/users", async (HttpContext context, AccountManagementProxy proxy, CancellationToken cancellation) =>
            AccountManagementProxy.Result(await proxy.SendAsync(context, AccessOperation.Users, cancellation)));
}
