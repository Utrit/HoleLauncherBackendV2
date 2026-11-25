using System.Net;
using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Messaging;
using HoleLauncherBackend.Shared;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HoleLauncherBackend.Orchestrator;

internal sealed class GameTextureOperations(
    [FromKeyedServices("game-profiles")] NpgsqlDataSource game,
    DBUtils operations, KafkaBus bus, DraslClient drasl, IConfiguration configuration) : IDisposable
{
    private const int MaxTextureBytes = 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private readonly HttpClient storage = new() { Timeout = RequestTimeout };

    private sealed record Entry(Guid OperationId, Guid AccountId, Guid FileId,
        bool Skin, string Model, bool Reverting, AccessOutcome? Outcome, string Error);

    public async Task EnqueueAsync(OrchestrationMessage message, CancellationToken cancellation)
    {
        if (message.GameTexture is not { } texture || texture.FileId == Guid.Empty || !SkinModels.IsValid(texture.Model) ||
            message.ActorId == Guid.Empty || message.ExpiresAt is not { } expires || expires <= DateTimeOffset.UtcNow)
            return;

        await using var connection = await game.OpenConnectionAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);
        await transaction.LockStorageFileAsync(texture.FileId, cancellation);
        await using var insert = transaction.CreateSqlCommand("""
            INSERT INTO game_texture_history (operation_id, account_id, file_id, is_skin, skin_model)
            VALUES ($1, $2, $3, $4, $5) ON CONFLICT (operation_id) DO NOTHING
            """, message.OperationId, message.ActorId, texture.FileId, texture.Skin, texture.Model);
        await insert.ExecuteNonQueryAsync(cancellation);
        await transaction.CommitAsync(cancellation);
        await operations.InsertGameOperationAsync(message.OperationId, message.ActorId, cancellation);

        var status = await operations.FindOperationAsync(message.OperationId, message.ActorId, cancellation);
        await bus.ReplyAsync(new AccessReply(message.CorrelationId, AccessOutcome.Accepted, Operation: status), cancellation);
    }

    public async Task ProcessNextAsync(CancellationToken cancellation)
    {
        await using var connection = await game.OpenConnectionAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);
        var entry = await FindNextAsync(transaction, cancellation);
        if (entry is null)
            return;

        await using var accountLock = transaction.CreateSqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended($1::uuid::text, 0))", entry.AccountId);
        await accountLock.ExecuteNonQueryAsync(cancellation);

        if (entry.Reverting)
            await RevertAsync(transaction, entry, cancellation);
        else
            await ApplyAsync(transaction, entry, cancellation);
        await transaction.CommitAsync(cancellation);
    }

    private static async Task<Entry?> FindNextAsync(NpgsqlTransaction transaction, CancellationToken cancellation)
    {
        await using var query = transaction.CreateSqlCommand("""
            SELECT operation_id, account_id, file_id, is_skin, skin_model, state,
                   outcome, COALESCE(error_message, '')
            FROM game_texture_history AS entry
            WHERE entry.state IN ('pending','reverting') AND entry.next_attempt_at <= now() AND NOT EXISTS (
                SELECT 1 FROM game_texture_history AS earlier
                WHERE earlier.account_id = entry.account_id AND earlier.sequence < entry.sequence
                    AND earlier.state IN ('pending','reverting')
            )
            ORDER BY entry.sequence LIMIT 1 FOR UPDATE OF entry SKIP LOCKED
            """);
        await using var row = await query.ExecuteReaderAsync(cancellation);
        return await row.ReadAsync(cancellation)
            ? new(row.GetGuid(0), row.GetGuid(1), row.GetGuid(2), row.GetBoolean(3), row.GetString(4),
                row.GetString(5) == "reverting", row.IsDBNull(6) ? null : (AccessOutcome)row.GetInt32(6), row.GetString(7))
            : null;
    }

    private async Task ApplyAsync(NpgsqlTransaction transaction, Entry entry, CancellationToken cancellation)
    {
        byte[]? bytes;
        try
        {
            bytes = await ReadTextureAsync(entry.AccountId, entry.FileId, cancellation);
        }
        catch (Exception error) when (IsNetworkFailure(error, cancellation))
        {
            await UpdateAsync(transaction, entry.OperationId, "reverted", AccessOutcome.Unavailable,
                "error.texture_read", cancellation);
            return;
        }
        if (bytes is null)
        {
            await UpdateAsync(transaction, entry.OperationId, "reverted", AccessOutcome.NotFound,
                "error.texture_missing", cancellation);
            return;
        }

        if (!drasl.IsConfigured)
        {
            await UpdateAsync(transaction, entry.OperationId, "complete", AccessOutcome.NoContent, "", cancellation);
            return;
        }

        try
        {
            var status = await drasl.SetTextureAsync(entry.AccountId, bytes, entry.Skin, entry.Model, cancellation);
            if ((int)status is >= 200 and < 300)
                await UpdateAsync(transaction, entry.OperationId, "complete", AccessOutcome.NoContent, "", cancellation);
            else if ((int)status >= 500)
                await UpdateAsync(transaction, entry.OperationId, "reverting", AccessOutcome.Unavailable,
                    "error.drasl_texture_unconfirmed", cancellation);
            else
                await UpdateAsync(transaction, entry.OperationId, "reverted", AccessOutcome.InvalidRequest,
                    status == HttpStatusCode.NotFound ? "error.drasl_profile_missing" : "error.drasl_texture_rejected", cancellation);
        }
        catch (Exception error) when (IsNetworkFailure(error, cancellation))
        {
            await UpdateAsync(transaction, entry.OperationId, "reverting",
                error is OperationCanceledException ? AccessOutcome.Timeout : AccessOutcome.Unavailable,
                "error.drasl_texture_unconfirmed", cancellation);
        }
    }

    private async Task RevertAsync(NpgsqlTransaction transaction, Entry entry, CancellationToken cancellation)
    {
        var previous = await FindPreviousAsync(transaction, entry, cancellation);
        try
        {
            HttpStatusCode status;
            if (previous.FileId is { } fileId)
            {
                var bytes = await ReadTextureAsync(entry.AccountId, fileId, cancellation);
                status = bytes is null ? HttpStatusCode.ServiceUnavailable
                    : await drasl.SetTextureAsync(entry.AccountId, bytes, entry.Skin, previous.Model, cancellation);
            }
            else
                status = await drasl.ClearTextureAsync(entry.AccountId, entry.Skin, cancellation);

            if ((int)status is >= 200 and < 300 || status == HttpStatusCode.NotFound)
            {
                await UpdateAsync(transaction, entry.OperationId, "reverted",
                    entry.Outcome ?? AccessOutcome.Unavailable, entry.Error, cancellation);
                return;
            }
        }
        catch (Exception error) when (IsNetworkFailure(error, cancellation)) { }

        await using var delay = transaction.CreateSqlCommand("""
            UPDATE game_texture_history SET next_attempt_at = now() + interval '5 seconds'
            WHERE operation_id = $1
            """, entry.OperationId);
        await delay.ExecuteNonQueryAsync(cancellation);
    }

    private static async Task<(Guid? FileId, string Model)> FindPreviousAsync(
        NpgsqlTransaction transaction, Entry entry, CancellationToken cancellation)
    {
        await using var query = transaction.CreateSqlCommand("""
            SELECT COALESCE(previous.file_id, CASE WHEN $2 THEN profile.skin_file_id ELSE profile.cape_file_id END),
                COALESCE(previous.skin_model, model.skin_model, 'classic')
            FROM (SELECT $1 AS account_id) AS owner
            LEFT JOIN game_profiles AS profile USING (account_id)
            LEFT JOIN game_skin_models AS model USING (account_id)
            LEFT JOIN LATERAL (
                SELECT file_id, skin_model FROM game_texture_history
                WHERE account_id = $1 AND is_skin = $2 AND state = 'complete'
                ORDER BY sequence DESC LIMIT 1
            ) AS previous ON true
            """, entry.AccountId, entry.Skin);
        await using var row = await query.ExecuteReaderAsync(cancellation);
        return await row.ReadAsync(cancellation)
            ? (row.IsDBNull(0) ? null : row.GetGuid(0), row.GetString(1))
            : (null, SkinModels.Classic);
    }

    private async Task<byte[]?> ReadTextureAsync(Guid accountId, Guid fileId, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(RequestTimeout);
        var domain = StorageAddress.ResolveDomain(StorageAddress.UserDataDomain, accountId);
        var address = $"{configuration["Storage:Url"]?.TrimEnd('/')}/files/{domain}/{fileId:D}";
        using var response = await storage.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxTextureBytes)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var bytes = new byte[MaxTextureBytes + 1];
        var length = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, deadline.Token);
        return length <= MaxTextureBytes ? bytes.AsSpan(0, length).ToArray() : null;
    }

    private static bool IsNetworkFailure(Exception error, CancellationToken cancellation) =>
        !cancellation.IsCancellationRequested && error is HttpRequestException or IOException or OperationCanceledException;

    private static async Task UpdateAsync(NpgsqlTransaction transaction, Guid id, string state, AccessOutcome outcome,
        string error, CancellationToken cancellation)
    {
        await using var command = transaction.CreateSqlCommand("""
            UPDATE game_texture_history SET state = $2, outcome = $3, error_message = NULLIF($4, '')
            WHERE operation_id = $1
            """, id, state, (int)outcome, error);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    public async Task ReportAsync(CancellationToken cancellation)
    {
        var results = new List<(Guid Id, Guid Account, AccessOutcome Outcome, string Error)>();
        await using (var select = game.CreateSqlCommand("""
            SELECT operation_id, account_id, outcome, COALESCE(error_message, '')
            FROM game_texture_history WHERE state IN ('complete','reverted') AND NOT reported ORDER BY sequence LIMIT 50
            """))
        await using (var rows = await select.ExecuteReaderAsync(cancellation))
            while (await rows.ReadAsync(cancellation))
                results.Add((rows.GetGuid(0), rows.GetGuid(1), (AccessOutcome)rows.GetInt32(2), rows.GetString(3)));

        foreach (var result in results)
        {
            await operations.FinishGameOperationAsync(result.Id, result.Account, result.Outcome, result.Error, cancellation);
            await using var mark = game.CreateSqlCommand(
                "UPDATE game_texture_history SET reported = true WHERE operation_id = $1", result.Id);
            await mark.ExecuteNonQueryAsync(cancellation);
        }
    }

    public void Dispose() => storage.Dispose();
}
