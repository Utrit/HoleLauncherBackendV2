using HoleLauncherBackend.Contracts;
using Npgsql;

namespace HoleLauncherBackend.AccessService;

internal sealed class ChangeAccountPermissionsHandler : IAccountMutationHandler
{
    public AccountMutation Mutation => AccountMutation.ChangePermissions;

    public async Task<AccessOutcome> ApplyAsync(NpgsqlTransaction transaction, AccountOperationCommand command,
        CancellationToken cancellation)
    {
        if (command.Permissions is not { } permissions)
            return AccessOutcome.InvalidRequest;

        return await DBUtils.UpdatePermissionsAsync(transaction, command.AccountId, permissions, cancellation)
            ? AccessOutcome.NoContent
            : AccessOutcome.NotFound;
    }
}
