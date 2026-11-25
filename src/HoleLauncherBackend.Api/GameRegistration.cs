namespace HoleLauncherBackend.Api;

internal sealed class GameRegistration(GameProfileStore profiles)
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public async Task<bool?> WaitAsync(Guid accountId, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(WaitTimeout);
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                if (await profiles.GetProvisionStatusAsync(accountId, deadline.Token) is { } completed)
                    return completed;
            } while (await timer.WaitForNextTickAsync(deadline.Token));
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }

        return null;
    }
}
