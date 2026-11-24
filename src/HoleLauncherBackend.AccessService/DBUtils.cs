using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Shared;
using Npgsql;

namespace HoleLauncherBackend.AccessService;

internal sealed class DBUtils(NpgsqlDataSource database)
{
    internal sealed record LoginData(AccountIdentity Account, byte[] Hash, byte[] Salt, int Iterations);

    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellation) =>
        database.OpenConnectionAsync(cancellation);

    public async Task<bool> InsertAccountAsync(AccountIdentity account, byte[] hash, byte[] salt,
        int iterations, CancellationToken cancellation)
    {
        await using var command = Create("""
            INSERT INTO accounts (id, user_name, normalized_user_name, password_hash, password_salt, password_iterations)
            VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT (normalized_user_name) DO NOTHING
            """, account.AccountId, account.UserName, account.UserName.ToUpperInvariant(), hash, salt, iterations);
        return await command.ExecuteNonQueryAsync(cancellation) == 1;
    }

    public async Task<LoginData?> ReadLoginAsync(string userName, CancellationToken cancellation)
    {
        await using var command = Create("""
            SELECT id, user_name, permissions, password_hash, password_salt, password_iterations
            FROM accounts WHERE normalized_user_name = $1 AND NOT suppressed
            """, userName.ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        return await reader.ReadAsync(cancellation)
            ? new LoginData(ReadAccount(reader), reader.GetFieldValue<byte[]>(3), reader.GetFieldValue<byte[]>(4), reader.GetInt32(5))
            : null;
    }

    public async Task<bool> InsertSessionAsync(string lookup, Guid accountId, byte[] expectedHash,
        CancellationToken cancellation)
    {
        await using var command = Create("""
            INSERT INTO account_sessions (session_lookup, account_id, expires_at)
            SELECT $1, id, now() + interval '8 hours'
            FROM accounts WHERE id = $2 AND password_hash = $3 AND NOT suppressed FOR UPDATE
            """, lookup, accountId, expectedHash);
        return await command.ExecuteNonQueryAsync(cancellation) == 1;
    }

    public async Task<AccountSession?> FindSessionAsync(string lookup, CancellationToken cancellation)
    {
        await using var command = Create("""
            SELECT accounts.id, accounts.user_name, accounts.permissions, account_sessions.expires_at
            FROM account_sessions JOIN accounts ON accounts.id = account_sessions.account_id
            WHERE session_lookup = $1 AND NOT accounts.suppressed
            """, lookup);
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        return await reader.ReadAsync(cancellation)
            ? new AccountSession(ReadAccount(reader), reader.GetFieldValue<DateTimeOffset>(3))
            : null;
    }

    public async Task<AccountIdentity[]> ListAccountsAsync(CancellationToken cancellation)
    {
        await using var command = Create("SELECT id, user_name, permissions, suppressed FROM accounts ORDER BY normalized_user_name");
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        var accounts = new List<AccountIdentity>();
        while (await reader.ReadAsync(cancellation))
            accounts.Add(ReadAccount(reader, reader.GetBoolean(3)));

        return accounts.ToArray();
    }

    public async Task RevokeSessionAsync(string lookup, CancellationToken cancellation)
    {
        await using var command = Create("DELETE FROM account_sessions WHERE session_lookup = $1", lookup);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    public static async Task LockAccountsAsync(NpgsqlTransaction transaction, Guid[] accountIds, CancellationToken cancellation)
    {
        await using var command = Create(transaction,
            "SELECT id FROM accounts WHERE id = ANY($1) ORDER BY id FOR UPDATE", accountIds);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    public static async Task<bool> UpdatePasswordAsync(NpgsqlTransaction transaction, Guid accountId,
        string lookup, byte[] hash, byte[] salt, int iterations, CancellationToken cancellation)
    {
        await using var command = Create(transaction, """
            UPDATE accounts SET password_hash = $2, password_salt = $3, password_iterations = $4
            WHERE id = $1 AND NOT suppressed AND EXISTS (
                SELECT 1 FROM account_sessions WHERE session_lookup = $5
                AND account_id = $1 AND expires_at > statement_timestamp()
            )
            """, accountId, hash, salt, iterations, lookup);
        return await command.ExecuteNonQueryAsync(cancellation) == 1;
    }

    public static async Task RevokeSessionsAsync(NpgsqlTransaction transaction, Guid accountId,
        CancellationToken cancellation, string? exceptLookup = null)
    {
        await using var command = exceptLookup is null
            ? Create(transaction, "DELETE FROM account_sessions WHERE account_id = $1", accountId)
            : Create(transaction, "DELETE FROM account_sessions WHERE account_id = $1 AND session_lookup <> $2",
                accountId, exceptLookup);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    public static async Task<bool> TryClaimOperationAsync(NpgsqlTransaction transaction,
        Guid operationId, CancellationToken cancellation)
    {
        await using var command = Create(transaction, """
            INSERT INTO account_operation_receipts (operation_id, outcome) VALUES ($1, NULL)
            ON CONFLICT (operation_id) DO NOTHING
            """, operationId);
        return await command.ExecuteNonQueryAsync(cancellation) == 1;
    }

    public static async Task<AccessOutcome> ReadOperationOutcomeAsync(NpgsqlTransaction transaction,
        Guid operationId, CancellationToken cancellation)
    {
        await using var command = Create(transaction,
            "SELECT outcome FROM account_operation_receipts WHERE operation_id = $1", operationId);
        return (AccessOutcome)(int)(await command.ExecuteScalarAsync(cancellation))!;
    }

    public static async Task SaveOperationOutcomeAsync(NpgsqlTransaction transaction,
        Guid operationId, AccessOutcome outcome, CancellationToken cancellation)
    {
        await using var command = Create(transaction,
            "UPDATE account_operation_receipts SET outcome = $2 WHERE operation_id = $1", operationId, (int)outcome);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    public static async Task<bool> HasPermissionAsync(NpgsqlTransaction transaction, Guid accountId,
        string permission, CancellationToken cancellation)
    {
        await using var command = Create(transaction,
            "SELECT $2 = ANY(permissions) FROM accounts WHERE id = $1 AND NOT suppressed", accountId, permission);
        return await command.ExecuteScalarAsync(cancellation) is true;
    }

    public static Task<bool> UpdatePermissionsAsync(NpgsqlTransaction transaction, Guid accountId,
        string[] permissions, CancellationToken cancellation) =>
        UpdateAsync(transaction, "UPDATE accounts SET permissions = $2 WHERE id = $1", accountId, permissions, cancellation);

    public static Task<bool> UpdateSuppressionAsync(NpgsqlTransaction transaction, Guid accountId,
        bool suppressed, CancellationToken cancellation) =>
        UpdateAsync(transaction, "UPDATE accounts SET suppressed = $2 WHERE id = $1", accountId, suppressed, cancellation);

    private static async Task<bool> UpdateAsync(NpgsqlTransaction transaction, string sql, Guid accountId,
        object value, CancellationToken cancellation)
    {
        await using var command = Create(transaction, sql, accountId, value);
        return await command.ExecuteNonQueryAsync(cancellation) == 1;
    }

    private NpgsqlCommand Create(string sql, params object[] parameters) =>
        database.CreateSqlCommand(sql, parameters);

    private static NpgsqlCommand Create(NpgsqlTransaction transaction, string sql, params object[] parameters) =>
        transaction.CreateSqlCommand(sql, parameters);

    private static AccountIdentity ReadAccount(NpgsqlDataReader reader, bool suppressed = false) =>
        new(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<string[]>(2), suppressed);
}
