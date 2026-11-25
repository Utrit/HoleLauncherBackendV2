using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Messaging;

namespace HoleLauncherBackend.Storage;

internal sealed class StorageWorker(KafkaBus bus, FileStore storage, ILogger<StorageWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bus.SubscribeAsync("holelauncher-storage-v1", [KafkaBus.StorageDecisionsTopic],
            (_, payload, cancellation) => ConsumeAsync(payload, cancellation),
            stoppingToken);

    private async Task ConsumeAsync(string? payload, CancellationToken cancellation)
    {
        if (KafkaBus.Read<StorageDecision>(payload) is not { } decision ||
            !StorageAddress.IsValidDomain(decision.Domain))
            return;

        var result = await ExecuteDecisionAsync(decision, cancellation);
        await bus.PublishAsync(KafkaBus.StorageResultsTopic, decision.OperationId.ToString("N"), result, cancellation);
    }

    private async Task<StorageResult> ExecuteDecisionAsync(StorageDecision decision, CancellationToken cancellation)
    {
        var id = decision.OperationId;
        var step = decision.Publish ? StorageStep.Publish : StorageStep.Revert;
        try
        {
            if (!decision.Publish)
                await storage.RevertAsync(decision.Domain, id, cancellation);
            else if (!storage.TryPublish(decision.Domain, id))
                return new StorageResult(id, step, ResultStatus.Failed, "error.storage_not_ready");

            return new StorageResult(id, step, ResultStatus.Complete);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Storage could not complete {Step} for {OperationId}: {ErrorType}",
                step, id, error.GetType().Name);
            return new StorageResult(id, step, ResultStatus.Failed, "error.storage_io");
        }
    }
}
