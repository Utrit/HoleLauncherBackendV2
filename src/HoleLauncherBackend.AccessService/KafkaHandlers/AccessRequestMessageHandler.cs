using HoleLauncherBackend.AccessService.AccessCommands;
using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Messaging;

namespace HoleLauncherBackend.AccessService.KafkaHandlers;

internal sealed class AccessRequestMessageHandler(AccessCommandRouter router) : IKafkaMessageHandler
{
    public string Topic => KafkaBus.AccessTopic;

    public Task HandleAsync(string? payload, CancellationToken cancellation)
    {
        var request = KafkaBus.Read<AccessRequest>(payload);
        return request is { SessionLookup.Length: 64 } && request.ExpiresAt > DateTimeOffset.UtcNow
            ? router.DispatchAsync(request, cancellation)
            : Task.CompletedTask;
    }
}
