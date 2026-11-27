using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Shared;
using Npgsql;

namespace HoleLauncherBackend.Orchestrator;

internal sealed class DBUtils(NpgsqlDataSource database)
{
    public async Task<OperationStatus?> FindOperationAsync(Guid id, Guid actorId, CancellationToken cancellation)
    {
        await using var command = Create("SELECT state, outcome, error_message FROM operations WHERE id = $1 AND actor_id = $2", id, actorId);
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        if (!await reader.ReadAsync(cancellation))
            return null;

        var state = (OperationState)reader.GetInt32(0);
        return new OperationStatus(id, state,
            reader.IsDBNull(1) ? null : (AccessOutcome)reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public async Task InsertOperationAsync(Guid id, Guid actorId, OperationState state, CancellationToken cancellation)
    {
        await using var command = Create("""
            INSERT INTO operations (id, actor_id, state, expires_at)
            VALUES ($1, $2, $3, now() + interval '30 seconds') ON CONFLICT (id) DO NOTHING
            """, id, actorId, (int)state);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    public async Task InsertGameOperationAsync(Guid id, Guid actorId, CancellationToken cancellation)
    {
        await using var command = Create("""
            INSERT INTO operations (id, actor_id, state) VALUES ($1, $2, $3)
            ON CONFLICT (id) DO NOTHING
            """, id, actorId, (int)OperationState.Pending);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    public async Task FinishGameOperationAsync(Guid id, Guid actorId, AccessOutcome outcome, string error,
        CancellationToken cancellation)
    {
        var state = outcome == AccessOutcome.NoContent ? OperationState.Done : OperationState.Failed;
        await using var command = Create("""
            INSERT INTO operations (id, actor_id, state, outcome, error_message, finished_at)
            VALUES ($1, $2, $3, $4, NULLIF($5, ''), now())
            ON CONFLICT (id) DO UPDATE SET state = EXCLUDED.state, outcome = EXCLUDED.outcome,
                error_message = EXCLUDED.error_message, finished_at = EXCLUDED.finished_at
            WHERE operations.actor_id = EXCLUDED.actor_id AND operations.state = $6
            """, id, actorId, (int)state, (int)outcome, error, (int)OperationState.Pending);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    public async Task FinishOperationAsync(Guid id, OperationState state, AccessOutcome outcome,
        CancellationToken cancellation)
    {
        await using var command = Create("""
            UPDATE operations SET state = $2, outcome = COALESCE(outcome, $3), finished_at = now()
            WHERE id = $1 AND state = $4 AND (expires_at IS NULL OR expires_at > now())
            """, id, (int)state, (int)outcome, (int)OperationState.Pending);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    public async Task FailExpiredAccountOperationsAsync(CancellationToken cancellation)
    {
        await using var command = Create("""
            UPDATE operations SET state = $1, outcome = $2, finished_at = now()
            WHERE state = $3 AND storage_domain IS NULL AND expires_at <= now()
            """, (int)OperationState.Failed, (int)AccessOutcome.Timeout, (int)OperationState.Pending);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    public async Task InsertUploadAsync(Guid id, Guid actorId, string domain, CancellationToken cancellation)
    {
        await using var command = Create("""
            INSERT INTO operations (id, actor_id, state, storage_domain, expires_at, transfer_deadline)
            VALUES ($1, $2, $3, $4, now() + interval '1 hour', now() + interval '30 seconds')
            ON CONFLICT (id) DO NOTHING
            """, id, actorId, (int)OperationState.Pending, domain);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    public async Task<(OperationState State, string Domain, DateTimeOffset ExpiresAt)?> FindUploadAsync(
        Guid id, CancellationToken cancellation)
    {
        await using var command = Create("""
            SELECT state, storage_domain, expires_at FROM operations WHERE id = $1 AND storage_domain IS NOT NULL
            """, id);
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        return await reader.ReadAsync(cancellation)
            ? ((OperationState)reader.GetInt32(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2))
            : null;
    }

    public async Task<(string Domain, DateTimeOffset ExpiresAt)?> BeginUploadRevertAsync(
        Guid id, string reason, AccessOutcome failure, CancellationToken cancellation, bool discardLauncherArtifact = false)
    {
        await using var command = Create("""
            UPDATE operations SET state = $2,
                finished_at = CASE WHEN state = $10 THEN NULL ELSE finished_at END,
                error_message = COALESCE(error_message,
                    CASE WHEN COALESCE(step_deadline, transfer_deadline, expires_at) <= now() THEN $6 ELSE $3 END),
                outcome = CASE WHEN state = $10 THEN $5 ELSE COALESCE(outcome,
                    CASE WHEN COALESCE(step_deadline, transfer_deadline, expires_at) <= now() THEN $7 ELSE $5 END) END
            WHERE id = $1 AND storage_domain IS NOT NULL AND (
                NOT $8 AND state IN ($4, $2)
                OR $8 AND storage_domain = $9 AND state IN ($4, $2, $10))
            RETURNING storage_domain, expires_at
            """, id, (int)OperationState.Reverting, reason, (int)OperationState.Pending, (int)failure,
            "error.storage_timeout", (int)AccessOutcome.Timeout, discardLauncherArtifact, StorageAddress.LauncherDomain, (int)OperationState.Done);
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        return await reader.ReadAsync(cancellation)
            ? (reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1))
            : null;
    }

    public async Task CompleteUploadAsync(Guid id, bool published, CancellationToken cancellation)
    {
        var previous = published ? OperationState.Pending : OperationState.Reverting;
        var completed = published ? OperationState.Done : OperationState.Failed;
        var outcome = published ? AccessOutcome.NoContent : AccessOutcome.Unavailable;
        await using var command = Create("""
            UPDATE operations SET state = $2, outcome = COALESCE(outcome, $3), finished_at = now()
            WHERE id = $1 AND storage_domain IS NOT NULL AND state = $4
              AND (NOT $5 OR step_deadline > now())
            """, id, (int)completed, (int)outcome, (int)previous, published);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    public async Task<DateTimeOffset?> BeginUploadPublishAsync(Guid id, CancellationToken cancellation)
    {
        await using var command = Create("""
            UPDATE operations SET step_deadline = COALESCE(step_deadline, LEAST(expires_at, now() + interval '30 seconds'))
            WHERE id = $1 AND state = $2 AND storage_domain IS NOT NULL AND expires_at > now()
              AND COALESCE(step_deadline, transfer_deadline, expires_at) > now()
            RETURNING step_deadline
            """, id, (int)OperationState.Pending);
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        return await reader.ReadAsync(cancellation) ? reader.GetFieldValue<DateTimeOffset>(0) : null;
    }

    public async Task ExtendUploadTransferAsync(Guid id, DateTimeOffset progressAt, CancellationToken cancellation)
    {
        await using var command = Create("""
            UPDATE operations
            SET transfer_deadline = LEAST(expires_at, now() + interval '30 seconds', $3 + interval '30 seconds')
            WHERE id = $1 AND state = $2 AND storage_domain IS NOT NULL AND step_deadline IS NULL
              AND COALESCE(transfer_deadline, expires_at) > now()
              AND $3 + interval '30 seconds' > GREATEST(now(), COALESCE(transfer_deadline, now()))
            """, id, (int)OperationState.Pending, progressAt);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    public async Task<IReadOnlyList<Guid>> FindExpiredUploadsAsync(CancellationToken cancellation)
    {
        await using var command = Create("""
            SELECT id FROM operations
            WHERE storage_domain IS NOT NULL AND COALESCE(step_deadline, transfer_deadline, expires_at) <= now() AND state IN ($1, $2)
            ORDER BY COALESCE(step_deadline, transfer_deadline, expires_at) LIMIT 100
            """, (int)OperationState.Pending, (int)OperationState.Reverting);
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        var result = new List<Guid>();
        while (await reader.ReadAsync(cancellation))
            result.Add(reader.GetGuid(0));
        return result;
    }

    private NpgsqlCommand Create(string sql, params object[] parameters) =>
        database.CreateSqlCommand(sql, parameters);
}
