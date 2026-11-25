using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.AccessService.AccessCommands;

internal sealed class AdminDirectoryConsumer(AccountStore accounts) : IAccessCommandConsumer
{
    public AccessOperation Operation => AccessOperation.AdminDirectory;

    public async Task ConsumeAsync(AccessCommandContext context, CancellationToken cancellation)
    {
        var rights = context.Account.Permissions;
        if (!rights.Contains(Permissions.StorageEdit) && !rights.Contains(Permissions.StorageDelete) &&
            !rights.Contains(Permissions.DatabaseEdit))
        {
            await context.ReplyAsync(AccessOutcome.Forbidden, cancellation);
            return;
        }

        var accountsList = await accounts.ListAsync(cancellation);
        await context.ReplyAsync(AccessOutcome.Ok, cancellation,
            directory: accountsList.Select(account => new AdminAccountName(account.AccountId, account.UserName)).ToArray());
    }
}
