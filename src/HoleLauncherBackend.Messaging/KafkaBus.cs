using System.Text.Json;
using Confluent.Kafka;
using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Messaging;

public sealed class KafkaBus(string bootstrapServers, string username, string password) : IDisposable
{
    public const string AccessTopic = "holelauncher.access.requests.v1";
    public const string OperationsTopic = "holelauncher.orchestrator.operations.v1";
    public const string AccountExecutionTopic = "holelauncher.access.execute.v1";
    public const string StatusTopic = "holelauncher.orchestrator.status.v1";
    public const string StorageTransfersTopic = "holelauncher.storage.transfers.v1";
    public const string StorageDecisionsTopic = "holelauncher.storage.decisions.v1";
    public const string StorageResultsTopic = "holelauncher.storage.results.v1";
    public const string ReplyTopic = "holelauncher.api.replies.v1";

    public static TopicPartition ReplyPartition { get; } = new(ReplyTopic, new Partition(0));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IProducer<string, string> producer = new ProducerBuilder<string, string>(new ProducerConfig
    {
        BootstrapServers = bootstrapServers,
            SecurityProtocol = SecurityProtocol.SaslPlaintext,
            SaslMechanism = SaslMechanism.Plain,
            SaslUsername = username,
            SaslPassword = password,
        EnableIdempotence = true,
        MessageTimeoutMs = 5000
    }).Build();

    public IConsumer<string, string> CreateConsumer(string group) =>
        new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            SecurityProtocol = SecurityProtocol.SaslPlaintext,
            SaslMechanism = SaslMechanism.Plain,
            SaslUsername = username,
            SaslPassword = password,
            GroupId = group,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();

    public async Task SubscribeAsync(string group, IEnumerable<string> topics,
        Func<string, string?, CancellationToken, Task> onMessage,
        CancellationToken cancellation)
    {
        using var consumer = CreateConsumer(group);
        consumer.Subscribe(topics);
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var record = consumer.Consume(cancellation);
                consumer.Commit(record);
                await onMessage(record.Topic, record.Message.Value, cancellation);
            }
        }
        finally
        {
            consumer.Close();
        }
    }


    public Task PublishAsync<T>(string topic, string key, T value, CancellationToken cancellation) =>
        producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = JsonSerializer.Serialize(value, Json) }, cancellation);

    public Task ReplyAsync(AccessReply reply, CancellationToken cancellation) =>
        producer.ProduceAsync(
            ReplyPartition,
            new Message<string, string>
            {
                Key = reply.CorrelationId.ToString("N"),
                Value = JsonSerializer.Serialize(reply, Json)
            },
            cancellation);

    public static T? Read<T>(string? value)
    {
        if (value is null)
            return default;

        try { return JsonSerializer.Deserialize<T>(value, Json); }
        catch (JsonException) { return default; }
    }

    public void Dispose() => producer.Dispose();
}
