using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Messaging;
using Npgsql;

namespace HoleLauncherBackend.AccessService.KafkaHandlers;

internal sealed class AccountExecutionMessageHandler(KafkaBus bus, AccountOperationExecutor executor,
    ILogger<AccountExecutionMessageHandler> logger) : IKafkaMessageHandler
{
    public string Topic => KafkaBus.AccountExecutionTopic;

    public async Task HandleAsync(string? payload, CancellationToken cancellation)
    {
        var command = KafkaBus.Read<AccountOperationCommand>(payload);
        if (command is null)
            return;

        AccessOutcome outcome;
        try
        {
            outcome = await executor.ExecuteAsync(command, cancellation);
        }
        catch (NpgsqlException error)
        {
            logger.LogWarning("Account operation {OperationId} failed: {ErrorType}",
                command.OperationId, error.GetType().Name);
            outcome = AccessOutcome.Unavailable;
        }

        await bus.PublishAsync(KafkaBus.OperationsTopic, command.OperationId.ToString("N"),
            new OrchestrationMessage(Guid.Empty, command.ActorId, command.OperationId, Result: outcome), cancellation);
    }
}
