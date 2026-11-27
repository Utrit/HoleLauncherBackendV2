using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.AccessService.AccessCommands;

internal sealed class StorageUploadConsumer(AccessOperation operation) : IAccessCommandConsumer
{
    public AccessOperation Operation => operation;

    public Task ConsumeAsync(AccessCommandContext context, CancellationToken cancellation)
    {
        if (context.Request.StorageDomain is not { } domain || (!StorageAddress.IsPublicDomain(domain) || domain == StorageAddress.LauncherDomain))
            return context.ReplyAsync(AccessOutcome.InvalidRequest, cancellation);

        var permissions = context.Account.Permissions;
        var allowed = operation switch
        {
            AccessOperation.StartOfferUpload => domain == RepositoryStorage.OfferDomain(context.Account.AccountId) &&
                permissions.Contains(Permissions.RepositoryOffer),
            AccessOperation.StartRepositoryUpload => RepositoryStorage.DomainFor(domain) == domain &&
                !domain.StartsWith("offer-", StringComparison.Ordinal) && permissions.Contains(Permissions.RepositoryEdit),
            _ => permissions.Contains(Permissions.StorageUpload) ||
                (domain == StorageAddress.UserDataDomain && permissions.Contains(Permissions.StorageUserDataUpload))
        };
        if (!allowed)
            return context.ReplyAsync(AccessOutcome.Forbidden, cancellation);

        var storageDomain = StorageAddress.ResolveDomain(domain, context.Account.AccountId);
        return context.ForwardOperationAsync(context.Request.CorrelationId, cancellation, uploadDomain: storageDomain);
    }
}
