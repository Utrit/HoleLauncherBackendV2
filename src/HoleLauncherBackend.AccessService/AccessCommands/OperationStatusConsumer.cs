using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.AccessService.AccessCommands;

internal sealed class OperationStatusConsumer : IAccessCommandConsumer
{
    public AccessOperation Operation => AccessOperation.OperationStatus;

    public Task ConsumeAsync(AccessCommandContext context, CancellationToken cancellation) =>
        context.Request.OperationId is { } operationId
            ? context.ForwardOperationAsync(operationId, cancellation)
            : context.ReplyAsync(AccessOutcome.InvalidRequest, cancellation);
}
