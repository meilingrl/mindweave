using Microsoft.Data.Sqlite;
using Mindweave.Application.Exchange;
using Mindweave.Application.ManualBackup;
using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Resources;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>
/// Writes what a manual backup adds, in one transaction (docs/design/sharing-backup.md §2, §3). What is here stays as it is,
/// except a conversation joined by time: its messages are numbered anew, and what refers to them by number follows.
/// </summary>
internal sealed class SqliteManualBackupImportStore(SqliteDatabase database) : IManualBackupImportStore
{
    public Task<ManualBackupStored> StoredAsync(ManualBackupFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        return database.ReadAsync(connection => Stored(connection, null, file), cancellationToken);
    }

    public Task<ManualBackupImported> ImportAsync(ManualBackupFile file, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                // Compared again here: what the dialog showed may have changed since.
                var stored = Stored(connection, transaction, file);
                var plan = ManualBackupImportRules.Plan(file, stored);
                if (plan.IsBlocked)
                {
                    return ManualBackupImported.Nothing(ManualBackupImportStatus.AnswerRunning);
                }

                if (plan.IsEmpty)
                {
                    return ManualBackupImported.Nothing(ManualBackupImportStatus.NothingNew);
                }

                var writer = new Writer(connection, transaction, stored, at);
                foreach (var workspace in plan.Workspaces.Where(w => w.Writes))
                {
                    writer.Write(workspace);
                }

                return new ManualBackupImported(ManualBackupImportStatus.Imported, [.. plan.Workspaces.Where(w => w.Writes)])
                {
                    Created = [.. plan.Workspaces.Where(w => w.Create).Select(w => new Workspace(w.Workspace.Id, WorkspaceName.FromStored(w.Workspace.Name), w.Workspace.CreatedAt))],
                };
            },
            cancellationToken);
    }

    /// <summary>What the store has of the package, by identity, in one snapshot.</summary>
    private static ManualBackupStored Stored(SqliteConnection connection, SqliteTransaction? transaction, ManualBackupFile file)
    {
        var workspaces = Column(connection, transaction, "SELECT id FROM workspace;").Select(WorkspaceId.Parse).ToHashSet();
        var messages = Pairs(connection, transaction, "SELECT id, conversation_id FROM message;").ToDictionary(p => MessageId.Parse(p.Item1), p => ConversationId.Parse(p.Item2));
        var versions = Pairs(connection, transaction, "SELECT v.id, n.conversation_id FROM summary_version v JOIN summary_node n ON n.id = v.node_id;")
            .ToDictionary(p => SummaryVersionId.Parse(p.Item1), p => ConversationId.Parse(p.Item2));
        var resources = Pairs(connection, transaction, "SELECT id, coalesce(conversation_id, '') FROM resource;")
            .ToDictionary(p => ResourceId.Parse(p.Item1), p => p.Item2.Length == 0 ? (ConversationId?)null : ConversationId.Parse(p.Item2));
        var nodes = Column(connection, transaction, "SELECT id FROM summary_node;").Select(SummaryNodeId.Parse).ToHashSet();
        var linked = Pairs(connection, transaction, "SELECT node_a, node_b FROM summary_link;").Select(p => (SummaryNodeId.Parse(p.Item1), SummaryNodeId.Parse(p.Item2))).ToHashSet();
        var conversations = new Dictionary<ConversationId, ManualBackupStoredConversation>();
        foreach (var conversation in file.Workspaces.SelectMany(d => d.Conversations))
        {
            if (Conversation(connection, transaction, conversation.Id) is { } held)
            {
                conversations[conversation.Id] = held;
            }
        }

        return new ManualBackupStored(workspaces, conversations, messages, versions, resources, nodes, linked);
    }

    private static ManualBackupStoredConversation? Conversation(SqliteConnection connection, SqliteTransaction? transaction, ConversationId id)
    {
        WorkspaceId workspace;
        bool received, source;
        SummaryNodeId? node;
        using (var command = connection.Command(transaction, """
            SELECT c.workspace_id, s.source_message_id IS NOT NULL,
                   EXISTS (SELECT 1 FROM import_conversation i WHERE i.conversation_id = c.id),
                   (SELECT n.id FROM summary_node n WHERE n.conversation_id = c.id)
            FROM conversation c LEFT JOIN branch_source s ON s.child_id = c.id WHERE c.id = $id;
            """, ("$id", id.ToString())))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                return null;
            }

            workspace = WorkspaceId.Parse(reader.GetString(0));
            (source, received) = (reader.GetInt64(1) != 0, reader.GetInt64(2) != 0);
            node = reader.NullableString(3) is { } n ? SummaryNodeId.Parse(n) : null;
        }

        var line = new List<LineEntry>();
        var unfinished = false;
        using var rows = connection.Command(transaction, "SELECT id, role, seq, created_at, state FROM message WHERE conversation_id = $id ORDER BY seq;", ("$id", id.ToString()));
        using var row = rows.ExecuteReader();
        while (row.Read())
        {
            line.Add(new LineEntry(MessageId.Parse(row.GetString(0)), SqlNames.ParseRole(row.GetString(1)), row.GetInt32(2), row.Time(3)));
            unfinished |= row.GetString(4) is "pending" or "streaming";
        }

        return new ManualBackupStoredConversation(workspace, received, line, unfinished, source, node);
    }

    private static List<string> Column(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.Command(transaction, sql);
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static List<(string, string)> Pairs(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Command(transaction, sql, parameters);
        using var reader = command.ExecuteReader();
        var values = new List<(string, string)>();
        while (reader.Read())
        {
            values.Add((reader.GetString(0), reader.GetString(1)));
        }

        return values;
    }

    /// <summary>One import's writing: what it has written so far, so that nothing is written twice.</summary>
    private sealed class Writer(SqliteConnection connection, SqliteTransaction transaction, ManualBackupStored stored, DateTimeOffset at)
    {
        private readonly HashSet<ResourceId> _resources = [.. stored.Resources.Keys];
        private readonly Dictionary<(string Title, DateTimeOffset At), string> _imports = [];

        public void Write(ManualBackupWorkspacePlan plan)
        {
            if (plan.Create)
            {
                SqliteWorkspaceStore.Insert(connection, transaction, new Workspace(plan.Workspace.Id, WorkspaceName.FromStored(plan.Workspace.Name), plan.Workspace.CreatedAt));
            }

            var material = plan.Document.Resources.ToDictionary(r => r.Id);
            var nodes = new Dictionary<ConversationId, SummaryNodeId>();
            foreach (var conversation in plan.Conversations)
            {
                switch (conversation.Fit)
                {
                    case ManualBackupFit.Unfit:
                        continue;
                    case ManualBackupFit.New:
                        Create(conversation);
                        break;
                }

                var held = stored.Conversations.GetValueOrDefault(conversation.Conversation.Id);
                if (conversation.Fit == ManualBackupFit.Joins)
                {
                    Renumber(conversation.Conversation.Id, held!.Messages, conversation.Line!);
                }

                Messages(conversation, material);
                // An answer is the retry of the one directly before it, so two sides' retries of one answer form one chain
                // (V2); what was here already follows that rule, and only what arrived can change.
                if (conversation.Arriving.Count > 0)
                {
                    SqliteConversationStore.RelinkAttempts(connection, transaction, conversation.Conversation.Id);
                }

                if (conversation.Fit == ManualBackupFit.New)
                {
                    Branch(conversation);
                }
                else if (conversation.GainsSource)
                {
                    GainSource(conversation);
                }

                if (Summary(conversation, held) is { } node)
                {
                    nodes[conversation.Conversation.Id] = node;
                }
            }

            foreach (var link in plan.Links)
            {
                Link(plan, link, nodes);
            }
        }

        /// <summary>A conversation that is not here, as the package has it: its title and whose it is, its archived state, and whether it was received.</summary>
        private void Create(ManualBackupConversationPlan plan)
        {
            var conversation = plan.Conversation;
            var title = string.IsNullOrWhiteSpace(conversation.Name) ? null : conversation.Name;
            var touched = conversation.Content!.Messages.Select(m => m.Stored!.CompletedAt ?? m.Stored.CreatedAt).Append(conversation.StartedAt).Max();
            SqliteConversationStore.InsertConversation(connection, transaction, new Conversation(
                conversation.Id, plan.Workspace, title,
                title is null ? TitleSource.None : conversation.TitleByUser ? TitleSource.Manual : TitleSource.Auto,
                conversation.StartedAt, touched));
            if (conversation.IsArchived)
            {
                // The package does not say when it was archived; it was, as of the import.
                connection.Execute(transaction, "INSERT INTO conversation_archive (conversation_id, archived_at) VALUES ($id, $at);", ("$id", conversation.Id.ToString()), ("$at", at.Ms()));
            }

            if (conversation.Received is { } received)
            {
                // The record of where it came from: the share's title and when it came, as the package says.
                if (!_imports.TryGetValue((received.Title, received.At), out var import))
                {
                    import = Guid.CreateVersion7().ToString("D");
                    connection.Execute(transaction,
                        "INSERT INTO import (id, title, kind, imported_at) VALUES ($id, $title, $kind, $at);",
                        ("$id", import), ("$title", received.Title), ("$kind", ExchangeDocument.DeviceKind), ("$at", received.At.Ms()));
                    _imports[(received.Title, received.At)] = import;
                }

                connection.Execute(transaction, "INSERT INTO import_conversation (conversation_id, import_id) VALUES ($id, $import);",
                    ("$id", conversation.Id.ToString()), ("$import", import));
            }
        }

        /// <summary>
        /// Numbers the line anew (V2): first out of the way, then each message to its place. Whatever names a place in it by
        /// number follows the message it meant: the context of the branches below it, and its summaries' coverage, which ends
        /// before the first message a version did not know. Its context summaries are made again when needed.
        /// </summary>
        private void Renumber(ConversationId conversation, IReadOnlyList<LineEntry> held, JoinedLine line)
        {
            var id = ("$id", (object?)conversation.ToString());
            var shift = 1 + Math.Max(line.Messages.Count, held.Count == 0 ? 0 : held.Max(m => m.Seq));
            connection.Execute(transaction, "UPDATE message SET seq = seq + $shift WHERE conversation_id = $id;", id, ("$shift", shift));
            foreach (var message in held)
            {
                connection.Execute(transaction, "UPDATE message SET seq = $seq WHERE id = $m;", ("$m", message.Id.ToString()), ("$seq", line.Seqs[message.Id]));
            }

            foreach (var (child, max) in Pairs(connection, transaction, "SELECT child_id, CAST(max_seq AS TEXT) FROM branch_context_range WHERE ancestor_id = $id AND max_seq >= 0;", id))
            {
                connection.Execute(transaction, "UPDATE branch_context_range SET max_seq = $max WHERE child_id = $child AND ancestor_id = $id;",
                    id, ("$child", child), ("$max", line.Follow(held, int.Parse(max, System.Globalization.CultureInfo.InvariantCulture))));
            }

            foreach (var (version, to) in Pairs(connection, transaction, "SELECT v.id, CAST(v.to_seq AS TEXT) FROM summary_version v JOIN summary_node n ON n.id = v.node_id WHERE n.conversation_id = $id;", id))
            {
                connection.Execute(transaction, "UPDATE summary_version SET to_seq = $to WHERE id = $v;",
                    ("$v", version), ("$to", line.Coverage(held, int.Parse(to, System.Globalization.CultureInfo.InvariantCulture))));
            }

            foreach (var (task, to) in Pairs(connection, transaction, "SELECT id, CAST(summary_to_seq AS TEXT) FROM generation_task WHERE conversation_id = $id AND summary_to_seq IS NOT NULL;", id))
            {
                connection.Execute(transaction, "UPDATE generation_task SET summary_to_seq = $to WHERE id = $t;",
                    ("$t", task), ("$to", line.Coverage(held, int.Parse(to, System.Globalization.CultureInfo.InvariantCulture))));
            }

            connection.Execute(transaction, "DELETE FROM context_summary WHERE conversation_id = $id;", id);
        }

        /// <summary>The messages it lacks, each at its place in the line, with the material they refer to and their runs.</summary>
        private void Messages(ManualBackupConversationPlan plan, Dictionary<ResourceId, ExchangeResource> material)
        {
            if (plan.Arriving.Count == 0)
            {
                return;
            }

            foreach (var resource in plan.Arriving.SelectMany(ManualBackupDocument.Referred).Distinct())
            {
                if (_resources.Add(resource) && material.TryGetValue(resource, out var described))
                {
                    Resource(described, plan);
                }
            }

            var conversation = plan.Conversation.Id;
            foreach (var message in plan.Arriving)
            {
                var stored = message.Stored!;
                var supersedes = stored.Supersedes is { } replaced
                    && connection.Scalar<long>(transaction, "SELECT EXISTS (SELECT 1 FROM message WHERE id = $id AND conversation_id = $c AND role = 'assistant');",
                        ("$id", replaced.ToString()), ("$c", conversation.ToString())) != 0 ? replaced : (MessageId?)null;
                SqliteConversationStore.InsertStored(connection, transaction, new Message(
                    message.Id, conversation, plan.Line!.Seqs[message.Id], message.Role, stored.Blocks, message.State, 0,
                    message.Role == MessageRole.Assistant ? supersedes : null, message.Role == MessageRole.Assistant ? stored.Error : null, null,
                    stored.CreatedAt, stored.CompletedAt));
                if (message.Role == MessageRole.Assistant && (stored.Model is not null || stored.Run is not null))
                {
                    Task(conversation, message, stored);
                }
            }
        }

        /// <summary>
        /// What an answer's task kept that travelled: the model's name as text, and the steps of its run (a run is a task
        /// with tools; which were offered did not travel). Nothing of a connection is in it; such a task is finished and never runs.
        /// </summary>
        private void Task(ConversationId conversation, ExchangeMessage answer, ExchangeStoredMessage stored)
        {
            var task = Guid.CreateVersion7().ToString("D");
            connection.Execute(transaction, """
                INSERT INTO generation_task (id, kind, conversation_id, target_message_id, state, model_config_id, base_url, protocol, model, credential_ref,
                                             input_message_ids, error_kind, created_at, started_at, finished_at, tools, run_limit_reached)
                VALUES ($id, 'answer', $conv, $target, $state, '', '', $protocol, $model, '', '[]', $kind, $created, $created, $finished, $tools, $limit);
                """,
                ("$id", task),
                ("$conv", conversation.ToString()),
                ("$target", answer.Id.ToString()),
                ("$state", SqlNames.Of(SqlNames.TaskStateFor(answer.State))),
                ("$protocol", SqlNames.Of(ChatProtocol.ChatCompletions)),
                ("$model", stored.Model ?? string.Empty),
                ("$kind", stored.Error is { } error ? SqlNames.Of(error) : null),
                ("$created", stored.CreatedAt.Ms()),
                ("$finished", stored.CompletedAt?.Ms()),
                ("$tools", stored.Run is null ? null : "[]"),
                ("$limit", stored.Run?.LimitReached is { } limit ? SqlNames.Of(limit) : null));
            foreach (var step in stored.Run?.Steps ?? [])
            {
                connection.Execute(transaction, """
                    INSERT INTO run_step (task_id, ordinal, kind, state, call_id, tool, risk, approval, input, result_resource_id, started_at, finished_at, error_kind)
                    VALUES ($task, $ord, $kind, $state, $call, $tool, $risk, $approval, $input, (SELECT id FROM resource WHERE id = $result), $started, $finished, $error);
                    """,
                    ("$task", task),
                    ("$ord", step.Ordinal),
                    ("$kind", SqlNames.Of(step.Kind)),
                    ("$state", SqlNames.Of(step.State)),
                    ("$call", step.CallId),
                    ("$tool", step.Tool),
                    ("$risk", step.Risk is { } risk ? SqlNames.Of(risk) : null),
                    ("$approval", step.Tool is null ? null : SqlNames.Of(step.Approval ?? Core.Runs.Approval.NotNeeded)),
                    ("$input", step.Input),
                    ("$result", step.Result?.ToString()),
                    ("$started", step.StartedAt.Ms()),
                    ("$finished", step.FinishedAt?.Ms()),
                    ("$error", step.Error is { } failed ? SqlNames.Of(failed) : null));
            }
        }

        /// <summary>
        /// Stored material as it was, in the workspace its conversation is in here. Its text arrives with it; the chunks and
        /// their keyword index are made again from it. Whether its original is here is the blob store's to say.
        /// </summary>
        private void Resource(ExchangeResource described, ManualBackupConversationPlan plan)
        {
            var text = described.Text is { } extracted
                ? new ResourceDerivative(DerivativeKind.Text, extracted, described.TextProducer ?? "manual-backup/1", EstimatedTokens: Core.Context.TokenEstimator.Estimate(extracted), Truncated: described.TextTruncated)
                : null;
            SqliteResourceStore.Insert(
                connection,
                transaction,
                new Resource(described.Id, plan.Workspace, described.Conversation ?? plan.Conversation.Id, described.Hash, described.MediaType, described.Size, described.Origin, described.Name, described.SourceUrl, described.Provenance, described.CreatedAt),
                text is null ? [] : [text],
                text is null ? [] : ResourceChunker.Split(text.Text));
        }

        /// <summary>A new branch with its source as stored, and its context built from the sources, as a shared file's is.</summary>
        private void Branch(ManualBackupConversationPlan plan)
        {
            if (plan.Conversation.Parent is not { } parent)
            {
                return;
            }

            var child = plan.Conversation.Id;
            var source = plan.Conversation.Content!.Source;
            var seq = source is null ? null : SeqOf(parent, source);
            if (seq is null)
            {
                SqliteBranchStore.InsertWithoutSource(connection, transaction, child, parent, source?.At ?? plan.Conversation.StartedAt);
                return;
            }

            connection.Execute(transaction, """
                INSERT INTO branch_source (child_id, parent_id, source_message_id, source_block, quote, anchor, anchor_version, render_profile, created_at, source_offset)
                VALUES ($child, $parent, $message, $block, $quote, $anchor, $version, $profile, $at, $offset);
                """,
                ("$child", child.ToString()),
                ("$parent", parent.ToString()),
                ("$message", source!.Message.ToString()),
                ("$block", source.Block ?? source.Position?.Block),
                ("$quote", source.Quote),
                ("$anchor", source.Anchor?.Data),
                ("$version", source.Anchor?.Version),
                ("$profile", source.Anchor?.Profile),
                ("$at", (source.At ?? plan.Conversation.StartedAt).Ms()),
                ("$offset", source.Position?.Offset));
            foreach (var range in BranchRules.RangesForChild(parent, SqliteBranchStore.ReadRanges(connection, transaction, parent), seq.Value))
            {
                connection.Execute(transaction, "INSERT INTO branch_context_range (child_id, ancestor_id, depth, max_seq) VALUES ($child, $ancestor, $depth, $max);",
                    ("$child", child.ToString()), ("$ancestor", range.Ancestor.ToString()), ("$depth", range.Depth), ("$max", range.MaxSeq));
            }
        }

        /// <summary>A branch here that left from no message gains the one the package names; a stored source is never replaced.</summary>
        private void GainSource(ManualBackupConversationPlan plan)
        {
            var source = plan.Conversation.Content!.Source!;
            if (plan.Conversation.Parent is not { } parent || SeqOf(parent, source) is null)
            {
                return;
            }

            var set = connection.Execute(transaction, """
                UPDATE branch_source
                SET source_message_id = $message, source_block = $block, source_offset = $offset, quote = $quote, anchor = $anchor, anchor_version = $version, render_profile = $profile
                WHERE child_id = $child AND parent_id = $parent AND source_message_id IS NULL;
                """,
                ("$child", plan.Conversation.Id.ToString()),
                ("$parent", parent.ToString()),
                ("$message", source.Message.ToString()),
                ("$block", source.Block ?? source.Position?.Block),
                ("$offset", source.Position?.Offset),
                ("$quote", source.Quote),
                ("$anchor", source.Anchor?.Data),
                ("$version", source.Anchor?.Version),
                ("$profile", source.Anchor?.Profile));
            if (set == 1)
            {
                SqliteBranchStore.SetContext(connection, transaction, [plan.Conversation.Id]);
            }
        }

        /// <summary>Where the source message is in the parent here; a sentence is always an answer's.</summary>
        private int? SeqOf(ConversationId parent, ExchangeBranchSource source) =>
            (int?)connection.Scalar<long?>(transaction, "SELECT seq FROM message WHERE id = $id AND conversation_id = $c AND ($quote IS NULL OR role = 'assistant');",
                ("$id", source.Message.ToString()), ("$c", parent.ToString()), ("$quote", source.Quote));

        /// <summary>
        /// The package's summary where there is none here, with every version, its name, its current version and read marks;
        /// otherwise the versions this one lacks, as further saved versions, with name and current version unchanged.
        /// </summary>
        private SummaryNodeId? Summary(ManualBackupConversationPlan plan, ManualBackupStoredConversation? held)
        {
            if (plan.Conversation.Content!.History is not { } history)
            {
                return held?.Node;
            }

            if (plan.Versions.Count == 0)
            {
                return held?.Node ?? (plan.NewSummary ? history.Node : null);
            }

            var conversation = plan.Conversation.Id;
            var backup = ManualBackupImportRules.Line(plan.Conversation.Content.Messages);
            var line = plan.Line ?? ConversationJoin.Join(held!.Messages, backup);
            var node = plan.NewSummary ? history.Node : held!.Node!.Value;
            if (plan.NewSummary)
            {
                connection.Execute(transaction, """
                    INSERT INTO summary_node (id, workspace_id, conversation_id, current_version_id, pending_version_id, base_title, display_name, name_source, created_at, updated_at)
                    VALUES ($id, $ws, $conv, $current, NULL, $base, $name, $source, $at, $at);
                    """,
                    ("$id", node.ToString()), ("$ws", plan.Workspace.ToString()), ("$conv", conversation.ToString()), ("$current", history.Current.ToString()),
                    ("$base", history.BaseTitle), ("$name", history.Name), ("$source", SqlNames.Of(history.NameSource)), ("$at", history.CreatedAt.Ms()));
            }

            var next = (int)(connection.Scalar<long?>(transaction, "SELECT max(seq) FROM summary_version WHERE node_id = $id;", ("$id", node.ToString())) ?? 0) + 1;
            foreach (var version in plan.Versions.OrderBy(v => v.Seq))
            {
                connection.Execute(transaction, """
                    INSERT INTO summary_version (id, node_id, seq, title, body, to_seq, origin, baseline_id, task_id, model, created_at, short_title)
                    VALUES ($id, $node, $seq, $title, $body, $to, $origin, $baseline, NULL, $model, $at, $short);
                    """,
                    ("$id", version.Id.ToString()),
                    ("$node", node.ToString()),
                    ("$seq", plan.NewSummary ? version.Seq : next++),
                    ("$title", version.Title),
                    ("$body", version.Body),
                    ("$to", line.Coverage(backup, version.ToSeq)),
                    ("$origin", SqlNames.Of(version.Origin)),
                    ("$baseline", version.Baseline?.ToString()),
                    ("$model", version.Model),
                    ("$at", version.At.Ms()),
                    ("$short", version.ShortTitle));
                if (version.Read is { } read)
                {
                    connection.Execute(transaction, "INSERT INTO summary_read_state (version_id, read_source, max_progress, locator, updated_at) VALUES ($id, $source, 0, NULL, $at);",
                        ("$id", version.Id.ToString()), ("$source", SqlNames.Of(read)), ("$at", at.Ms()));
                }
            }

            return node;
        }

        /// <summary>A link as stored, between the two summaries as they are here.</summary>
        private void Link(ManualBackupWorkspacePlan plan, ExchangeLink link, Dictionary<ConversationId, SummaryNodeId> nodes)
        {
            if (!nodes.TryGetValue(link.A, out var first) || !nodes.TryGetValue(link.B, out var second))
            {
                return;
            }

            var workspace = plan.Conversations.First(c => c.Conversation.Id == link.A).Workspace;
            var (a, b) = SummaryLinkPair.Normalize(first, second);
            var id = link.Stored is { } kept && connection.Scalar<long>(transaction, "SELECT EXISTS (SELECT 1 FROM summary_link WHERE id = $id);", ("$id", kept.Id.ToString())) == 0
                ? kept.Id
                : SummaryLinkId.New();
            var pair = new (string, object?)[] { ("$ws", workspace.ToString()), ("$a", a.ToString()), ("$b", b.ToString()) };
            connection.Execute(transaction, "INSERT INTO summary_link (id, workspace_id, node_a, node_b, note, origin, created_at) VALUES ($id, $ws, $a, $b, $note, $origin, $at);",
                [.. pair, ("$id", id.ToString()), ("$note", SummaryLinkNote.Normalize(link.Note)), ("$origin", SqliteSummaryLinkStore.OriginName(link.Stored?.Origin ?? SummaryLinkOrigin.User)), ("$at", (link.Stored?.At ?? at).Ms())]);
            connection.Execute(transaction, "DELETE FROM link_suggestion WHERE workspace_id = $ws AND node_a = $a AND node_b = $b AND state = 'pending';", pair);
        }
    }
}
