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

        Migration.Sql(6, "summary nodes, versions, read states, edit drafts; summary and naming tasks", """
            -- Task kinds gain 'summary' and 'naming'; a CHECK can only change by rebuilding the table (foreign keys
            -- are off during migrations and checked before commit). A summary task records the frozen coverage and
            -- the version that was current when it was accepted (the adoption baseline). The baseline is only an
            -- identity: versions may be deleted later, so it carries no foreign key.
            CREATE TABLE generation_task_v6 (
                id                TEXT    NOT NULL PRIMARY KEY,
                kind              TEXT    NOT NULL CHECK (kind IN ('answer', 'title', 'summary', 'naming')),
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
                capabilities      TEXT,
                input_summary_ids TEXT    NOT NULL DEFAULT '[]',
                summary_to_seq    INTEGER,
                summary_baseline  TEXT,
                CHECK (kind <> 'answer' OR target_message_id IS NOT NULL),
                CHECK (kind <> 'summary' OR summary_to_seq IS NOT NULL)
            ) STRICT;
            INSERT INTO generation_task_v6 (
                id, kind, conversation_id, target_message_id, state, model_config_id, base_url, protocol, model,
                credential_ref, max_output_tokens, input_message_ids, error_kind, error_detail, created_at, started_at,
                finished_at, capabilities, input_summary_ids)
            SELECT
                id, kind, conversation_id, target_message_id, state, model_config_id, base_url, protocol, model,
                credential_ref, max_output_tokens, input_message_ids, error_kind, error_detail, created_at, started_at,
                finished_at, capabilities, input_summary_ids
            FROM generation_task;
            DROP TABLE generation_task;
            ALTER TABLE generation_task_v6 RENAME TO generation_task;
            CREATE INDEX generation_task_unfinished ON generation_task (state) WHERE state IN ('queued', 'running');
            CREATE INDEX generation_task_by_conversation ON generation_task (conversation_id, kind, created_at);

            -- One stable node per source conversation (architecture §8). The node and its versions reference each
            -- other, so these keys are checked at commit; the current version is set in the transaction that
            -- creates the node. Deleting the last saved version deletes the node; summarizing again makes a new one.
            CREATE TABLE summary_node (
                id                 TEXT    NOT NULL PRIMARY KEY,
                workspace_id       TEXT    NOT NULL REFERENCES workspace (id) ON DELETE RESTRICT,
                conversation_id    TEXT    NOT NULL UNIQUE REFERENCES conversation (id) ON DELETE RESTRICT,
                current_version_id TEXT    NOT NULL REFERENCES summary_version (id) DEFERRABLE INITIALLY DEFERRED,
                pending_version_id TEXT    REFERENCES summary_version (id) DEFERRABLE INITIALLY DEFERRED,
                base_title         TEXT    NOT NULL CHECK (length(base_title) > 0),
                display_name       TEXT    NOT NULL CHECK (length(display_name) > 0),
                name_source        TEXT    NOT NULL CHECK (name_source IN ('base', 'auto', 'manual')),
                created_at         INTEGER NOT NULL,
                updated_at         INTEGER NOT NULL,
                CHECK (pending_version_id IS NULL OR pending_version_id <> current_version_id)
            ) STRICT;
            CREATE INDEX summary_node_by_workspace ON summary_node (workspace_id);

            -- Full text per version, never a diff. The key to the node is NO ACTION, not RESTRICT: SQLite checks RESTRICT
            -- immediately even when deferred, and deleting the last version deletes the node in the same transaction. Coverage is the source conversation up to seq to_seq; an edit
            -- keeps its baseline's coverage. The task is kept as an identity only.
            CREATE TABLE summary_version (
                id          TEXT    NOT NULL PRIMARY KEY,
                node_id     TEXT    NOT NULL REFERENCES summary_node (id) DEFERRABLE INITIALLY DEFERRED,
                seq         INTEGER NOT NULL CHECK (seq >= 1),
                title       TEXT    NOT NULL CHECK (length(title) > 0),
                body        TEXT    NOT NULL CHECK (length(body) > 0),
                to_seq      INTEGER NOT NULL CHECK (to_seq >= 0),
                origin      TEXT    NOT NULL CHECK (origin IN ('generated', 'edited')),
                baseline_id TEXT,
                task_id     TEXT,
                model       TEXT,
                created_at  INTEGER NOT NULL,
                UNIQUE (node_id, seq)
            ) STRICT;

            -- Read state per version (U2): read by progress past the threshold, by the manual toggle, or by saving
            -- an edit. Dependent rows, removed with their version.
            CREATE TABLE summary_read_state (
                version_id    TEXT    NOT NULL PRIMARY KEY REFERENCES summary_version (id) ON DELETE CASCADE,
                read_source   TEXT    CHECK (read_source IN ('progress', 'manual', 'edit')),
                max_progress  REAL    NOT NULL DEFAULT 0 CHECK (max_progress BETWEEN 0 AND 1),
                locator       TEXT,
                updated_at    INTEGER NOT NULL
            ) STRICT, WITHOUT ROWID;

            -- At most one autosaved edit per node, against the version it started from; not a version.
            CREATE TABLE summary_edit_draft (
                node_id     TEXT    NOT NULL PRIMARY KEY REFERENCES summary_node (id) ON DELETE CASCADE,
                baseline_id TEXT    NOT NULL REFERENCES summary_version (id) ON DELETE RESTRICT,
                title       TEXT    NOT NULL,
                body        TEXT    NOT NULL,
                updated_at  INTEGER NOT NULL
            ) STRICT, WITHOUT ROWID;
            """),

        Migration.Sql(7, "summary short titles, network node layout", """
            -- The label a network node shows (S4 trial feedback); versions made before have none and fall back to
            -- the start of their title.
            ALTER TABLE summary_version ADD COLUMN short_title TEXT;

            -- Saved node positions (architecture §11.2): one row per node, the centre in absolute canvas
            -- coordinates. The viewport is never stored. Dependent rows, removed with their node.
            CREATE TABLE node_layout (
                node_id      TEXT    NOT NULL PRIMARY KEY REFERENCES summary_node (id) ON DELETE CASCADE,
                workspace_id TEXT    NOT NULL REFERENCES workspace (id) ON DELETE RESTRICT,
                x            REAL    NOT NULL,
                y            REAL    NOT NULL,
                updated_at   INTEGER NOT NULL
            ) STRICT, WITHOUT ROWID;
            CREATE INDEX node_layout_by_workspace ON node_layout (workspace_id);
            """),

        Migration.Sql(8, "conversation archive", """
            -- One row means the user archived this conversation (S6, D-021). A conversation hidden because an
            -- ancestor is archived has no row of its own: that is derived on read from branch_context_range, so
            -- marks are never written to descendants and restoring a parent leaves separately archived children be.
            CREATE TABLE conversation_archive (
                conversation_id TEXT    NOT NULL PRIMARY KEY REFERENCES conversation (id) ON DELETE CASCADE,
                archived_at     INTEGER NOT NULL
            ) STRICT, WITHOUT ROWID;

            -- A conversation's subtree (archiving checks it for running work; restoring counts what reappears).
            CREATE INDEX branch_context_range_by_ancestor ON branch_context_range (ancestor_id);
            """),

        Migration.Sql(9, "request token estimates", """
            -- The app's own token estimate of each request as sent, beside the prompt tokens the service billed, so the
            -- context budget can learn how each model counts (TokenCalibration, 2026-11-05). Older requests have none.
            ALTER TABLE ai_request ADD COLUMN estimated_tokens INTEGER CHECK (estimated_tokens >= 0);

            -- The newest requests of one address and model are read before each plan; tasks are found by model, then
            -- their requests by the existing per-task index.
            CREATE INDEX generation_task_by_model ON generation_task (model, base_url);
            """),

        Migration.Sql(10, "requests without a task, counted for a workspace", """
            -- A request may be made without a task row (2026-11-11: a workspace's suggested names, asked for or prepared in
            -- the background): it is then counted for its workspace and a kind, with its own model, so the statistics page
            -- can count its tokens. Exactly one of task and workspace. The kind is checked by the application, as message
            -- blocks are (a CHECK on its values would need another rebuild for each new kind). The workspace's requests go
            -- with it, as a task's go with the task. task_id can only lose NOT NULL by rebuilding the table; nothing
            -- references ai_request, so the rebuild touches no other table.
            CREATE TABLE ai_request_v10 (
                id                  TEXT    NOT NULL PRIMARY KEY,
                task_id             TEXT    REFERENCES generation_task (id) ON DELETE CASCADE,
                workspace_id        TEXT    REFERENCES workspace (id) ON DELETE CASCADE,
                kind                TEXT    CHECK (length(kind) > 0),
                model               TEXT,
                base_url            TEXT,
                started_at          INTEGER NOT NULL,
                finished_at         INTEGER NOT NULL,
                response_ms         INTEGER,
                first_text_ms       INTEGER,
                outcome             TEXT    NOT NULL CHECK (outcome IN ('completed', 'truncated', 'stopped', 'failed')),
                http_status         INTEGER,
                provider_request_id TEXT,
                error_kind          TEXT,
                error_detail        TEXT,
                output_chars        INTEGER NOT NULL CHECK (output_chars >= 0),
                prompt_tokens       INTEGER,
                completion_tokens   INTEGER,
                estimated_tokens    INTEGER CHECK (estimated_tokens >= 0),
                CHECK ((task_id IS NULL) <> (workspace_id IS NULL)),
                CHECK (task_id IS NOT NULL OR (kind IS NOT NULL AND model IS NOT NULL AND base_url IS NOT NULL)),
                CHECK (task_id IS NULL OR (kind IS NULL AND model IS NULL AND base_url IS NULL))
            ) STRICT;
            INSERT INTO ai_request_v10 (
                id, task_id, started_at, finished_at, response_ms, first_text_ms, outcome, http_status, provider_request_id,
                error_kind, error_detail, output_chars, prompt_tokens, completion_tokens, estimated_tokens)
            SELECT
                id, task_id, started_at, finished_at, response_ms, first_text_ms, outcome, http_status, provider_request_id,
                error_kind, error_detail, output_chars, prompt_tokens, completion_tokens, estimated_tokens
            FROM ai_request;
            DROP TABLE ai_request;
            ALTER TABLE ai_request_v10 RENAME TO ai_request;
            CREATE INDEX ai_request_by_task ON ai_request (task_id, started_at);
            CREATE INDEX ai_request_by_workspace ON ai_request (workspace_id, started_at) WHERE workspace_id IS NOT NULL;
            """),

        Migration.Sql(11, "recommendations", """
            -- Recommended questions for 新探索 and recommended network nodes (requirements §10.3, architecture
            -- "Recommendation"): hints, not knowledge. Kept per workspace across restarts; a pending one is shown, a used
            -- or ignored one is kept only so it is not offered again. An ignored row keeps only the normalized question it
            -- is matched by (no reason). The basis summary is required for a node and kept for a question that shows
            -- "延伸自"; deleting that summary deletes its recommendations, ignored ones included, and deleting the workspace
            -- deletes all of them. generation_id names the request that made the row, generated_at when.
            CREATE TABLE recommendation (
                id            TEXT    NOT NULL PRIMARY KEY,
                workspace_id  TEXT    NOT NULL REFERENCES workspace (id) ON DELETE CASCADE,
                kind          TEXT    NOT NULL CHECK (kind IN ('question', 'node')),
                question      TEXT    NOT NULL CHECK (length(question) > 0),
                reason        TEXT,
                basis_node_id TEXT    REFERENCES summary_node (id) ON DELETE CASCADE,
                generation_id TEXT    NOT NULL,
                generated_at  INTEGER NOT NULL,
                seq           INTEGER NOT NULL CHECK (seq >= 0),
                state         TEXT    NOT NULL CHECK (state IN ('pending', 'used', 'ignored')),
                updated_at    INTEGER NOT NULL,
                CHECK (kind = 'question' OR basis_node_id IS NOT NULL),
                CHECK (state <> 'ignored' OR reason IS NULL)
            ) STRICT;
            CREATE INDEX recommendation_by_workspace ON recommendation (workspace_id, kind, state);
            CREATE INDEX recommendation_by_basis ON recommendation (basis_node_id) WHERE basis_node_id IS NOT NULL;
            """),

        Migration.Sql(12, "foreign keys indexed on their child columns", """
            -- Health check 2026-11-12 (storage-2): deleting a parent row checks every child table for rows that point at
            -- it, and without an index on the child column that is a scan per deleted row. Deleting a workspace of 8,000
            -- messages took 38 s and held the only writer; with these indexes 0.1 s. Indexes only: no data changes.
            CREATE INDEX message_by_supersedes ON message (supersedes_id);
            CREATE INDEX reading_position_by_message ON reading_position (message_id);
            CREATE INDEX generation_task_by_target ON generation_task (target_message_id);
            CREATE INDEX summary_node_by_current ON summary_node (current_version_id);
            CREATE INDEX summary_node_by_pending ON summary_node (pending_version_id);
            CREATE INDEX summary_edit_draft_by_baseline ON summary_edit_draft (baseline_id);
            """),

        Migration.Sql(13, "summary links and link suggestions", """
            -- Content links between two summaries of one workspace (requirements §10.3.3–10.3.4, architecture "SummaryLink",
            -- "LinkSuggestion"), both tables in one step so the AI-suggested links (8b) need no migration of their own. A link
            -- has no direction: the pair is kept in one order (node_a < node_b), so a pair is unique whichever way round it was
            -- made, and a summary is never linked to itself. Both nodes are referenced together with the link's workspace, so
            -- SQLite itself refuses a link across workspaces; deleting either summary (its last version, its conversation, its
            -- workspace) deletes its links and suggestions in the same transaction, and deleting the workspace deletes the rest.
            -- The note is at most 16 characters (length() counts characters); the application stores none as NULL.
            CREATE UNIQUE INDEX summary_node_by_workspace_id ON summary_node (workspace_id, id);

            CREATE TABLE summary_link (
                id           TEXT    NOT NULL PRIMARY KEY,
                workspace_id TEXT    NOT NULL REFERENCES workspace (id) ON DELETE CASCADE,
                node_a       TEXT    NOT NULL,
                node_b       TEXT    NOT NULL,
                note         TEXT    CHECK (note IS NULL OR length(note) <= 16),
                origin       TEXT    NOT NULL CHECK (origin IN ('user', 'confirmed')),
                created_at   INTEGER NOT NULL,
                CHECK (node_a < node_b),
                UNIQUE (workspace_id, node_a, node_b),
                FOREIGN KEY (workspace_id, node_a) REFERENCES summary_node (workspace_id, id) ON DELETE CASCADE,
                FOREIGN KEY (workspace_id, node_b) REFERENCES summary_node (workspace_id, id) ON DELETE CASCADE
            ) STRICT;
            CREATE INDEX summary_link_by_b ON summary_link (workspace_id, node_b);

            -- Suggested pairs (8b): pending ones are shown until accepted (they become a summary_link with origin 'confirmed')
            -- or ignored; ignored ones are kept, with no reason, so the pair is not suggested again. A pair whose link the user
            -- deleted is recorded here as ignored (§10.3.4), without a request. generation_id names the request that made a
            -- pending row. One row per pair, in the same order as links.
            CREATE TABLE link_suggestion (
                id            TEXT    NOT NULL PRIMARY KEY,
                workspace_id  TEXT    NOT NULL REFERENCES workspace (id) ON DELETE CASCADE,
                node_a        TEXT    NOT NULL,
                node_b        TEXT    NOT NULL,
                note          TEXT    CHECK (note IS NULL OR length(note) <= 16),
                reason        TEXT,
                state         TEXT    NOT NULL CHECK (state IN ('pending', 'ignored')),
                generation_id TEXT,
                created_at    INTEGER NOT NULL,
                updated_at    INTEGER NOT NULL,
                CHECK (node_a < node_b),
                CHECK (state = 'ignored' OR generation_id IS NOT NULL),
                CHECK (state = 'pending' OR reason IS NULL),
                UNIQUE (workspace_id, node_a, node_b),
                FOREIGN KEY (workspace_id, node_a) REFERENCES summary_node (workspace_id, id) ON DELETE CASCADE,
                FOREIGN KEY (workspace_id, node_b) REFERENCES summary_node (workspace_id, id) ON DELETE CASCADE
            ) STRICT;
            CREATE INDEX link_suggestion_by_b ON link_suggestion (workspace_id, node_b);
            """),
    ];
}
