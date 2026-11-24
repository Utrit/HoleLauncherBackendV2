using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.AccessService.AccessCommands;

internal sealed class SessionConsumer : IAccessCommandConsumer
{
    public AccessOperation Operation => AccessOperation.Session;

    public Task ConsumeAsync(AccessCommandContext context, CancellationToken cancellation) =>
        context.ReplyAsync(AccessOutcome.Ok, cancellation, account: context.Account);
}
