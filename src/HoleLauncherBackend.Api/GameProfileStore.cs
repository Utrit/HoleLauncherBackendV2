using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Shared;
using Npgsql;

namespace HoleLauncherBackend.Api;

internal sealed class GameProfileStore(NpgsqlDataSource database)
{
    public async Task<GameProfile> FindAsync(AccountIdentity account, CancellationToken cancellation)
    {
        await using var query = database.CreateSqlCommand("""
            SELECT COALESCE((
                SELECT game_name FROM game_name_history
                WHERE account_id = $1 AND state = 'complete' ORDER BY sequence DESC LIMIT 1
            ), profile.game_name), COALESCE(skin.file_id, profile.skin_file_id),
                COALESCE(cape.file_id, profile.cape_file_id),
                ARRAY(SELECT recent.game_name FROM game_name_history AS recent
                    WHERE recent.account_id = $1 AND recent.state = 'complete'
                    ORDER BY recent.sequence DESC LIMIT 5), COALESCE(skin.skin_model, model.skin_model)
            FROM (SELECT $1 AS account_id) AS owner
            LEFT JOIN game_profiles AS profile USING (account_id)
            LEFT JOIN game_skin_models AS model USING (account_id)
            LEFT JOIN LATERAL (
                SELECT file_id, skin_model FROM game_texture_history
                WHERE account_id = owner.account_id AND is_skin AND state = 'complete'
                ORDER BY sequence DESC LIMIT 1
            ) AS skin ON true
            LEFT JOIN LATERAL (
                SELECT file_id FROM game_texture_history
                WHERE account_id = owner.account_id AND NOT is_skin AND state = 'complete'
                ORDER BY sequence DESC LIMIT 1
            ) AS cape ON true
            """, account.AccountId);
        await using var row = await query.ExecuteReaderAsync(cancellation);
        return await row.ReadAsync(cancellation)
            ? new GameProfile(row.IsDBNull(0) ? account.UserName : row.GetString(0),
                row.IsDBNull(1) || row.GetGuid(1) == Guid.Empty ? null : row.GetGuid(1),
                row.IsDBNull(2) || row.GetGuid(2) == Guid.Empty ? null : row.GetGuid(2), row.GetFieldValue<string[]>(3), row.IsDBNull(4) ? SkinModels.Classic : row.GetString(4))
            : new GameProfile(null, null, null);
    }

    public async Task<bool?> GetProvisionStatusAsync(Guid accountId, CancellationToken cancellation)
    {
        await using var query = database.CreateSqlCommand("""
            SELECT state FROM game_name_history WHERE operation_id = $1 AND account_id = $1 AND provision AND reported
            """, accountId);
        return (await query.ExecuteScalarAsync(cancellation) as string) switch
        {
            "complete" => true,
            "reverted" => false,
            _ => null
        };
    }
}
