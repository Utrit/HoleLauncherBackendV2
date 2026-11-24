using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.AccessService.AccessCommands;

internal interface IAccessCommandConsumer
{
    AccessOperation Operation { get; }
    string? RequiredPermission => null;
    Task ConsumeAsync(AccessCommandContext context, CancellationToken cancellation);
}
