using System.Collections.Concurrent;
using Confluent.Kafka;
using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Messaging;

namespace HoleLauncherBackend.Api;

internal sealed class KafkaAccessClient(KafkaBus bus, ILogger<KafkaAccessClient> logger) : BackgroundService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<AccessReply>> pending = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<AccessReply> SendAsync(
        AccessOperation operation, string lookup, Guid? accountId, string[]? permissions, bool? suppressed, Guid? operationId, string? storageDomain, CancellationToken cancellation)
    {
        var id = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow + RequestTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(RequestTimeout);
        var completion = new TaskCompletionSource<AccessReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.TryAdd(id, completion);
        try
        {
            await ready.Task.WaitAsync(deadline.Token);
            var request = new AccessRequest(id, expiresAt, lookup, operation, accountId, permissions, suppressed, operationId, storageDomain);
            await bus.PublishAsync(KafkaBus.AccessTopic, accountId?.ToString("N") ?? lookup, request, deadline.Token);
            return await completion.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new AccessReply(id, AccessOutcome.Unavailable);
        }
        catch (KafkaException error)
        {
            logger.LogWarning("Kafka request failed with {ErrorCode}", error.Error.Code);
            return new AccessReply(id, AccessOutcome.Unavailable);
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var consumer = bus.CreateConsumer($"holelauncher-api-{Guid.NewGuid():N}");
        var partition = KafkaBus.ReplyPartition;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var offsets = consumer.QueryWatermarkOffsets(partition, TimeSpan.FromSeconds(2));
                consumer.Assign(new TopicPartitionOffset(partition, offsets.High));
                ready.TrySetResult();
                break;
            }
            catch (KafkaException)
            {
                await Task.Delay(500, stoppingToken);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var record = consumer.Consume(stoppingToken);
                var reply = KafkaBus.Read<AccessReply>(record.Message.Value);
                if (reply is not null && pending.TryGetValue(reply.CorrelationId, out var completion))
                    completion.TrySetResult(reply);
            }
            catch (ConsumeException error) when (!error.Error.IsFatal)
            {
                logger.LogWarning("Kafka reply consumer received {ErrorCode}", error.Error.Code);
                await Task.Delay(500, stoppingToken);
            }
        }
    }
}
