using System.Text.Json;
using Mindweave.Application.Storage;

namespace Mindweave.Infrastructure.Sqlite;

internal sealed class SqliteModelSettingsStore(SqliteDatabase database) : IModelSettingsStore
{
    private const string ActiveKey = "active_model_config_id";
    private const string RetiredKey = "retired_credential_refs";

    public Task<ModelSettings?> GetActiveAsync(CancellationToken cancellationToken) =>
        database.ReadAsync(
            connection =>
            {
                using var command = connection.Command(null, """
                    SELECT c.id, c.name, c.base_url, c.protocol, c.credential_ref, m.id, m.model, m.max_output_tokens
                    FROM app_state s
                    JOIN model_config m ON m.id = s.value
                    JOIN service_connection c ON c.id = m.connection_id
                    WHERE s.key = $key;
                    """, ("$key", ActiveKey));
                using var reader = command.ExecuteReader();
                return reader.Read()
                    ? new ModelSettings(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        SqlNames.ParseProtocol(reader.GetString(3)),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetString(6),
                        reader.NullableInt64(7) is { } max ? (int)max : null)
                    : null;
            },
            cancellationToken);

    public Task SaveActiveAsync(ModelSettings settings, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                connection.Execute(transaction, """
                    INSERT INTO service_connection (id, name, base_url, protocol, credential_ref, created_at, updated_at)
                    VALUES ($id, $name, $url, $protocol, $cred, $at, $at)
                    ON CONFLICT (id) DO UPDATE SET name = excluded.name, base_url = excluded.base_url,
                        protocol = excluded.protocol, credential_ref = excluded.credential_ref, updated_at = excluded.updated_at;
                    """,
                    ("$id", settings.ConnectionId), ("$name", settings.ConnectionName), ("$url", settings.BaseUrl),
                    ("$protocol", SqlNames.Of(settings.Protocol)), ("$cred", settings.CredentialRef), ("$at", at.Ms()));
                connection.Execute(transaction, """
                    INSERT INTO model_config (id, connection_id, model, max_output_tokens, created_at, updated_at)
                    VALUES ($id, $conn, $model, $max, $at, $at)
                    ON CONFLICT (id) DO UPDATE SET connection_id = excluded.connection_id, model = excluded.model,
                        max_output_tokens = excluded.max_output_tokens, updated_at = excluded.updated_at;
                    """,
                    ("$id", settings.ModelConfigId), ("$conn", settings.ConnectionId), ("$model", settings.Model),
                    ("$max", settings.MaxOutputTokens), ("$at", at.Ms()));
                return connection.Execute(transaction,
                    "INSERT INTO app_state (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
                    ("$key", ActiveKey), ("$value", settings.ModelConfigId));
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<string>> RetiredCredentialRefsAsync(CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<string>>(
            connection =>
            {
                var json = connection.Scalar<string>(null, "SELECT value FROM app_state WHERE key = $key;", ("$key", RetiredKey));
                return json is null ? [] : JsonSerializer.Deserialize<string[]>(json) ?? [];
            },
            cancellationToken);

    public Task SetRetiredCredentialRefsAsync(IReadOnlyList<string> references, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) => connection.Execute(transaction,
                "INSERT INTO app_state (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
                ("$key", RetiredKey), ("$value", JsonSerializer.Serialize(references))),
            cancellationToken);
}
