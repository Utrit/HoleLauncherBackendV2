using Npgsql;

namespace HoleLauncherBackend.Orchestrator;

internal sealed class GameTextureWorker(GameTextureOperations operations, ILogger<GameTextureWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await operations.ProcessNextAsync(stoppingToken);
                await operations.ReportAsync(stoppingToken);
            }
            catch (NpgsqlException error)
            {
                logger.LogWarning("Could not process game texture history: {ErrorType}", error.GetType().Name);
            }
        }
    }
}
