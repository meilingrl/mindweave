namespace Mindweave.Infrastructure.Sqlite.Migrations;

/// <summary>
/// Production schema history. Never edit a released step; append a new one.
/// Tables are STRICT so a wrong column type fails loudly instead of being coerced.
/// </summary>
internal static class SchemaMigrations
{
    public static IReadOnlyList<Migration> All { get; } =
    [
        Migration.Sql(1, "workspaces and last page", """
            CREATE TABLE workspace (
                id          TEXT    NOT NULL PRIMARY KEY,
                name        TEXT    NOT NULL CHECK (length(name) > 0),
                created_at  INTEGER NOT NULL
            ) STRICT;

            -- Last valid page per workspace; back history is runtime-only and never stored.
            CREATE TABLE workspace_location (
                workspace_id TEXT    NOT NULL PRIMARY KEY REFERENCES workspace (id) ON DELETE CASCADE,
                section_key  TEXT    NOT NULL,
                content_key  TEXT,
                updated_at   INTEGER NOT NULL
            ) STRICT;

            -- Small application-wide values, e.g. the last active workspace.
            CREATE TABLE app_state (
                key   TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            ) STRICT;
            """),
    ];
}
