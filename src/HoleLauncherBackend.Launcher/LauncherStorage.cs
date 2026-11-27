using Confluent.Kafka;
using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Messaging;

namespace HoleLauncherBackend.Launcher;

internal sealed class LauncherStorage(IHttpClientFactory clients, KafkaBus bus, ILogger<LauncherStorage> logger)
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan PublishCheckInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DiscardTimeout = TimeSpan.FromSeconds(6);

    public async Task UploadAsync(Guid id, Guid uploader, string path, CancellationToken cancellation)
    {
        using var consumer = bus.CreateConsumer("holelauncher-launcher");
        var partition = KafkaBus.ReplyPartition;
        var offsets = consumer.QueryWatermarkOffsets(partition, StartTimeout);
        consumer.Assign(new TopicPartitionOffset(partition, offsets.High));
        var registration = await RequestAsync(consumer, id, uploader, StorageAddress.LauncherDomain, cancellation);
        if (registration is not { Outcome: AccessOutcome.Accepted, Operation.State: OperationState.Pending })
            throw new InvalidDataException("Launcher upload could not be registered.");

        using var client = clients.CreateClient("storage");
        using var upload = new HttpRequestMessage(HttpMethod.Put, $"staging/{StorageAddress.LauncherDomain}/{id:D}")
        {
            Content = new StreamContent(File.OpenRead(path))
        };
        upload.Headers.Add("X-Uploader-Id", uploader.ToString("D"));
        upload.Headers.Add("X-Storage-File-Name", Uri.EscapeDataString(Path.GetFileName(path)));
        using var response = await client.SendAsync(upload, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        await bus.PublishAsync(KafkaBus.StorageTransfersTopic, id.ToString("N"),
            new StorageTransfer(id, Complete: true), cancellation);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(PublishTimeout);
        while (true)
        {
            var status = await RequestAsync(consumer, id, uploader, null, deadline.Token);
            if (status.Operation?.State == OperationState.Done)
                return;
            if (status.Operation?.State != OperationState.Pending)
                throw new InvalidDataException("Launcher artifact could not be published.");
            await Task.Delay(PublishCheckInterval, deadline.Token);
        }
    }

    private async Task<AccessReply> RequestAsync(IConsumer<string, string> consumer, Guid id, Guid uploader,
        string? domain, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(StartTimeout);
        var token = deadline.Token;
        var correlation = Guid.NewGuid();
        await bus.PublishAsync(KafkaBus.OperationsTopic, id.ToString("N"),
            new OrchestrationMessage(correlation, uploader, id, DateTimeOffset.UtcNow + StartTimeout,
                UploadDomain: domain), token);
        return await Task.Run(() =>
        {
            while (true)
            {
                var record = consumer.Consume(token);
                if (KafkaBus.Read<AccessReply>(record.Message.Value) is not { } reply || reply.CorrelationId != correlation)
                    continue;
                return reply;
            }
        }, token);
    }

    public async Task<bool> DiscardAsync(Guid id)
    {
        using var deadline = new CancellationTokenSource(DiscardTimeout);
        try
        {
            await bus.PublishAsync(KafkaBus.OperationsTopic, id.ToString("N"),
                new OrchestrationMessage(Guid.Empty, Guid.Empty, id, DiscardLauncherArtifact: true), deadline.Token);
            return true;
        }
        catch (Exception error) when (error is KafkaException or OperationCanceledException)
        {
            logger.LogWarning("Could not request cleanup of launcher artifact {FileId}: {ErrorType}", id, error.GetType().Name);
            return false;
        }
    }

    public async Task<HttpResponseMessage> ReadAsync(Guid id, bool hash, CancellationToken cancellation)
    {
        using var client = clients.CreateClient("storage");
        return await client.GetAsync(FilePath(id) + (hash ? "/sha512" : ""),
            HttpCompletionOption.ResponseHeadersRead, cancellation);
    }

    private static string FilePath(Guid id) => $"files/{StorageAddress.LauncherDomain}/{id:D}";
}
