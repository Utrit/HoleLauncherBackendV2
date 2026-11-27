using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Messaging;

namespace HoleLauncherBackend.Orchestrator;

internal sealed class StorageUploadHandler(KafkaBus bus, DBUtils database)
{
    public async Task TransferAsync(StorageTransfer transfer, CancellationToken cancellation)
    {
        if (transfer.ProgressAt is { } progressAt)
        {
            if (transfer.BytesWritten > 0)
                await database.ExtendUploadTransferAsync(transfer.OperationId, progressAt, cancellation);
            return;
        }

        if (await database.FindUploadAsync(transfer.OperationId, cancellation) is not { State: OperationState.Pending } upload)
            return;

        if (transfer.Complete && await database.BeginUploadPublishAsync(transfer.OperationId, cancellation) is { } deadline)
        {
            await SendDecisionAsync(transfer.OperationId, upload.Domain, publish: true, deadline, cancellation);
            return;
        }

        if (transfer.TimedOut || transfer.Complete)
        {
            await RevertAsync(transfer.OperationId,
                transfer.TimedOut ? "error.upload_slow" : "error.upload_expired",
                cancellation, AccessOutcome.Timeout);
            return;
        }

        await RevertAsync(transfer.OperationId, "error.upload_interrupted", cancellation);
    }

    public async Task FinishAsync(StorageResult result, CancellationToken cancellation)
    {
        if (result.Status == ResultStatus.Failed)
        {
            await RevertAsync(result.OperationId, result.ErrorMessage ?? "error.storage_step_failed", cancellation);
            return;
        }

        if (result.Status != ResultStatus.Complete)
            return;

        switch (result.Step)
        {
            case StorageStep.Publish:
                await database.CompleteUploadAsync(result.OperationId, published: true, cancellation);
                break;
            case StorageStep.Revert:
                await database.CompleteUploadAsync(result.OperationId, published: false, cancellation);
                break;
        }
    }

    public async Task DiscardLauncherArtifactAsync(Guid id, CancellationToken cancellation)
    {
        if (await database.BeginUploadRevertAsync(id, "error.upload_interrupted", AccessOutcome.Unavailable,
                cancellation, discardLauncherArtifact: true) is { } artifact)
            await SendDecisionAsync(id, artifact.Domain, publish: false, artifact.ExpiresAt, cancellation);
    }

    public async Task RevertAsync(Guid id, string reason, CancellationToken cancellation, AccessOutcome failure = AccessOutcome.Unavailable)
    {
        if (await database.BeginUploadRevertAsync(id, reason, failure, cancellation) is { } upload)
            await SendDecisionAsync(id, upload.Domain, publish: false, upload.ExpiresAt, cancellation);
    }

    private Task SendDecisionAsync(Guid id, string domain, bool publish, DateTimeOffset expiresAt,
        CancellationToken cancellation) =>
        bus.PublishAsync(KafkaBus.StorageDecisionsTopic, id.ToString("N"),
            new StorageDecision(id, domain, publish, expiresAt), cancellation);
}
