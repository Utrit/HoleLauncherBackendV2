namespace HoleLauncherBackend.Storage;

internal sealed class StagingCleanup(FileStore storage, ILogger<StagingCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try { storage.RemoveExpiredStaging(DateTime.UtcNow.AddHours(-1)); }
            catch (IOException error) { logger.LogWarning("Staging cleanup failed: {ErrorType}", error.GetType().Name); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
