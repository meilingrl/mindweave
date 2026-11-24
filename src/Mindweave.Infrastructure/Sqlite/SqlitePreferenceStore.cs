using Mindweave.Application.Storage;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>Preferences as plain <c>app_state</c> rows under a prefix; no migration needed.</summary>
internal sealed class SqlitePreferenceStore(SqliteDatabase database) : IPreferenceStore
{
    internal const string Prefix = "pref:";

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return database.ReadAsync(
            connection => connection.Scalar<string>(null, "SELECT value FROM app_state WHERE key = $key;", ("$key", Prefix + key)),
            cancellationToken);
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        return database.WriteAsync(
            (connection, transaction) => connection.Execute(transaction,
                "INSERT INTO app_state (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
                ("$key", Prefix + key), ("$value", value)),
            cancellationToken);
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return database.WriteAsync(
            (connection, transaction) => connection.Execute(transaction, "DELETE FROM app_state WHERE key = $key;", ("$key", Prefix + key)),
            cancellationToken);
    }
}
