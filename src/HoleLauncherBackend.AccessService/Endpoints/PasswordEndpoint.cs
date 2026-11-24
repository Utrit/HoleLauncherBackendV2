using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.AccessService.Endpoints;

internal sealed class PasswordEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/password", ChangeAsync).RequireRateLimiting("credentials");

    private static async Task<IResult> ChangeAsync(ChangePasswordRequest request, AccountStore accounts, CancellationToken cancellation)
    {
        var session = await accounts.FindSessionAsync(request.SessionLookup, cancellation);
        if (session is null || !session.IsValidAt(DateTimeOffset.UtcNow))
            return Results.Unauthorized();
        if (request.Change.NewPassword is null)
            return Results.BadRequest();

        return await accounts.ChangePasswordAsync(session.Account.AccountId, request.SessionLookup, request.Change.NewPassword, cancellation)
            ? Results.NoContent()
            : Results.Unauthorized();
    }
}
