using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.AccessService.AccessCommands;

internal sealed class ChangePermissionsConsumer : IAccessCommandConsumer
{
    public AccessOperation Operation => AccessOperation.ChangePermissions;
    public string? RequiredPermission => Permissions.EditUsers;

    public Task ConsumeAsync(AccessCommandContext context, CancellationToken cancellation)
    {
        var request = context.Request;
        if (request.AccountId is not { } accountId || request.Permissions is not { } flags ||
            flags.Any(flag => !Permissions.All.Contains(flag)))
            return context.ReplyAsync(AccessOutcome.InvalidRequest, cancellation);

        var id = request.CorrelationId;
        var command = new AccountOperationCommand(id, context.Account.AccountId, accountId,
            AccountMutation.ChangePermissions, flags.Distinct().ToArray(), null);
        return context.ForwardOperationAsync(id, cancellation, command);
    }
}
