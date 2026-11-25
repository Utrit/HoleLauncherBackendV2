using System.Net;
using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Shared;
using Npgsql;

namespace HoleLauncherBackend.Api;

internal sealed class StorageAdministration(NpgsqlDataSource database, IHttpClientFactory clients)
{
    private const int RetainedNameCount = 5;

    public async Task<Dictionary<Guid, string>> GetTextureLabelsAsync(Guid[] fileIds, CancellationToken cancellation)
    {
        await using var query = database.CreateSqlCommand("""
            SELECT file_id, bool_or(is_skin) FROM (
                SELECT file_id, is_skin FROM game_texture_history WHERE file_id = ANY($1)
                UNION ALL SELECT skin_file_id, true FROM game_profiles WHERE skin_file_id = ANY($1)
                UNION ALL SELECT cape_file_id, false FROM game_profiles WHERE cape_file_id = ANY($1)
            ) AS textures GROUP BY file_id
            """, fileIds);
        var labels = new Dictionary<Guid, string>();
        await using var rows = await query.ExecuteReaderAsync(cancellation);
        while (await rows.ReadAsync(cancellation))
            labels.Add(rows.GetGuid(0), rows.GetBoolean(1) ? "storage.skin" : "storage.cape");
        return labels;
    }

    public async Task<HttpStatusCode> DeleteAsync(string domain, Guid fileId, CancellationToken cancellation)
    {
        if (!StorageAddress.TryGetUserDataOwner(domain, out var accountId))
            return await DeleteFromStorageAsync(domain, fileId, cancellation);

        await using var connection = await database.OpenConnectionAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);
        await transaction.LockStorageFileAsync(fileId, cancellation);
        var result = await IsReferencedAsync(transaction, accountId, fileId, cancellation)
            ? HttpStatusCode.Conflict : await DeleteFromStorageAsync(domain, fileId, cancellation);
        await transaction.CommitAsync(cancellation);
        return result;
    }

    private async Task<HttpStatusCode> DeleteFromStorageAsync(string domain, Guid fileId, CancellationToken cancellation)
    {
        using var response = await clients.CreateClient("storage").DeleteAsync($"files/{domain}/{fileId:D}", cancellation);
        return response.StatusCode;
    }

    private static async Task<bool> IsReferencedAsync(NpgsqlTransaction transaction, Guid accountId, Guid fileId,
        CancellationToken cancellation)
    {
        await using var query = transaction.CreateSqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM game_texture_history WHERE account_id = $1 AND file_id = $2
                    AND state IN ('pending','reverting','complete')
                UNION ALL
                SELECT 1 FROM game_profiles AS profile WHERE profile.account_id = $1 AND (
                    (profile.skin_file_id = $2 AND NOT EXISTS (
                        SELECT 1 FROM game_texture_history WHERE account_id = $1 AND is_skin AND state = 'complete'))
                    OR (profile.cape_file_id = $2 AND NOT EXISTS (
                        SELECT 1 FROM game_texture_history WHERE account_id = $1 AND NOT is_skin AND state = 'complete')))
            )
            """, accountId, fileId);
        return await query.ExecuteScalarAsync(cancellation) is true;
    }

    public async Task<HistoryCleanupResult> CleanHistoryAsync(HistoryCleanupRequest scope, CancellationToken cancellation)
    {
        await using var query = database.CreateSqlCommand("""
            WITH names_to_keep AS (
                SELECT operation_id, row_number() OVER (PARTITION BY account_id ORDER BY sequence DESC) AS position
                FROM game_name_history WHERE state = 'complete' AND ($1 OR account_id = $2)
            ), textures_to_keep AS (
                SELECT operation_id, row_number() OVER (PARTITION BY account_id, is_skin ORDER BY sequence DESC) AS position
                FROM game_texture_history WHERE state = 'complete' AND ($1 OR account_id = $2)
            ), removed_names AS (
                DELETE FROM game_name_history AS item
                WHERE ($1 OR account_id = $2) AND reported AND NOT provision
                    AND (state = 'reverted' OR state = 'complete' AND EXISTS (
                        SELECT 1 FROM names_to_keep AS keep WHERE keep.operation_id = item.operation_id AND keep.position > $3))
                RETURNING operation_id
            ), removed_textures AS (
                DELETE FROM game_texture_history AS item
                WHERE ($1 OR account_id = $2) AND reported
                    AND (state = 'reverted' OR state = 'complete' AND EXISTS (
                        SELECT 1 FROM textures_to_keep AS keep WHERE keep.operation_id = item.operation_id AND keep.position > 1))
                RETURNING operation_id
            )
            SELECT (SELECT count(*)::int FROM removed_names), (SELECT count(*)::int FROM removed_textures)
            """, scope.All, scope.AccountId ?? Guid.Empty, RetainedNameCount);
        await using var row = await query.ExecuteReaderAsync(cancellation);
        await row.ReadAsync(cancellation);
        return new(row.GetInt32(0), row.GetInt32(1));
    }
}
