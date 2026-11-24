using HoleLauncherBackend.AccessService.KafkaHandlers;
using HoleLauncherBackend.Messaging;

namespace HoleLauncherBackend.AccessService;

internal sealed class AccessMessageWorker(KafkaBus bus, IEnumerable<IKafkaMessageHandler> handlers) : BackgroundService
{
    private readonly Dictionary<string, IKafkaMessageHandler> routes =
        handlers.ToDictionary(handler => handler.Topic, StringComparer.Ordinal);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bus.SubscribeAsync("holelauncher-access-v1", routes.Keys,
            (topic, payload, cancellation) => routes[topic].HandleAsync(payload, cancellation),
            stoppingToken);
}
