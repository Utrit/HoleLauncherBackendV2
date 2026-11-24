using HoleLauncherBackend.Contracts;
using static HoleLauncherBackend.Api.SessionCookie;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class AccountManagementProxy(KafkaAccessClient client, SessionLookup lookup)
{
    public Task<AccessReply> SendAsync(HttpContext context, AccessOperation operation, CancellationToken cancellation,
        Guid? accountId = null, string[]? permissions = null, bool? suppressed = null, Guid? operationId = null, string? storageDomain = null)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var sessionId))
            return Task.FromResult(new AccessReply(Guid.Empty, AccessOutcome.InvalidSession));

        return client.SendAsync(operation, lookup.Compute(sessionId), accountId, permissions, suppressed, operationId, storageDomain, cancellation);
    }

    public static IResult Result(AccessReply reply) => reply.Outcome switch
    {
        AccessOutcome.Accepted when reply.Operation is { } operation =>
            Results.Accepted($"/api/auth/operations/{operation.OperationId}", new OperationAccepted(operation.OperationId)),
        AccessOutcome.Ok when reply.Operation is not null => Results.Ok(reply.Operation),
        AccessOutcome.Ok when reply.Account is not null => Results.Ok(reply.Account),
        AccessOutcome.Ok when reply.Users is not null => Results.Ok(reply.Users),
        AccessOutcome.Ok when reply.Services is not null => Results.Ok<ServiceStatus[]>([new("Public API", "Online"), ..reply.Services]),
        AccessOutcome.NoContent => Results.NoContent(),
        AccessOutcome.InvalidSession => Results.Unauthorized(),
        AccessOutcome.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        AccessOutcome.InvalidRequest => Results.BadRequest(),
        AccessOutcome.NotFound => Results.NotFound(),
        _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
    };
}
