using Microsoft.Data.Sqlite;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>Connection strings and per-connection pragmas. Pooling is off so closing a connection really releases the file.</summary>
internal static class SqliteConnections
{
    /// <summary>"MWV1": identifies a Mindweave database independently of its schema version.</summary>
    public const int ApplicationId = 0x4D575631;

    public static SqliteConnection OpenReadWrite(string path, bool create = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5,
        }.ToString());
        return Prepare(connection, "PRAGMA synchronous = FULL; PRAGMA busy_timeout = 5000;");
    }

    public static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        return Prepare(connection, "PRAGMA busy_timeout = 5000;");
    }

    /// <summary>
    /// Opens and configures, disposing on failure. A non-database file fails at the first statement, not at
    /// Open(); without this the handle would leak and keep the file locked on Windows.
    /// </summary>
    private static SqliteConnection Prepare(SqliteConnection connection, string pragmas)
    {
        try
        {
            connection.Open();
            Execute(connection, pragmas);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    public static T Scalar<T>(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        var value = command.ExecuteScalar();
        return (T)Convert.ChangeType(value!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Returns null when the check passes, otherwise the first reported problem.</summary>
    public static string? QuickCheck(SqliteConnection connection)
    {
        var result = Scalar<string>(connection, "PRAGMA quick_check;");
        return result == "ok" ? null : result;
    }

    /// <summary>Returns a description of the first foreign key violation, or null when there is none.</summary>
    public static string? ForeignKeyViolation(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? $"table {reader.GetString(0)} row {reader.GetValue(1)} references missing {reader.GetString(2)}"
            : null;
    }
}
