using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.AccessService.AccessCommands;

internal sealed class LogoutConsumer(AccountStore accounts) : IAccessCommandConsumer
{
    public AccessOperation Operation => AccessOperation.Logout;

    public async Task ConsumeAsync(AccessCommandContext context, CancellationToken cancellation)
    {
        await accounts.RevokeSessionAsync(context.Request.SessionLookup, cancellation);
        await context.ReplyAsync(AccessOutcome.NoContent, cancellation);
    }
}
