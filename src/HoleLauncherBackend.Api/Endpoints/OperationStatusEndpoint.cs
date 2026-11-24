using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class OperationStatusEndpoint : IAccountEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/operations/{operationId:guid}", async (Guid operationId, HttpContext context,
            AccountManagementProxy proxy, CancellationToken cancellation) =>
            AccountManagementProxy.Result(await proxy.SendAsync(context, AccessOperation.OperationStatus,
                cancellation, operationId: operationId)));
}
