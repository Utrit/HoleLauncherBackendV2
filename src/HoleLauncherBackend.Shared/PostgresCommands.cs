using Npgsql;

namespace HoleLauncherBackend.Shared;

public static class PostgresCommands
{
    public static NpgsqlCommand CreateSqlCommand(this NpgsqlDataSource source, string sql, params object[] parameters) =>
        Bind(source.CreateCommand(sql), parameters);

    public static NpgsqlCommand CreateSqlCommand(this NpgsqlTransaction transaction, string sql, params object[] parameters) =>
        Bind(new NpgsqlCommand(sql, transaction.Connection, transaction), parameters);

    public static async Task LockStorageFileAsync(this NpgsqlTransaction transaction, Guid fileId, CancellationToken cancellation)
    {
        await using var command = transaction.CreateSqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('storage:' || $1::uuid::text, 0))", fileId);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    private static NpgsqlCommand Bind(NpgsqlCommand command, object[] parameters)
    {
        try
        {
            foreach (var value in parameters)
                command.Parameters.AddWithValue(value);
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }
}
