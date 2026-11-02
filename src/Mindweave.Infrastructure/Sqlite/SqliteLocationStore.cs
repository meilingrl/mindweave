using Mindweave.Application;
using Mindweave.Application.Storage;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Sqlite;

internal sealed class SqliteLocationStore(SqliteDatabase database, IClock clock) : ILocationStore
{
    private const string LastWorkspaceKey = SqliteWorkspaceStore.LastWorkspaceKey;

    public Task<PageLocation?> GetLastAsync(CancellationToken cancellationToken) =>
        database.ReadAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT l.workspace_id, l.section_key, l.content_key
                    FROM app_state s
                    JOIN workspace_location l ON l.workspace_id = s.value
                    WHERE s.key = $key;
                    """;
                command.Parameters.AddWithValue("$key", LastWorkspaceKey);
                using var reader = command.ExecuteReader();
                return reader.Read()
                    ? new PageLocation(
                        WorkspaceId.Parse(reader.GetString(0)),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2))
                    : null;
            },
            cancellationToken);

    public Task SetLastAsync(PageLocation location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        return database.WriteAsync(
            (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO workspace_location (workspace_id, section_key, content_key, updated_at)
                    VALUES ($ws, $section, $content, $now)
                    ON CONFLICT (workspace_id) DO UPDATE
                        SET section_key = excluded.section_key,
                            content_key = excluded.content_key,
                            updated_at  = excluded.updated_at;
                    INSERT INTO app_state (key, value) VALUES ($key, $ws)
                    ON CONFLICT (key) DO UPDATE SET value = excluded.value;
                    """;
                command.Parameters.AddWithValue("$ws", location.WorkspaceId.ToString());
                command.Parameters.AddWithValue("$section", location.SectionKey);
                command.Parameters.AddWithValue("$content", (object?)location.ContentKey ?? DBNull.Value);
                command.Parameters.AddWithValue("$now", now);
                command.Parameters.AddWithValue("$key", LastWorkspaceKey);
                return command.ExecuteNonQuery();
            },
            cancellationToken);
    }
}
