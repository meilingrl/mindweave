using Microsoft.Data.Sqlite;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>
/// Summary nodes and versions (architecture §8.2). Each operation re-checks its preconditions inside its own
/// transaction; the node ↔ version references are deferred foreign keys checked at commit.
/// </summary>
internal sealed class SqliteSummaryStore(SqliteDatabase database) : ISummaryStore
{
    private const string NodeColumns = "id, workspace_id, conversation_id, current_version_id, pending_version_id, base_title, display_name, name_source, created_at, updated_at";
    private const string VersionColumns = "id, node_id, seq, title, body, to_seq, origin, baseline_id, task_id, model, created_at";

    /// <summary>Message states that are final; coverage and "later content" count only these.</summary>
    private const string Finished = "state NOT IN ('pending', 'streaming')";

    public Task<IReadOnlyList<SummaryEntry>> ListAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<SummaryEntry>>(
            connection =>
            {
                var nodes = Nodes(connection, null, "workspace_id = $ws ORDER BY created_at", ("$ws", workspace.ToString()));
                return [.. nodes.Select(n => Entry(connection, n))];
            },
            cancellationToken);

    public Task<SummaryEntry?> ForConversationAsync(ConversationId conversation, CancellationToken cancellationToken) =>
        database.ReadAsync(
            connection => Nodes(connection, null, "conversation_id = $conv", ("$conv", conversation.ToString())) is [var node] ? Entry(connection, node) : null,
            cancellationToken);

    public Task<SummaryDetail?> GetAsync(SummaryNodeId node, CancellationToken cancellationToken) =>
        database.ReadAsync(connection => Detail(connection, null, node), cancellationToken);

    public Task<SummarySaved?> SaveGeneratedAsync(
        GenerationTaskId task, ConversationId conversation, string title, string body, int toSeq, SummaryVersionId? baseline, string model, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync<SummarySaved?>(
            (connection, transaction) =>
            {
                // The version exists exactly when the task completed (MW-C06): a stopped or interrupted task saves nothing.
                if (connection.Execute(transaction,
                        "UPDATE generation_task SET state = 'completed', finished_at = $at WHERE id = $id AND state = 'running';",
                        ("$id", task.ToString()), ("$at", at.Ms())) != 1)
                {
                    return null;
                }

                var version = SummaryVersionId.New();
                var existing = Nodes(connection, transaction, "conversation_id = $conv", ("$conv", conversation.ToString()));
                if (existing is [var node])
                {
                    if (node.Pending is not null)
                    {
                        throw new SummaryRefusedException(SummaryRefusal.HasCandidate);
                    }

                    InsertVersion(connection, transaction, new(version, node.Id, NextSeq(connection, transaction, node.Id), title, body, toSeq, SummaryOrigin.Generated, baseline, task, model, at));
                    connection.Execute(transaction, "UPDATE summary_node SET pending_version_id = $v, updated_at = $at WHERE id = $id;",
                        ("$id", node.Id.ToString()), ("$v", version.ToString()), ("$at", at.Ms()));
                    return new SummarySaved(node.Id, version, IsCandidate: true);
                }

                var workspace = connection.Scalar<string>(transaction, "SELECT workspace_id FROM conversation WHERE id = $id;", ("$id", conversation.ToString()))
                    ?? throw new SummaryRefusedException(SummaryRefusal.Changed);
                var id = SummaryNodeId.New();
                connection.Execute(transaction, $"""
                    INSERT INTO summary_node ({NodeColumns})
                    VALUES ($id, $ws, $conv, $v, NULL, $title, $title, 'base', $at, $at);
                    """, ("$id", id.ToString()), ("$ws", workspace), ("$conv", conversation.ToString()), ("$v", version.ToString()), ("$title", title), ("$at", at.Ms()));
                InsertVersion(connection, transaction, new(version, id, 1, title, body, toSeq, SummaryOrigin.Generated, null, task, model, at));
                return new SummarySaved(id, version, IsCandidate: false);
            },
            cancellationToken);

    public Task AdoptAsync(SummaryNodeId node, SummaryVersionId candidate, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                var current = RequireNode(connection, transaction, node);
                if (current.Pending != candidate)
                {
                    throw new SummaryRefusedException(SummaryRefusal.Changed);
                }

                ThrowIfDraft(connection, transaction, node);
                return connection.Execute(transaction,
                    "UPDATE summary_node SET current_version_id = $v, pending_version_id = NULL, updated_at = $at WHERE id = $id;",
                    ("$id", node.ToString()), ("$v", candidate.ToString()), ("$at", at.Ms()));
            },
            cancellationToken);

    public Task DiscardAsync(SummaryNodeId node, SummaryVersionId candidate, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                if (RequireNode(connection, transaction, node).Pending != candidate)
                {
                    throw new SummaryRefusedException(SummaryRefusal.Changed);
                }

                connection.Execute(transaction, "UPDATE summary_node SET pending_version_id = NULL, updated_at = $at WHERE id = $id;", ("$id", node.ToString()), ("$at", at.Ms()));
                return DeleteVersion(connection, transaction, candidate);
            },
            cancellationToken);

    public Task SetCurrentAsync(SummaryNodeId node, SummaryVersionId version, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                var current = RequireNode(connection, transaction, node);
                if (current.Pending == version || !VersionBelongs(connection, transaction, node, version))
                {
                    throw new SummaryRefusedException(current.Pending == version ? SummaryRefusal.IsCandidate : SummaryRefusal.Changed);
                }

                ThrowIfDraft(connection, transaction, node);
                return connection.Execute(transaction, "UPDATE summary_node SET current_version_id = $v, updated_at = $at WHERE id = $id;",
                    ("$id", node.ToString()), ("$v", version.ToString()), ("$at", at.Ms()));
            },
            cancellationToken);

    public Task<VersionsDeleted> DeleteVersionsAsync(SummaryNodeId node, IReadOnlySet<SummaryVersionId> versions, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(versions);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                var detail = Detail(connection, transaction, node) ?? throw new SummaryRefusedException(SummaryRefusal.Changed);
                if (detail.Node.Pending is { } pending && versions.Contains(pending))
                {
                    throw new SummaryRefusedException(SummaryRefusal.IsCandidate);
                }

                if (!versions.All(v => detail.Versions.Any(x => x.Id == v)))
                {
                    throw new SummaryRefusedException(SummaryRefusal.Changed);
                }

                if (detail.Draft is not null)
                {
                    throw new SummaryRefusedException(SummaryRefusal.HasDraft);
                }

                if (detail.HasUnfinishedTask)
                {
                    throw new SummaryRefusedException(SummaryRefusal.TaskRunning);
                }

                var next = SummaryRules.NextCurrent(detail.Saved, detail.Node.Current, versions);
                if (next is null)
                {
                    if (detail.Node.Pending is not null)
                    {
                        throw new SummaryRefusedException(SummaryRefusal.HasCandidate);
                    }

                    // The last saved version goes, and the node with it; the source conversation stays.
                    connection.Execute(transaction, "DELETE FROM summary_node WHERE id = $id;", ("$id", node.ToString()));
                }
                else
                {
                    connection.Execute(transaction, "UPDATE summary_node SET current_version_id = $v, updated_at = $at WHERE id = $id;",
                        ("$id", node.ToString()), ("$v", next.Value.ToString()), ("$at", at.Ms()));
                }

                foreach (var version in versions)
                {
                    DeleteVersion(connection, transaction, version);
                }

                return new VersionsDeleted(next);
            },
            cancellationToken);
    }

    public Task SaveDraftAsync(SummaryEditDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                // A draft only ever edits the current version.
                if (RequireNode(connection, transaction, draft.Node).Current != draft.Baseline)
                {
                    throw new SummaryRefusedException(SummaryRefusal.Changed);
                }

                return connection.Execute(transaction, """
                    INSERT INTO summary_edit_draft (node_id, baseline_id, title, body, updated_at) VALUES ($node, $base, $title, $body, $at)
                    ON CONFLICT (node_id) DO UPDATE SET baseline_id = excluded.baseline_id, title = excluded.title, body = excluded.body, updated_at = excluded.updated_at;
                    """, ("$node", draft.Node.ToString()), ("$base", draft.Baseline.ToString()), ("$title", draft.Title), ("$body", draft.Body), ("$at", draft.UpdatedAt.Ms()));
            },
            cancellationToken);
    }

    public Task DeleteDraftAsync(SummaryNodeId node, CancellationToken cancellationToken) =>
        database.WriteAsync((connection, transaction) => connection.Execute(transaction, "DELETE FROM summary_edit_draft WHERE node_id = $id;", ("$id", node.ToString())), cancellationToken);

    public Task<SummaryVersionId?> SaveEditAsync(SummaryNodeId node, SummaryVersionId baseline, string title, string body, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync<SummaryVersionId?>(
            (connection, transaction) =>
            {
                var detail = Detail(connection, transaction, node) ?? throw new SummaryRefusedException(SummaryRefusal.Changed);
                if (detail.Node.Current != baseline)
                {
                    throw new SummaryRefusedException(SummaryRefusal.Changed);
                }

                connection.Execute(transaction, "DELETE FROM summary_edit_draft WHERE node_id = $id;", ("$id", node.ToString()));
                var source = detail.Current;
                if (source.Title == title && source.Body == body)
                {
                    return null;
                }

                // An edit keeps its baseline's coverage; it never extends to later messages.
                var version = SummaryVersionId.New();
                InsertVersion(connection, transaction, new(version, node, NextSeq(connection, transaction, node), title, body, source.ToSeq, SummaryOrigin.Edited, baseline, null, null, at));
                connection.Execute(transaction, "UPDATE summary_node SET current_version_id = $v, updated_at = $at WHERE id = $id;",
                    ("$id", node.ToString()), ("$v", version.ToString()), ("$at", at.Ms()));
                UpsertRead(connection, transaction, version, ReadSource.Edit, null, null, at);
                return version;
            },
            cancellationToken);

    public Task SetReadAsync(SummaryVersionId version, ReadSource? source, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                if (connection.Scalar<long>(transaction, "SELECT count(*) FROM summary_version WHERE id = $id;", ("$id", version.ToString())) == 0)
                {
                    throw new SummaryRefusedException(SummaryRefusal.Changed);
                }

                connection.Execute(transaction, """
                    INSERT INTO summary_read_state (version_id, read_source, max_progress, updated_at) VALUES ($id, $source, 0, $at)
                    ON CONFLICT (version_id) DO UPDATE SET read_source = excluded.read_source, updated_at = excluded.updated_at;
                    """, ("$id", version.ToString()), ("$source", source is { } s ? SqlNames.Of(s) : null), ("$at", at.Ms()));
                return 0;
            },
            cancellationToken);

    public Task RecordProgressAsync(SummaryVersionId version, double progress, string? locator, bool markRead, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                // A version deleted while it was open is simply not recorded.
                if (connection.Scalar<long>(transaction, "SELECT count(*) FROM summary_version WHERE id = $id;", ("$id", version.ToString())) == 0)
                {
                    return 0;
                }

                UpsertRead(connection, transaction, version, markRead ? ReadSource.Progress : null, Math.Clamp(progress, 0, 1), locator, at);
                return 1;
            },
            cancellationToken);

    public Task RenameAsync(SummaryNodeId node, string displayName, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                RequireNode(connection, transaction, node);
                return connection.Execute(transaction, "UPDATE summary_node SET display_name = $name, name_source = 'manual', updated_at = $at WHERE id = $id;",
                    ("$id", node.ToString()), ("$name", displayName), ("$at", at.Ms()));
            },
            cancellationToken);

    public Task<IReadOnlyList<SummaryNodeId>> ApplyNamesAsync(IReadOnlyList<(NamingExpectation Expected, string DisplayName)> names, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        return database.WriteAsync<IReadOnlyList<SummaryNodeId>>(
            (connection, transaction) =>
            {
                var renamed = new List<SummaryNodeId>();
                foreach (var (expected, displayName) in names)
                {
                    // Late results check the node still is what the naming saw; manual names are never touched.
                    var changed = connection.Execute(transaction, """
                        UPDATE summary_node SET display_name = $name, name_source = 'auto', updated_at = $at
                        WHERE id = $id AND current_version_id = $v AND display_name = $old AND name_source <> 'manual' AND display_name <> $name;
                        """, ("$id", expected.Node.ToString()), ("$v", expected.Current.ToString()), ("$old", expected.DisplayName), ("$name", displayName), ("$at", at.Ms()));
                    if (changed == 1)
                    {
                        renamed.Add(expected.Node);
                    }
                }

                return renamed;
            },
            cancellationToken);
    }

    public Task UpdateBaseTitleAsync(SummaryNodeId node, SummaryVersionId current, string baseTitle, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) => connection.Execute(transaction, """
                UPDATE summary_node
                SET base_title = $title,
                    display_name = CASE WHEN name_source = 'manual' THEN display_name ELSE $title END,
                    name_source = CASE WHEN name_source = 'manual' THEN 'manual' ELSE 'base' END,
                    updated_at = $at
                WHERE id = $id AND current_version_id = $v;
                """, ("$id", node.ToString()), ("$v", current.ToString()), ("$title", baseTitle), ("$at", at.Ms())),
            cancellationToken);

    public Task<SummaryTaskStatus?> LatestTaskAsync(ConversationId conversation, CancellationToken cancellationToken) =>
        database.ReadAsync(
            connection =>
            {
                using var command = connection.Command(null, """
                    SELECT id, state, error_kind, error_detail, created_at FROM generation_task
                    WHERE conversation_id = $conv AND kind = 'summary' ORDER BY created_at DESC, rowid DESC LIMIT 1;
                    """, ("$conv", conversation.ToString()));
                using var reader = command.ExecuteReader();
                return reader.Read()
                    ? new SummaryTaskStatus(
                        GenerationTaskId.Parse(reader.GetString(0)),
                        SqlNames.ParseTaskState(reader.GetString(1)),
                        reader.NullableString(2) is { } kind ? SqlNames.ParseErrorKind(kind) : null,
                        reader.NullableString(3),
                        reader.Time(4))
                    : null;
            },
            cancellationToken);

    private static SummaryEntry Entry(SqliteConnection connection, SummaryNode node)
    {
        var current = Versions(connection, null, node.Id).Single(v => v.Id == node.Current);
        var read = ReadStates(connection, null, [node.Current]).GetValueOrDefault(node.Current);
        return new(node, current, SummaryRules.Markers(node, current, read, LastFinishedSeq(connection, null, node.Conversation)));
    }

    private static SummaryDetail? Detail(SqliteConnection connection, SqliteTransaction? transaction, SummaryNodeId id)
    {
        if (Nodes(connection, transaction, "id = $id", ("$id", id.ToString())) is not [var node])
        {
            return null;
        }

        var versions = Versions(connection, transaction, id);
        SummaryEditDraft? draft = null;
        using (var command = connection.Command(transaction, "SELECT baseline_id, title, body, updated_at FROM summary_edit_draft WHERE node_id = $id;", ("$id", id.ToString())))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                draft = new(id, SummaryVersionId.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.Time(3));
            }
        }

        var running = connection.Scalar<long>(transaction,
            "SELECT count(*) FROM generation_task WHERE conversation_id = $conv AND kind = 'summary' AND state IN ('queued', 'running');",
            ("$conv", node.Conversation.ToString())) > 0;
        return new(node, versions, ReadStates(connection, transaction, [.. versions.Select(v => v.Id)]), draft, LastFinishedSeq(connection, transaction, node.Conversation), running);
    }

    private static int? LastFinishedSeq(SqliteConnection connection, SqliteTransaction? transaction, ConversationId conversation) =>
        connection.Scalar<int?>(transaction, $"SELECT max(seq) FROM message WHERE conversation_id = $conv AND {Finished};", ("$conv", conversation.ToString()));

    private static List<SummaryNode> Nodes(SqliteConnection connection, SqliteTransaction? transaction, string where, params (string, object?)[] parameters)
    {
        using var command = connection.Command(transaction, $"SELECT {NodeColumns} FROM summary_node WHERE {where};", parameters);
        using var reader = command.ExecuteReader();
        var list = new List<SummaryNode>();
        while (reader.Read())
        {
            list.Add(new(
                SummaryNodeId.Parse(reader.GetString(0)),
                WorkspaceId.Parse(reader.GetString(1)),
                ConversationId.Parse(reader.GetString(2)),
                SummaryVersionId.Parse(reader.GetString(3)),
                reader.NullableString(4) is { } pending ? SummaryVersionId.Parse(pending) : null,
                reader.GetString(5),
                reader.GetString(6),
                SqlNames.ParseNameSource(reader.GetString(7)),
                reader.Time(8),
                reader.Time(9)));
        }

        return list;
    }

    private static List<SummaryVersion> Versions(SqliteConnection connection, SqliteTransaction? transaction, SummaryNodeId node)
    {
        using var command = connection.Command(transaction, $"SELECT {VersionColumns} FROM summary_version WHERE node_id = $id ORDER BY seq;", ("$id", node.ToString()));
        using var reader = command.ExecuteReader();
        var list = new List<SummaryVersion>();
        while (reader.Read())
        {
            list.Add(new(
                SummaryVersionId.Parse(reader.GetString(0)),
                SummaryNodeId.Parse(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                SqlNames.ParseOrigin(reader.GetString(6)),
                reader.NullableString(7) is { } baseline ? SummaryVersionId.Parse(baseline) : null,
                reader.NullableString(8) is { } task ? GenerationTaskId.Parse(task) : null,
                reader.NullableString(9),
                reader.Time(10)));
        }

        return list;
    }

    private static Dictionary<SummaryVersionId, SummaryReadState> ReadStates(SqliteConnection connection, SqliteTransaction? transaction, IReadOnlyList<SummaryVersionId> versions)
    {
        var states = new Dictionary<SummaryVersionId, SummaryReadState>();
        foreach (var version in versions)
        {
            using var command = connection.Command(transaction, "SELECT read_source, max_progress, locator FROM summary_read_state WHERE version_id = $id;", ("$id", version.ToString()));
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                states[version] = new(version, reader.NullableString(0) is { } source ? SqlNames.ParseReadSource(source) : null, reader.GetDouble(1), reader.NullableString(2));
            }
        }

        return states;
    }

    private static SummaryNode RequireNode(SqliteConnection connection, SqliteTransaction transaction, SummaryNodeId node) =>
        Nodes(connection, transaction, "id = $id", ("$id", node.ToString())) is [var found] ? found : throw new SummaryRefusedException(SummaryRefusal.Changed);

    private static void ThrowIfDraft(SqliteConnection connection, SqliteTransaction transaction, SummaryNodeId node)
    {
        if (connection.Scalar<long>(transaction, "SELECT count(*) FROM summary_edit_draft WHERE node_id = $id;", ("$id", node.ToString())) > 0)
        {
            throw new SummaryRefusedException(SummaryRefusal.HasDraft);
        }
    }

    private static bool VersionBelongs(SqliteConnection connection, SqliteTransaction transaction, SummaryNodeId node, SummaryVersionId version) =>
        connection.Scalar<long>(transaction, "SELECT count(*) FROM summary_version WHERE id = $v AND node_id = $n;", ("$v", version.ToString()), ("$n", node.ToString())) == 1;

    private static int NextSeq(SqliteConnection connection, SqliteTransaction transaction, SummaryNodeId node) =>
        (int)(connection.Scalar<long?>(transaction, "SELECT max(seq) FROM summary_version WHERE node_id = $id;", ("$id", node.ToString())) ?? 0) + 1;

    private static void InsertVersion(SqliteConnection connection, SqliteTransaction transaction, SummaryVersion v) =>
        connection.Execute(transaction, $"""
            INSERT INTO summary_version ({VersionColumns}) VALUES ($id, $node, $seq, $title, $body, $to, $origin, $baseline, $task, $model, $at);
            """,
            ("$id", v.Id.ToString()),
            ("$node", v.Node.ToString()),
            ("$seq", v.Seq),
            ("$title", v.Title),
            ("$body", v.Body),
            ("$to", v.ToSeq),
            ("$origin", SqlNames.Of(v.Origin)),
            ("$baseline", v.Baseline?.ToString()),
            ("$task", v.Task?.ToString()),
            ("$model", v.Model),
            ("$at", v.CreatedAt.Ms()));

    /// <summary>Removes a version's text and its read state; nothing keeps a hidden copy (§8.2).</summary>
    private static int DeleteVersion(SqliteConnection connection, SqliteTransaction transaction, SummaryVersionId version) =>
        connection.Execute(transaction, "DELETE FROM summary_version WHERE id = $id;", ("$id", version.ToString()));

    private static void UpsertRead(SqliteConnection connection, SqliteTransaction transaction, SummaryVersionId version, ReadSource? markRead, double? progress, string? locator, DateTimeOffset at) =>
        connection.Execute(transaction, """
            INSERT INTO summary_read_state (version_id, read_source, max_progress, locator, updated_at) VALUES ($id, $source, coalesce($progress, 0), $locator, $at)
            ON CONFLICT (version_id) DO UPDATE SET
                read_source = coalesce(read_source, excluded.read_source),
                max_progress = max(max_progress, excluded.max_progress),
                locator = coalesce(excluded.locator, locator),
                updated_at = excluded.updated_at;
            """,
            ("$id", version.ToString()),
            ("$source", markRead is { } s ? SqlNames.Of(s) : null),
            ("$progress", progress),
            ("$locator", locator),
            ("$at", at.Ms()));
}
