using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.AccessService.AccessCommands;

internal sealed class ChangeSuppressionConsumer : IAccessCommandConsumer
{
    public AccessOperation Operation => AccessOperation.ChangeSuppression;
    public string? RequiredPermission => Permissions.EditUsers;

    public Task ConsumeAsync(AccessCommandContext context, CancellationToken cancellation)
    {
        var request = context.Request;
        if (request.AccountId is not { } accountId || request.Suppressed is not { } suppressed)
            return context.ReplyAsync(AccessOutcome.InvalidRequest, cancellation);

        var id = request.CorrelationId;
        var command = new AccountOperationCommand(id, context.Account.AccountId, accountId,
            AccountMutation.ChangeSuppression, null, suppressed);
        return context.ForwardOperationAsync(id, cancellation, command);
    }
}
