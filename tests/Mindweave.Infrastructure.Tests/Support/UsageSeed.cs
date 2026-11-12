using Microsoft.Data.Sqlite;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Testing.Usage;

/// <summary>
/// Rows for the statistics page written straight into the schema (2026-11-12), so tests and screenshots can place
/// questions, requests and summaries on chosen days and models without running the services that would stamp them with
/// the clock. Every row passes the schema's own checks and keys. Also compiled into the desktop tests (linked file).
/// </summary>
internal sealed class UsageSeed(SqliteDatabase database)
{
    public const string DefaultBase = "https://api.example.com/v1";

    private int _seq;

    public async Task<WorkspaceId> WorkspaceAsync(string name, DateTimeOffset at)
    {
        var id = WorkspaceId.New();
        await database.WriteAsync(
            (c, t) => c.Execute(t, "INSERT INTO workspace (id, name, created_at) VALUES ($id, $name, $at);", ("$id", id.ToString()), ("$name", name), ("$at", at.Ms())),
            CancellationToken.None);
        return id;
    }

    /// <summary>A conversation; with <paramref name="parent"/> a branch from the parent's last message (which must exist).</summary>
    public async Task<ConversationId> ConversationAsync(WorkspaceId workspace, DateTimeOffset at, ConversationId? parent = null)
    {
        var id = ConversationId.New();
        await database.WriteAsync(
            (c, t) =>
            {
                c.Execute(t, """
                    INSERT INTO conversation (id, workspace_id, title, title_source, created_at, updated_at)
                    VALUES ($id, $ws, '会话', 'manual', $at, $at);
                    """, ("$id", id.ToString()), ("$ws", workspace.ToString()), ("$at", at.Ms()));
                if (parent is { } p)
                {
                    var source = c.Scalar<string>(t, "SELECT id FROM message WHERE conversation_id = $p ORDER BY seq DESC LIMIT 1;", ("$p", p.ToString()))
                        ?? throw new InvalidOperationException("A branch needs a message in its parent.");
                    c.Execute(t, """
                        INSERT INTO branch_source (child_id, parent_id, source_message_id, source_block, quote, anchor, anchor_version, render_profile, created_at)
                        VALUES ($child, $parent, $source, 0, '引用', '{}', 1, 'seed', $at);
                        """, ("$child", id.ToString()), ("$parent", p.ToString()), ("$source", source), ("$at", at.Ms()));
                    c.Execute(t, "INSERT INTO branch_context_range (child_id, ancestor_id, depth, max_seq) VALUES ($child, $parent, 0, 0);",
                        ("$child", id.ToString()), ("$parent", p.ToString()));
                }

                return 0;
            },
            CancellationToken.None);
        return id;
    }

    /// <summary>A question (a user message) asked at <paramref name="at"/>.</summary>
    public Task QuestionAsync(ConversationId conversation, DateTimeOffset at) =>
        database.WriteAsync((c, t) => InsertMessage(c, t, conversation, "user", at), CancellationToken.None);

    /// <summary>
    /// One request of a task of <paramref name="kind"/> (answer, title, summary, naming) in the conversation; null token
    /// counts mean the service reported none.
    /// </summary>
    public Task RequestAsync(
        ConversationId conversation, string kind, string model, DateTimeOffset at, int? prompt, int? completion,
        string baseUrl = DefaultBase, string outcome = "completed", int? estimated = null) =>
        database.WriteAsync(
            (c, t) =>
            {
                var target = kind == "answer" ? InsertMessage(c, t, conversation, "assistant", at) : null;
                var task = Guid.NewGuid().ToString("D");
                c.Execute(t, """
                    INSERT INTO generation_task (id, kind, conversation_id, target_message_id, state, model_config_id, base_url, protocol, model,
                                                 credential_ref, input_message_ids, created_at, started_at, finished_at, summary_to_seq)
                    VALUES ($id, $kind, $conv, $target, 'completed', 'seed', $base, 'chat_completions', $model, 'seed/key', '[]', $at, $at, $at,
                            CASE WHEN $kind = 'summary' THEN 0 END);
                    """, ("$id", task), ("$kind", kind), ("$conv", conversation.ToString()), ("$target", target), ("$base", baseUrl), ("$model", model), ("$at", at.Ms()));
                InsertRequest(c, t, task, null, model, baseUrl, at, prompt, completion, outcome, estimated);
                return 0;
            },
            CancellationToken.None);

    /// <summary>One request made without a task for the workspace (its suggested names).</summary>
    public Task WorkspaceRequestAsync(WorkspaceId workspace, string model, DateTimeOffset at, int? prompt, int? completion, string baseUrl = DefaultBase) =>
        database.WriteAsync(
            (c, t) =>
            {
                InsertRequest(c, t, null, workspace, model, baseUrl, at, prompt, completion, "completed", null);
                return 0;
            },
            CancellationToken.None);

    /// <summary>A summary node of the conversation with <paramref name="versions"/> versions; the current one read when asked.</summary>
    public Task SummaryAsync(ConversationId conversation, DateTimeOffset at, int versions = 1, bool read = false) =>
        database.WriteAsync(
            (c, t) =>
            {
                var workspace = c.Scalar<string>(t, "SELECT workspace_id FROM conversation WHERE id = $id;", ("$id", conversation.ToString()));
                var node = Guid.NewGuid().ToString("D");
                var ids = Enumerable.Range(0, versions).Select(_ => Guid.NewGuid().ToString("D")).ToList();
                c.Execute(t, """
                    INSERT INTO summary_node (id, workspace_id, conversation_id, current_version_id, base_title, display_name, name_source, created_at, updated_at)
                    VALUES ($id, $ws, $conv, $current, '总结', '总结', 'base', $at, $at);
                    """, ("$id", node), ("$ws", workspace), ("$conv", conversation.ToString()), ("$current", ids[^1]), ("$at", at.Ms()));
                for (var i = 0; i < ids.Count; i++)
                {
                    c.Execute(t, """
                        INSERT INTO summary_version (id, node_id, seq, title, body, to_seq, origin, created_at)
                        VALUES ($id, $node, $seq, '总结', '正文', 0, 'generated', $at);
                        """, ("$id", ids[i]), ("$node", node), ("$seq", i + 1), ("$at", at.Ms()));
                }

                if (read)
                {
                    c.Execute(t, "INSERT INTO summary_read_state (version_id, read_source, max_progress, updated_at) VALUES ($id, 'manual', 1, $at);",
                        ("$id", ids[^1]), ("$at", at.Ms()));
                }

                return 0;
            },
            CancellationToken.None);

    public Task ArchiveAsync(ConversationId conversation, DateTimeOffset at) =>
        database.WriteAsync(
            (c, t) => c.Execute(t, "INSERT INTO conversation_archive (conversation_id, archived_at) VALUES ($id, $at);", ("$id", conversation.ToString()), ("$at", at.Ms())),
            CancellationToken.None);

    private string InsertMessage(SqliteConnection c, SqliteTransaction t, ConversationId conversation, string role, DateTimeOffset at)
    {
        var id = Guid.NewGuid().ToString("D");
        c.Execute(t, """
            INSERT INTO message (id, conversation_id, seq, role, state, created_at, completed_at)
            VALUES ($id, $conv, $seq, $role, 'complete', $at, $at);
            """, ("$id", id), ("$conv", conversation.ToString()), ("$seq", Interlocked.Increment(ref _seq)), ("$role", role), ("$at", at.Ms()));
        c.Execute(t, "INSERT INTO message_block (message_id, ordinal, kind, text) VALUES ($id, 0, 'text', '问题');", ("$id", id));
        return id;
    }

    private static void InsertRequest(
        SqliteConnection c, SqliteTransaction t, string? task, WorkspaceId? workspace, string model, string baseUrl, DateTimeOffset at,
        int? prompt, int? completion, string outcome, int? estimated) =>
        c.Execute(t, """
            INSERT INTO ai_request (id, task_id, workspace_id, kind, model, base_url, started_at, finished_at, outcome, output_chars,
                                    prompt_tokens, completion_tokens, estimated_tokens)
            VALUES ($id, $task, $ws, $kind, $model, $base, $at, $at, $outcome, 0, $prompt, $completion, $estimated);
            """,
            ("$id", Guid.NewGuid().ToString("D")),
            ("$task", task),
            ("$ws", workspace?.ToString()),
            ("$kind", workspace is null ? null : "workspace-name"),
            ("$model", workspace is null ? null : model),
            ("$base", workspace is null ? null : baseUrl),
            ("$at", at.Ms()),
            ("$outcome", outcome),
            ("$prompt", prompt),
            ("$completion", completion),
            ("$estimated", estimated));
}
