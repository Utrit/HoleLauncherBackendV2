using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Messaging;

namespace HoleLauncherBackend.Orchestrator;

internal sealed class StatusMessageWorker(KafkaBus bus, OperationHandler operations, StorageUploadHandler uploads) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var routes = new Dictionary<string, Func<string?, CancellationToken, Task>>(StringComparer.Ordinal)
        {
            [KafkaBus.StatusTopic] = ReplyWithStatusAsync,
            [KafkaBus.OperationsTopic] = (payload, cancellation) =>
                DispatchAsync<OrchestrationMessage>(payload, operations.HandleAsync, cancellation),
            [KafkaBus.StorageTransfersTopic] = (payload, cancellation) =>
                DispatchAsync<StorageTransfer>(payload, uploads.TransferAsync, cancellation),
            [KafkaBus.StorageResultsTopic] = (payload, cancellation) =>
                DispatchAsync<StorageResult>(payload, uploads.FinishAsync, cancellation)
        };

        return bus.SubscribeAsync("holelauncher-orchestrator-status-v1", routes.Keys,
            (topic, payload, cancellation) => routes[topic](payload, cancellation),
            stoppingToken);
    }

    private static Task DispatchAsync<T>(string? payload, Func<T, CancellationToken, Task> handler,
        CancellationToken cancellation) where T : class =>
        KafkaBus.Read<T>(payload) is { } message ? handler(message, cancellation) : Task.CompletedTask;

    private Task ReplyWithStatusAsync(string? payload, CancellationToken cancellation)
    {
        if (KafkaBus.Read<GetSystemStatusRequest>(payload) is not { User.Permissions: not null } request ||
            request.ExpiresAt <= DateTimeOffset.UtcNow)
            return Task.CompletedTask;

        var reply = request.User.Permissions.Contains(Permissions.ReadSystemStatus)
            ? new AccessReply(request.CorrelationId, AccessOutcome.Ok, Services:
                [new("Access Service", "Online"), new("Accounts PostgreSQL", "Online"),
                 new("Init", "Schema ready"), new("Kafka", "Online"), new("Orchestrator", "Online")])
            : new AccessReply(request.CorrelationId, AccessOutcome.Forbidden);
        return bus.ReplyAsync(reply, cancellation);
    }
}
