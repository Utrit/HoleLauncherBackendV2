using System.Diagnostics;
using Confluent.Kafka;
using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Messaging;

namespace HoleLauncherBackend.Storage;

internal sealed class UploadWatchdog(KafkaBus bus, IConfiguration configuration, ILogger<UploadWatchdog> logger)
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(30);
    private readonly int minimumRate = Math.Max(1, configuration.GetValue("Storage:MinUploadBytesPerSecond", 500_000));

    public async Task RunAsync(Guid id, UploadProgress progress, CancellationTokenSource transfer)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        var windowStart = Stopwatch.GetTimestamp();
        long windowBytes = 0;
        long reportedBytes = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(transfer.Token))
            {
                var bytes = progress.BytesWritten;
                var elapsed = Stopwatch.GetElapsedTime(windowStart);
                if (elapsed >= RateWindow)
                {
                    if (bytes - windowBytes < minimumRate * elapsed.TotalSeconds)
                    {
                        progress.MarkTimedOut();
                        await transfer.CancelAsync();
                        await ReportAsync(new StorageTransfer(id, Complete: false, TimedOut: true), CancellationToken.None);
                        return;
                    }

                    windowStart = Stopwatch.GetTimestamp();
                    windowBytes = bytes;
                }

                if (bytes > reportedBytes &&
                    await ReportAsync(new StorageTransfer(id, Complete: false, BytesWritten: bytes,
                        ProgressAt: DateTimeOffset.UtcNow), transfer.Token))
                    reportedBytes = bytes;
            }
        }
        catch (OperationCanceledException) when (transfer.IsCancellationRequested) { }
    }

    private async Task<bool> ReportAsync(StorageTransfer message, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await bus.PublishAsync(KafkaBus.StorageTransfersTopic, message.OperationId.ToString("N"), message, deadline.Token);
            return true;
        }
        catch (Exception error) when (error is KafkaException or OperationCanceledException)
        {
            if (!cancellation.IsCancellationRequested)
                logger.LogWarning("Could not report upload progress for {OperationId}", message.OperationId);
            return false;
        }
    }
}

internal sealed class UploadProgress
{
    private long bytesWritten;
    private int timedOut;

    public long BytesWritten => Interlocked.Read(ref bytesWritten);
    public bool TimedOut => Volatile.Read(ref timedOut) != 0;

    public void Record(int count) => Interlocked.Add(ref bytesWritten, count);
    public void MarkTimedOut() => Volatile.Write(ref timedOut, 1);
}
