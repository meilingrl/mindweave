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

        Migration.Sql(2, "conversations, messages, generation tasks, model settings", """
            CREATE TABLE conversation (
                id           TEXT    NOT NULL PRIMARY KEY,
                workspace_id TEXT    NOT NULL REFERENCES workspace (id) ON DELETE RESTRICT,
                title        TEXT,
                title_source TEXT    NOT NULL CHECK (title_source IN ('none', 'auto', 'manual')),
                created_at   INTEGER NOT NULL,
                updated_at   INTEGER NOT NULL,
                CHECK ((title_source = 'none') = (title IS NULL))
            ) STRICT;
            CREATE INDEX conversation_by_workspace ON conversation (workspace_id, updated_at DESC);

            -- Bodies are Markdown source. A terminal answer's body never changes; revision only grows.
            CREATE TABLE message (
                id              TEXT    NOT NULL PRIMARY KEY,
                conversation_id TEXT    NOT NULL REFERENCES conversation (id) ON DELETE RESTRICT,
                seq             INTEGER NOT NULL CHECK (seq >= 0),
                role            TEXT    NOT NULL CHECK (role IN ('user', 'assistant')),
                body            TEXT    NOT NULL,
                state           TEXT    NOT NULL CHECK (state IN ('complete', 'pending', 'streaming', 'stopped', 'failed', 'interrupted', 'cancelled')),
                revision        INTEGER NOT NULL DEFAULT 0,
                supersedes_id   TEXT    REFERENCES message (id) ON DELETE RESTRICT,
                error_kind      TEXT,
                error_detail    TEXT,
                created_at      INTEGER NOT NULL,
                completed_at    INTEGER,
                UNIQUE (conversation_id, seq),
                CHECK (role = 'assistant' OR state = 'complete'),
                CHECK (supersedes_id IS NULL OR role = 'assistant')
            ) STRICT;

            CREATE TABLE service_connection (
                id             TEXT    NOT NULL PRIMARY KEY,
                name           TEXT    NOT NULL,
                base_url       TEXT    NOT NULL,
                protocol       TEXT    NOT NULL CHECK (protocol IN ('chat_completions')),
                credential_ref TEXT    NOT NULL,
                created_at     INTEGER NOT NULL,
                updated_at     INTEGER NOT NULL
            ) STRICT;

            CREATE TABLE model_config (
                id                TEXT    NOT NULL PRIMARY KEY,
                connection_id     TEXT    NOT NULL REFERENCES service_connection (id) ON DELETE RESTRICT,
                model             TEXT    NOT NULL CHECK (length(model) > 0),
                max_output_tokens INTEGER,
                created_at        INTEGER NOT NULL,
                updated_at        INTEGER NOT NULL
            ) STRICT;

            -- The snapshot columns freeze what the task was accepted with; settings edits never change them.
            CREATE TABLE generation_task (
                id                TEXT    NOT NULL PRIMARY KEY,
                kind              TEXT    NOT NULL CHECK (kind IN ('answer', 'title')),
                conversation_id   TEXT    NOT NULL REFERENCES conversation (id) ON DELETE RESTRICT,
                target_message_id TEXT    REFERENCES message (id) ON DELETE RESTRICT,
                state             TEXT    NOT NULL CHECK (state IN ('queued', 'running', 'completed', 'stopped', 'failed', 'interrupted', 'cancelled')),
                model_config_id   TEXT    NOT NULL,
                base_url          TEXT    NOT NULL,
                protocol          TEXT    NOT NULL,
                model             TEXT    NOT NULL,
                credential_ref    TEXT    NOT NULL,
                max_output_tokens INTEGER,
                input_message_ids TEXT    NOT NULL,
                error_kind        TEXT,
                error_detail      TEXT,
                created_at        INTEGER NOT NULL,
                started_at        INTEGER,
                finished_at       INTEGER,
                CHECK (kind <> 'answer' OR target_message_id IS NOT NULL)
            ) STRICT;
            CREATE INDEX generation_task_unfinished ON generation_task (state) WHERE state IN ('queued', 'running');
            """),
    ];
}
