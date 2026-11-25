using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Messaging;

namespace HoleLauncherBackend.Orchestrator;

internal sealed class OperationHandler(KafkaBus bus, OperationStore operations, DBUtils database, GameNameOperations gameNames, GameTextureOperations gameTextures)
{
    public async Task HandleAsync(OrchestrationMessage message, CancellationToken cancellation)
    {
        if (message.Result is { } result)
        {
            await operations.FinishAsync(message.OperationId, result, cancellation);
            return;
        }

        if (message.GameTexture is not null)
        {
            await gameTextures.EnqueueAsync(message, cancellation);
            return;
        }

        if (message.ProvisionGameAccount || message.GameName is not null)
        {
            await gameNames.EnqueueAsync(message, cancellation);
            return;
        }

        if (message.ExpiresAt is not { } expires)
            return;

        var state = await operations.FindAsync(message.OperationId, message.ActorId, cancellation);
        if (message.UploadDomain is { } domain)
        {
            if (expires <= DateTimeOffset.UtcNow || !StorageAddress.IsValidDomain(domain))
                return;

            await database.InsertUploadAsync(message.OperationId, message.ActorId, domain, cancellation);
            state = await operations.FindAsync(message.OperationId, message.ActorId, cancellation);
            if (state is not null)
                await bus.ReplyAsync(new AccessReply(message.CorrelationId, AccessOutcome.Accepted,
                    Operation: state, ActorId: message.ActorId), cancellation);
            return;
        }

        if (message.Command is { } command)
        {
            if (state is null)
            {
                if (expires <= DateTimeOffset.UtcNow)
                    return;
                state = await operations.StartAsync(command, cancellation);
            }
            if (state?.State == OperationState.Pending)
                await bus.PublishAsync(KafkaBus.AccountExecutionTopic, command.AccountId.ToString("N"), command, cancellation);

            if (expires > DateTimeOffset.UtcNow && state is not null)
                await bus.ReplyAsync(new AccessReply(message.CorrelationId, AccessOutcome.Accepted,
                    Operation: state), cancellation);
            return;
        }

        if (expires <= DateTimeOffset.UtcNow)
            return;

        await bus.ReplyAsync(new AccessReply(message.CorrelationId,
            state is null ? AccessOutcome.NotFound : AccessOutcome.Ok, Operation: state), cancellation);
    }
}
