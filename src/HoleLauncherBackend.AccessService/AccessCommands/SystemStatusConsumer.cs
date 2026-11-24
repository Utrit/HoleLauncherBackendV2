using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Messaging;

namespace HoleLauncherBackend.AccessService.AccessCommands;

internal sealed class SystemStatusConsumer : IAccessCommandConsumer
{
    public AccessOperation Operation => AccessOperation.Status;

    public Task ConsumeAsync(AccessCommandContext context, CancellationToken cancellation)
    {
        var request = context.Request;
        var account = context.Account;
        var status = new GetSystemStatusRequest(request.CorrelationId, request.ExpiresAt,
            new UserAccess(account.AccountId, account.UserName, account.Permissions));
        return context.Bus.PublishAsync(KafkaBus.StatusTopic, account.AccountId.ToString("N"), status, cancellation);
    }
}
