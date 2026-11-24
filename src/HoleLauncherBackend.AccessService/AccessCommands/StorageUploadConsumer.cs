using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.AccessService.AccessCommands;

internal sealed class StorageUploadConsumer : IAccessCommandConsumer
{
    public AccessOperation Operation => AccessOperation.StartStorageUpload;

    public Task ConsumeAsync(AccessCommandContext context, CancellationToken cancellation)
    {
        if (context.Request.StorageDomain is not { } domain || !StorageAddress.IsPublicDomain(domain))
            return context.ReplyAsync(AccessOutcome.InvalidRequest, cancellation);

        var permissions = context.Account.Permissions;
        if (!permissions.Contains(Permissions.StorageUpload) &&
            !(domain == StorageAddress.UserDataDomain && permissions.Contains(Permissions.StorageUserDataUpload)))
            return context.ReplyAsync(AccessOutcome.Forbidden, cancellation);

        var storageDomain = StorageAddress.ResolveDomain(domain, context.Account.AccountId);
        return context.ForwardOperationAsync(context.Request.CorrelationId, cancellation, uploadDomain: storageDomain);
    }
}
