using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.AccessService.Endpoints;

internal sealed class RegisterEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/register", async (Credentials credentials, AccountStore accounts, CancellationToken cancellation) =>
        {
            if (!Utils.IsValid(credentials))
                return Results.BadRequest();

            var account = await accounts.RegisterAsync(credentials, cancellation);
            return account is null ? Results.Conflict() : Results.Ok(account);
        }).RequireRateLimiting("credentials");
    }
}
