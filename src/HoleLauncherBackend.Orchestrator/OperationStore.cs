using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Orchestrator;

internal sealed class OperationStore(DBUtils database)
{
    public Task<OperationStatus?> FindAsync(Guid id, Guid actorId, CancellationToken cancellation) =>
        database.FindOperationAsync(id, actorId, cancellation);

    public async Task<OperationStatus?> StartAsync(AccountOperationCommand operation, CancellationToken cancellation)
    {
        await database.InsertOperationAsync(operation.OperationId, operation.ActorId, OperationState.Pending, cancellation);
        return await FindAsync(operation.OperationId, operation.ActorId, cancellation);
    }

    public Task FinishAsync(Guid id, AccessOutcome outcome, CancellationToken cancellation)
    {
        var state = outcome == AccessOutcome.NoContent ? OperationState.Done : OperationState.Failed;
        return database.FinishOperationAsync(id, state, outcome, cancellation);
    }
}
