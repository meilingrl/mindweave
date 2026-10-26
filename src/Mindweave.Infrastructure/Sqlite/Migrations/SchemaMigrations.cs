using Mindweave.Application.Diagnostics;

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

        new(3, "message blocks, model capabilities, AI request trace", (connection, transaction) =>
        {
            SqliteConnections.Execute(connection, """
                -- Message content as ordered blocks (docs/evolution.md E2). Kinds beyond 'text' are added in code,
                -- so the kind is validated by the application rather than by a CHECK that would need a rebuild.
                -- data holds the structured payload of non-text kinds as JSON; text blocks leave it NULL.
                CREATE TABLE message_block (
                    message_id TEXT    NOT NULL REFERENCES message (id) ON DELETE CASCADE,
                    ordinal    INTEGER NOT NULL CHECK (ordinal >= 0),
                    kind       TEXT    NOT NULL CHECK (length(kind) > 0),
                    text       TEXT    NOT NULL,
                    data       TEXT,
                    PRIMARY KEY (message_id, ordinal)
                ) STRICT, WITHOUT ROWID;
                INSERT INTO message_block (message_id, ordinal, kind, text)
                SELECT id, 0, 'text', body FROM message;
                ALTER TABLE message DROP COLUMN body;

                -- Declared capabilities as JSON (E4). NULL means the protocol defaults.
                ALTER TABLE model_config ADD COLUMN capabilities TEXT;
                ALTER TABLE generation_task ADD COLUMN capabilities TEXT;

                -- One row per HTTP request to an AI service (E3); a task may make several. No content, no secrets.
                CREATE TABLE ai_request (
                    id                  TEXT    NOT NULL PRIMARY KEY,
                    task_id             TEXT    NOT NULL REFERENCES generation_task (id) ON DELETE CASCADE,
                    started_at          INTEGER NOT NULL,
                    finished_at         INTEGER NOT NULL,
                    response_ms         INTEGER,
                    first_text_ms       INTEGER,
                    outcome             TEXT    NOT NULL CHECK (outcome IN ('completed', 'truncated', 'stopped', 'failed')),
                    http_status         INTEGER,
                    provider_request_id TEXT,
                    error_kind          TEXT,
                    error_detail        TEXT,
                    output_chars        INTEGER NOT NULL CHECK (output_chars >= 0)
                ) STRICT;
                CREATE INDEX ai_request_by_task ON ai_request (task_id, started_at);
                """, transaction);

            // Error details stored before redaction existed may echo a key; scrub them once.
            foreach (var table in new[] { "message", "generation_task" })
            {
                var rows = new List<(string Id, string Detail)>();
                using (var read = connection.Command(transaction, $"SELECT id, error_detail FROM {table} WHERE error_detail IS NOT NULL;"))
                using (var reader = read.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add((reader.GetString(0), reader.GetString(1)));
                    }
                }

                foreach (var (id, detail) in rows)
                {
                    var scrubbed = Redaction.Scrub(detail);
                    if (scrubbed != detail)
                    {
                        connection.Execute(transaction, $"UPDATE {table} SET error_detail = $detail WHERE id = $id;", ("$id", id), ("$detail", scrubbed));
                    }
                }
            }
        }),

        Migration.Sql(4, "branches, ancestor ranges, reading positions, drafts", """
            -- Where a branch came from. The child's parent lives only here (no parent column on conversation).
            -- The anchor is opaque to storage: versioned and tied to the render profile that produced it.
            CREATE TABLE branch_source (
                child_id          TEXT    NOT NULL PRIMARY KEY REFERENCES conversation (id) ON DELETE RESTRICT,
                parent_id         TEXT    NOT NULL REFERENCES conversation (id) ON DELETE RESTRICT,
                source_message_id TEXT    NOT NULL REFERENCES message (id) ON DELETE RESTRICT,
                source_block      INTEGER NOT NULL CHECK (source_block >= 0),
                quote             TEXT    NOT NULL CHECK (length(quote) > 0),
                anchor            TEXT    NOT NULL,
                anchor_version    INTEGER NOT NULL,
                render_profile    TEXT    NOT NULL,
                created_at        INTEGER NOT NULL,
                CHECK (child_id <> parent_id)
            ) STRICT;
            CREATE INDEX branch_source_by_parent ON branch_source (parent_id, created_at);
            CREATE INDEX branch_source_by_message ON branch_source (source_message_id);

            -- The ancestor messages a branch may use, frozen at creation: one row per ancestor, copied from the
            -- parent plus the parent itself, so reading the chain needs no recursion.
            CREATE TABLE branch_context_range (
                child_id    TEXT    NOT NULL REFERENCES conversation (id) ON DELETE CASCADE,
                ancestor_id TEXT    NOT NULL REFERENCES conversation (id) ON DELETE RESTRICT,
                depth       INTEGER NOT NULL CHECK (depth >= 0),
                max_seq     INTEGER NOT NULL CHECK (max_seq >= 0),
                PRIMARY KEY (child_id, ancestor_id),
                UNIQUE (child_id, depth),
                CHECK (child_id <> ancestor_id)
            ) STRICT, WITHOUT ROWID;

            -- Page state per conversation and viewer slot (evolution seam E1); pure dependent rows.
            CREATE TABLE reading_position (
                conversation_id TEXT    NOT NULL REFERENCES conversation (id) ON DELETE CASCADE,
                slot_key        TEXT    NOT NULL CHECK (length(slot_key) > 0),
                message_id      TEXT    NOT NULL REFERENCES message (id) ON DELETE CASCADE,
                block_ordinal   INTEGER NOT NULL CHECK (block_ordinal >= 0),
                locator         TEXT,
                locator_version INTEGER NOT NULL,
                updated_at      INTEGER NOT NULL,
                PRIMARY KEY (conversation_id, slot_key)
            ) STRICT, WITHOUT ROWID;

            CREATE TABLE draft (
                conversation_id TEXT    NOT NULL REFERENCES conversation (id) ON DELETE CASCADE,
                slot_key        TEXT    NOT NULL CHECK (length(slot_key) > 0),
                text            TEXT    NOT NULL CHECK (length(text) > 0),
                updated_at      INTEGER NOT NULL,
                PRIMARY KEY (conversation_id, slot_key)
            ) STRICT, WITHOUT ROWID;
            """),

        Migration.Sql(5, "context summaries, task summary inputs, reported token usage", """
            -- Background notes standing in for older context (architecture §6.2). Owned by the conversation whose
            -- messages they cover (MW-C02), so siblings share them; reused only on an exact key match.
            CREATE TABLE context_summary (
                id              TEXT    NOT NULL PRIMARY KEY,
                conversation_id TEXT    NOT NULL REFERENCES conversation (id) ON DELETE CASCADE,
                from_seq        INTEGER NOT NULL CHECK (from_seq >= 0),
                to_seq          INTEGER NOT NULL,
                input_hash      TEXT    NOT NULL,
                prompt_version  INTEGER NOT NULL,
                model           TEXT    NOT NULL,
                text            TEXT    NOT NULL CHECK (length(text) > 0),
                created_at      INTEGER NOT NULL,
                UNIQUE (conversation_id, from_seq, to_seq, input_hash, prompt_version),
                CHECK (to_seq >= from_seq)
            ) STRICT;

            -- What a task actually sent besides raw messages (MW-C16).
            ALTER TABLE generation_task ADD COLUMN input_summary_ids TEXT NOT NULL DEFAULT '[]';

            -- Token counts as reported by the service, when it reports them; checks the local estimate.
            ALTER TABLE ai_request ADD COLUMN prompt_tokens INTEGER;
            ALTER TABLE ai_request ADD COLUMN completion_tokens INTEGER;
            """),
    ];
}
