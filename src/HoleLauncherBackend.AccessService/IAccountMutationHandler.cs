using HoleLauncherBackend.Contracts;
using Npgsql;

namespace HoleLauncherBackend.AccessService;

internal interface IAccountMutationHandler
{
    AccountMutation Mutation { get; }
    Task<AccessOutcome> ApplyAsync(NpgsqlTransaction transaction, AccountOperationCommand command,
        CancellationToken cancellation);
}
