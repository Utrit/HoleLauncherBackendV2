using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class UserSuppressionEndpoint : IAccountEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPut("/users/{accountId:guid}/suppression", UpdateAsync);

    private static async Task<IResult> UpdateAsync(Guid accountId, SuppressionChange change, HttpContext context,
        AccountManagementProxy proxy, CancellationToken cancellation)
    {
        if (change.Suppressed is not { } suppressed)
            return Results.BadRequest();

        var reply = await proxy.SendAsync(context, AccessOperation.ChangeSuppression, cancellation,
            accountId, suppressed: suppressed);
        return AccountManagementProxy.Result(reply);
    }
}
