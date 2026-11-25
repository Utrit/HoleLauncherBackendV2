using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Messaging;
using HoleLauncherBackend.Shared;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HoleLauncherBackend.Orchestrator;

internal sealed class GameNameOperations(
    [FromKeyedServices("game-profiles")] NpgsqlDataSource game,
    DBUtils operations, KafkaBus bus, DraslClient drasl)
{
    public async Task EnqueueAsync(OrchestrationMessage message, CancellationToken cancellation)
    {
        var name = message.GameName;
        if (!GameNameRules.IsValid(name) || message.ActorId == Guid.Empty ||
            (!message.ProvisionGameAccount && (message.ExpiresAt is not { } expires || expires <= DateTimeOffset.UtcNow)))
            return;

        if (!drasl.IsConfigured && !message.ProvisionGameAccount)
        {
            await bus.ReplyAsync(new AccessReply(message.CorrelationId, AccessOutcome.Unavailable), cancellation);
            return;
        }

        await using var insert = game.CreateSqlCommand("""
            INSERT INTO game_name_history (operation_id, account_id, game_name, provision)
            VALUES ($1, $2, $3, $4) ON CONFLICT (operation_id) DO NOTHING
            """, message.OperationId, message.ActorId, name!, message.ProvisionGameAccount);
        await insert.ExecuteNonQueryAsync(cancellation);
        await operations.InsertGameOperationAsync(message.OperationId, message.ActorId, cancellation);

        if (!message.ProvisionGameAccount)
        {
            var status = await operations.FindOperationAsync(message.OperationId, message.ActorId, cancellation);
            await bus.ReplyAsync(new AccessReply(message.CorrelationId, AccessOutcome.Accepted, Operation: status), cancellation);
        }
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellation)
    {
        await using var connection = await game.OpenConnectionAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);
        await using var select = transaction.CreateSqlCommand("""
            SELECT entry.operation_id, entry.account_id, entry.game_name, entry.provision,
                COALESCE((
                    SELECT previous.game_name FROM game_name_history AS previous
                    WHERE previous.account_id = entry.account_id AND previous.state = 'complete'
                    ORDER BY previous.sequence DESC LIMIT 1
                ), profile.game_name)
            FROM game_name_history AS entry
            LEFT JOIN game_profiles AS profile ON profile.account_id = entry.account_id
            WHERE entry.state = 'pending' AND NOT EXISTS (
                SELECT 1 FROM game_name_history AS earlier
                WHERE earlier.account_id = entry.account_id AND earlier.state = 'pending'
                    AND earlier.sequence < entry.sequence
            )
            ORDER BY entry.sequence LIMIT 1 FOR UPDATE OF entry SKIP LOCKED
            """);
        Guid operationId, accountId;
        string name, previousName;
        bool provision;
        await using (var row = await select.ExecuteReaderAsync(cancellation))
        {
            if (!await row.ReadAsync(cancellation))
                return false;
            operationId = row.GetGuid(0);
            accountId = row.GetGuid(1);
            name = row.GetString(2);
            provision = row.GetBoolean(3);
            previousName = row.IsDBNull(4) ? name : row.GetString(4);
        }

        await using var accountLock = transaction.CreateSqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended($1::uuid::text, 0))", accountId);
        await accountLock.ExecuteNonQueryAsync(cancellation);

        var result = await UpdateDraslAsync(accountId, name, previousName, provision, cancellation);
        var state = result.Outcome == AccessOutcome.NoContent ? "complete" : "reverted";
        await using var update = transaction.CreateSqlCommand("""
            UPDATE game_name_history SET state = $2, outcome = $3, error_message = NULLIF($4, ''), game_name = $5
            WHERE operation_id = $1
            """, operationId, state, (int)result.Outcome, result.Error ?? "", result.Name);
        await update.ExecuteNonQueryAsync(cancellation);
        await transaction.CommitAsync(cancellation);
        return true;
    }

    public async Task ReportAsync(CancellationToken cancellation)
    {
        var results = new List<(Guid Id, Guid Account, AccessOutcome Outcome, string Error, bool Provision)>();
        await using (var select = game.CreateSqlCommand("""
            SELECT operation_id, account_id, outcome, COALESCE(error_message, ''), provision FROM game_name_history
            WHERE state <> 'pending' AND NOT reported ORDER BY sequence LIMIT 50
            """))
        await using (var rows = await select.ExecuteReaderAsync(cancellation))
            while (await rows.ReadAsync(cancellation))
                results.Add((rows.GetGuid(0), rows.GetGuid(1), (AccessOutcome)rows.GetInt32(2), rows.GetString(3), rows.GetBoolean(4)));

        foreach (var result in results)
        {
            if (result.Provision)
            {
                var command = new AccountOperationCommand(result.Id, result.Account, result.Account, AccountMutation.FinalizeRegistration,
                    null, null, result.Outcome == AccessOutcome.NoContent);
                await bus.PublishAsync(KafkaBus.AccountExecutionTopic, result.Account.ToString("N"), command, cancellation);
                continue;
            }
            await operations.FinishGameOperationAsync(result.Id, result.Account, result.Outcome, result.Error, cancellation);
            await using var mark = game.CreateSqlCommand(
                "UPDATE game_name_history SET reported = true WHERE operation_id = $1", result.Id);
            await mark.ExecuteNonQueryAsync(cancellation);
        }
    }

    public async Task AcknowledgeRegistrationAsync(Guid id, Guid accountId, CancellationToken cancellation)
    {
        await using var query = game.CreateSqlCommand("""
            SELECT outcome, COALESCE(error_message, '') FROM game_name_history
            WHERE operation_id = $1 AND account_id = $2 AND provision AND state <> 'pending' AND NOT reported
            """, id, accountId);
        AccessOutcome outcome;
        string error;
        await using (var row = await query.ExecuteReaderAsync(cancellation))
        {
            if (!await row.ReadAsync(cancellation))
                return;
            outcome = (AccessOutcome)row.GetInt32(0);
            error = row.GetString(1);
        }
        await operations.FinishGameOperationAsync(id, accountId, outcome, error, cancellation);
        await using var mark = game.CreateSqlCommand(
            "UPDATE game_name_history SET reported = true WHERE operation_id = $1", id);
        await mark.ExecuteNonQueryAsync(cancellation);
    }

    private async Task<(AccessOutcome Outcome, string Name, string? Error)> UpdateDraslAsync(
        Guid accountId, string requestedName, string previousName, bool provision, CancellationToken cancellation)
    {
        if (!drasl.IsConfigured)
            return (AccessOutcome.Unavailable, requestedName, "error.drasl_unconfirmed");

        try
        {
            var current = await drasl.ProvisionAsync(accountId, provision ? requestedName : previousName, cancellation);
            if (provision || current == requestedName)
                return (AccessOutcome.NoContent, current, null);

            var outcome = await drasl.RenameAsync(accountId, requestedName, cancellation);
            return (outcome, requestedName, outcome switch
            {
                AccessOutcome.NoContent => null,
                AccessOutcome.Conflict => "error.game_name_taken",
                _ => "error.game_name_rejected"
            });
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested &&
            error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            if (await WasAppliedAsync(accountId, requestedName, cancellation))
                return (AccessOutcome.NoContent, requestedName, null);
            return (error is OperationCanceledException ? AccessOutcome.Timeout : AccessOutcome.Unavailable,
                requestedName, "error.drasl_unconfirmed");
        }
    }

    private async Task<bool> WasAppliedAsync(Guid accountId, string name, CancellationToken cancellation)
    {
        try { return await drasl.FindNameAsync(accountId, cancellation) == name; }
        catch (Exception error) when (!cancellation.IsCancellationRequested &&
            error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
