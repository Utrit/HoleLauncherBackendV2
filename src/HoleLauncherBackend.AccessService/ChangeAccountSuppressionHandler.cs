using HoleLauncherBackend.Contracts;
using Npgsql;

namespace HoleLauncherBackend.AccessService;

internal sealed class ChangeAccountSuppressionHandler : IAccountMutationHandler
{
    public AccountMutation Mutation => AccountMutation.ChangeSuppression;

    public async Task<AccessOutcome> ApplyAsync(NpgsqlTransaction transaction, AccountOperationCommand command,
        CancellationToken cancellation)
    {
        if (command.Suppressed is not { } suppressed)
            return AccessOutcome.InvalidRequest;

        if (!await DBUtils.UpdateSuppressionAsync(transaction, command.AccountId, suppressed, cancellation))
            return AccessOutcome.NotFound;

        if (suppressed)
            await DBUtils.RevokeSessionsAsync(transaction, command.AccountId, cancellation);

        return AccessOutcome.NoContent;
    }
}
