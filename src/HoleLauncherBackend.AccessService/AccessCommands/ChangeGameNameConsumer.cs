using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.AccessService.AccessCommands;

internal sealed class ChangeGameNameConsumer : IAccessCommandConsumer
{
    public AccessOperation Operation => AccessOperation.ChangeGameName;

    public Task ConsumeAsync(AccessCommandContext context, CancellationToken cancellation)
    {
        if (!context.Account.Permissions.Contains(Permissions.EditGameUser))
            return context.ReplyAsync(AccessOutcome.Forbidden, cancellation);
        if (context.Request.GameName is not { } name || !GameNameRules.IsValid(name))
            return context.ReplyAsync(AccessOutcome.InvalidRequest, cancellation);

        return context.ForwardOperationAsync(context.Request.CorrelationId, cancellation, gameName: name);
    }
}
