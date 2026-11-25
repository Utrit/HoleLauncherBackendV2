using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Messaging;

namespace HoleLauncherBackend.AccessService.Endpoints;

internal sealed class RegisterEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/register", async (Credentials credentials, AccountStore accounts, KafkaBus bus, CancellationToken cancellation) =>
        {
            if (!Utils.IsValid(credentials) || !GameNameRules.IsValid(credentials.UserName))
                return Results.BadRequest();

            var account = await accounts.RegisterAsync(credentials, cancellation);
            if (account is null)
                return Results.Conflict();

            using var dispatch = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await bus.PublishAsync(KafkaBus.OperationsTopic, account.AccountId.ToString("N"),
                new OrchestrationMessage(Guid.Empty, account.AccountId, account.AccountId,
                    GameName: account.UserName, ProvisionGameAccount: true), dispatch.Token);
            return Results.Ok(account);
        }).RequireRateLimiting("credentials");
    }
}
