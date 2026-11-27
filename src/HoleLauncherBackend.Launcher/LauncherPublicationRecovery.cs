namespace HoleLauncherBackend.Launcher;

internal sealed class LauncherPublicationRecovery(LauncherStore store) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => store.RecoverAsync(stoppingToken);
}
