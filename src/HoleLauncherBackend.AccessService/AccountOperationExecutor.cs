using HoleLauncherBackend.Contracts;
using Npgsql;

namespace HoleLauncherBackend.AccessService;

internal sealed class AccountOperationExecutor(DBUtils database, IEnumerable<IAccountMutationHandler> handlers)
{
    private readonly Dictionary<AccountMutation, IAccountMutationHandler> routes =
        handlers.ToDictionary(handler => handler.Mutation);

    public async Task<AccessOutcome> ExecuteAsync(AccountOperationCommand command, CancellationToken cancellation)
    {
        await using var connection = await database.OpenConnectionAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);
        if (!await DBUtils.TryClaimOperationAsync(transaction, command.OperationId, cancellation))
            return await DBUtils.ReadOperationOutcomeAsync(transaction, command.OperationId, cancellation);

        var outcome = await ApplyAsync(transaction, command, cancellation);
        await DBUtils.SaveOperationOutcomeAsync(transaction, command.OperationId, outcome, cancellation);
        await transaction.CommitAsync(cancellation);
        return outcome;
    }

    private async Task<AccessOutcome> ApplyAsync(NpgsqlTransaction transaction, AccountOperationCommand command, CancellationToken cancellation)
    {
        await DBUtils.LockAccountsAsync(transaction, [command.ActorId, command.AccountId], cancellation);
        if (!await DBUtils.HasPermissionAsync(transaction, command.ActorId, Permissions.EditUsers, cancellation))
            return AccessOutcome.Forbidden;

        return routes.TryGetValue(command.Mutation, out var handler)
            ? await handler.ApplyAsync(transaction, command, cancellation)
            : AccessOutcome.InvalidRequest;
    }
}
