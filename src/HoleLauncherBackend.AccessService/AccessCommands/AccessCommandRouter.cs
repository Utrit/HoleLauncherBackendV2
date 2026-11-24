using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Messaging;
using Npgsql;

namespace HoleLauncherBackend.AccessService.AccessCommands;

internal sealed class AccessCommandRouter(KafkaBus bus, AccountStore accounts,
    IEnumerable<IAccessCommandConsumer> consumers, ILogger<AccessCommandRouter> logger)
{
    private readonly Dictionary<AccessOperation, IAccessCommandConsumer> routes =
        consumers.ToDictionary(consumer => consumer.Operation);

    public async Task DispatchAsync(AccessRequest request, CancellationToken cancellation)
    {
        try
        {
            var session = await accounts.FindSessionAsync(request.SessionLookup, cancellation);
            if (session is null || !session.IsValidAt(DateTimeOffset.UtcNow))
            {
                await ReplyAsync(request, AccessOutcome.InvalidSession, cancellation);
                return;
            }

            if (!routes.TryGetValue(request.Operation, out var consumer))
            {
                await ReplyAsync(request, AccessOutcome.InvalidRequest, cancellation);
                return;
            }

            if (consumer.RequiredPermission is { } permission && !session.Account.Permissions.Contains(permission))
            {
                await ReplyAsync(request, AccessOutcome.Forbidden, cancellation);
                return;
            }

            var expiresAt = session.ExpiresAt < request.ExpiresAt ? session.ExpiresAt : request.ExpiresAt;
            var context = new AccessCommandContext(request with { ExpiresAt = expiresAt }, session, bus);
            await consumer.ConsumeAsync(context, cancellation);
        }
        catch (NpgsqlException error)
        {
            logger.LogWarning("Account request failed with SQL state {SqlState}", error.SqlState);
            await ReplyAsync(request, AccessOutcome.Unavailable, cancellation);
        }
    }

    private Task ReplyAsync(AccessRequest request, AccessOutcome outcome, CancellationToken cancellation) =>
        bus.ReplyAsync(new AccessReply(request.CorrelationId, outcome), cancellation);
}
