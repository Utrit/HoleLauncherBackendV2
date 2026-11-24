using Npgsql;

namespace HoleLauncherBackend.Shared;

public static class PostgresCommands
{
    public static NpgsqlCommand CreateSqlCommand(this NpgsqlDataSource source, string sql, params object[] parameters) =>
        Bind(source.CreateCommand(sql), parameters);

    public static NpgsqlCommand CreateSqlCommand(this NpgsqlTransaction transaction, string sql, params object[] parameters) =>
        Bind(new NpgsqlCommand(sql, transaction.Connection, transaction), parameters);

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
