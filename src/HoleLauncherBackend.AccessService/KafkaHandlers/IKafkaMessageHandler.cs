namespace HoleLauncherBackend.AccessService.KafkaHandlers;

internal interface IKafkaMessageHandler
{
    string Topic { get; }
    Task HandleAsync(string? payload, CancellationToken cancellation);
}
