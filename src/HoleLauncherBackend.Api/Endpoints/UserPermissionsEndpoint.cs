using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class UserPermissionsEndpoint : IAccountEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPut("/users/{accountId:guid}/permissions", async (Guid accountId, PermissionChange change, HttpContext context, AccountManagementProxy proxy, CancellationToken cancellation) =>
            AccountManagementProxy.Result(await proxy.SendAsync(context, AccessOperation.ChangePermissions, cancellation, accountId, change.Permissions)));
}
