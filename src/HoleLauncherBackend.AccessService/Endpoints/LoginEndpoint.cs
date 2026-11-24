using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.AccessService.Endpoints;

internal sealed class LoginEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/login", async (Credentials credentials, AccountStore accounts, CancellationToken cancellation) =>
        {
            if (!Utils.IsValid(credentials))
                return Results.Unauthorized();

            var session = await accounts.LoginAsync(credentials, cancellation);
            return session is null ? Results.Unauthorized() : Results.Ok(session);
        }).RequireRateLimiting("credentials");
    }
}
