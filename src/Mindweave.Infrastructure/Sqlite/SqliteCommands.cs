using Microsoft.Data.Sqlite;

namespace Mindweave.Infrastructure.Sqlite;

internal static class SqliteCommands
{
    /// <summary>
    /// Sees the text of every statement created here within the current async flow; for tests that check what a use case
    /// actually reads (architecture review R5), not only the shape of what it returns.
    /// </summary>
    internal static readonly AsyncLocal<Action<string>?> Observer = new();

    /// <summary>Creates a parameterized command; null values are bound as SQL NULL.</summary>
    public static SqliteCommand Command(this SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        Observer.Value?.Invoke(sql);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    public static int Execute(this SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Command(transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    public static T? Scalar<T>(this SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Command(transaction, sql, parameters);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string? NullableString(this SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static long? NullableInt64(this SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static DateTimeOffset Time(this SqliteDataReader reader, int ordinal) => DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(ordinal));

    public static long Ms(this DateTimeOffset value) => value.ToUnixTimeMilliseconds();
}
