using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.AccessService.AccessCommands;

internal sealed class UsersConsumer(AccountStore accounts) : IAccessCommandConsumer
{
    public AccessOperation Operation => AccessOperation.Users;
    public string? RequiredPermission => Permissions.EditUsers;

    public async Task ConsumeAsync(AccessCommandContext context, CancellationToken cancellation)
    {
        var users = await accounts.ListAsync(cancellation);
        await context.ReplyAsync(AccessOutcome.Ok, cancellation, users: users);
    }
}
