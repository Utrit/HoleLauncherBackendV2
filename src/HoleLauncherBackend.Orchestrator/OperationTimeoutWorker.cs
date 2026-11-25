using Confluent.Kafka;
using HoleLauncherBackend.Contracts;
using Npgsql;

namespace HoleLauncherBackend.Orchestrator;

internal sealed class OperationTimeoutWorker(DBUtils database, StorageUploadHandler uploads,
    ILogger<OperationTimeoutWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await database.FailExpiredAccountOperationsAsync(stoppingToken);
                foreach (var id in await database.FindExpiredUploadsAsync(stoppingToken))
                    await uploads.RevertAsync(id, "error.storage_timeout", stoppingToken, AccessOutcome.Timeout);
            }
            catch (Exception error) when (error is NpgsqlException or KafkaException { Error.IsFatal: false })
            {
                logger.LogWarning("Operation timeout check failed with {ErrorType}", error.GetType().Name);
            }
        }
    }
}
