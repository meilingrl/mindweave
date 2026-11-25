using Microsoft.Data.Sqlite;
using Mindweave.Application.Exchange;
using Mindweave.Application.Import;
using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>Writes what a shared file adds, in one transaction (docs/design/sharing-import.md §2).</summary>
internal sealed class SqliteImportStore(SqliteDatabase database) : IImportStore
{
    public Task<IReadOnlyDictionary<ConversationId, WorkspaceId>> HoldingsAsync(IReadOnlyCollection<ConversationId> conversations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        return database.ReadAsync<IReadOnlyDictionary<ConversationId, WorkspaceId>>(connection => Holdings(connection, null, conversations), cancellationToken);
    }

    public Task<StoredContents> ContentsAsync(ExchangeDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return database.ReadAsync(connection => Contents(connection, null, document), cancellationToken);
    }

    public Task<ImportResult> ImportAsync(ExchangeDocument document, ImportTarget target, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(target);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                if (target.Create is null && connection.Scalar<long>(transaction, "SELECT EXISTS (SELECT 1 FROM workspace WHERE id = $ws);", ("$ws", target.Id.ToString())) == 0)
                {
                    return ImportResult.Nothing(ImportStatus.WorkspaceMissing);
                }

                // Compared again here: what was shown may have changed since.
                var holdings = Holdings(connection, transaction, ImportRules.Compared(document));
                if (ImportRules.Plan(document, holdings, target.Id, Contents(connection, transaction, document)) is not { } plan)
                {
                    return ImportResult.Nothing(ImportStatus.DoesNotFit);
                }

                if (plan.IsEmpty)
                {
                    return ImportResult.Nothing(ImportStatus.NothingNew);
                }

                if (plan.New.Count > 0 && DeepestUnder(connection, transaction, plan.New) > ImportLimits.MaxDepth)
                {
                    return ImportResult.Nothing(ImportStatus.DoesNotFit);
                }

                return Write(connection, transaction, document, target, plan, at);
            },
            cancellationToken);
    }

    public Task<IReadOnlyDictionary<ConversationId, ImportMark>> MarksAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyDictionary<ConversationId, ImportMark>>(
            connection =>
            {
                using var command = connection.Command(null, """
                    SELECT c.id, i.title, i.imported_at
                    FROM import_conversation r JOIN conversation c ON c.id = r.conversation_id JOIN import i ON i.id = r.import_id
                    WHERE c.workspace_id = $ws;
                    """, ("$ws", workspace.ToString()));
                using var reader = command.ExecuteReader();
                var marks = new Dictionary<ConversationId, ImportMark>();
                while (reader.Read())
                {
                    marks[ConversationId.Parse(reader.GetString(0))] = new ImportMark(reader.GetString(1), reader.Time(2));
                }

                return marks;
            },
            cancellationToken);

    private static ImportResult Write(SqliteConnection connection, SqliteTransaction transaction, ExchangeDocument document, ImportTarget target, ImportPlan plan, DateTimeOffset at)
    {
        if (target.Create is { } workspace)
        {
            SqliteWorkspaceStore.Insert(connection, transaction, workspace);
        }

        if (plan.New.Count > 0)
        {
            // What a later file adds to a conversation leaves the record of the file it first came from.
            var import = Guid.CreateVersion7().ToString("D");
            connection.Execute(transaction,
                "INSERT INTO import (id, title, kind, imported_at) VALUES ($id, $title, $kind, $at);",
                ("$id", import), ("$title", ImportRules.OneLine(document.Title)), ("$kind", document.Kind), ("$at", at.Ms()));
            foreach (var conversation in plan.New)
            {
                Insert(connection, transaction, conversation, target.Id, import);
            }
        }

        // Summaries before messages: a citation of a shared summary names its node, and whichever message it is in.
        var gains = plan.New.Select(c => new ImportGain(c, c.Content?.Messages ?? [], c.Content?.Source is not null, c.Content?.Summary is not null)).Concat(plan.Gains).ToList();
        foreach (var gain in gains.Where(g => g.Summary))
        {
            SqliteSummaryStore.AddReceived(connection, transaction, target.Id, gain.Conversation.Id, gain.Conversation.Content!.Summary!);
        }

        foreach (var gain in gains.Where(g => g.Messages.Count > 0))
        {
            SqliteConversationStore.AddReceived(connection, transaction, gain.Conversation.Id, [.. gain.Messages.Select(m => Message(connection, transaction, gain.Conversation.Id, m, target.Id))]);
        }

        // A source needs its message, and the context of what is below it needs the source.
        SqliteBranchStore.SetContext(connection, transaction,
            [.. gains.Where(g => g.Source && SqliteBranchStore.SetSource(connection, transaction, g.Conversation.Id, g.Conversation.Content!.Source!)).Select(g => g.Conversation.Id)]);

        foreach (var link in plan.Links)
        {
            var (a, b) = SummaryLinkPair.Normalize(NodeOf(connection, transaction, link.A)!.Value, NodeOf(connection, transaction, link.B)!.Value);
            SqliteSummaryLinkStore.Insert(connection, transaction, target.Id, a, b, SummaryLinkNote.Normalize(link.Note), SummaryLinkOrigin.User, at);
        }

        var created = plan.New.Select(c => c.Id).ToHashSet();
        return new ImportResult(
            ImportStatus.Imported,
            [.. plan.New.Select(c => c.Id)],
            [.. plan.New.Select(c => c.Parent).OfType<ConversationId>().Where(p => !created.Contains(p)).Distinct()],
            plan.New.Count > 0 ? (plan.New.FirstOrDefault(c => c.Parent is null) ?? plan.New[0]).Id : plan.Gains.Count > 0 ? plan.Gains[0].Conversation.Id : plan.Links[0].A)
        {
            Gained = [.. plan.Gains.Select(g => g.Conversation.Id)],
            Summarized = [.. gains.Where(g => g.Summary).Select(g => g.Conversation.Id)],
            Linked = plan.Links.Count > 0 ? target.Id : null,
        };
    }

    /// <summary>A message as it arrives: its text, and what stands for what did not travel (docs/design/sharing-import.md §3).</summary>
    private static Message Message(SqliteConnection connection, SqliteTransaction transaction, ConversationId conversation, ExchangeMessage message, WorkspaceId workspace)
    {
        var blocks = message.Attachments.Select(ContentBlock (name) => new AbsentAttachmentBlock(name)).ToList();
        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            blocks.Add(new TextBlock(message.Text));
        }

        if (message.Activity is { } a)
        {
            blocks.Add(new ActivityCountBlock(a.AttachmentSearches, a.SummarySearches, a.WebSearches, a.PagesRead, a.OtherCalls));
        }

        blocks.AddRange(message.Citations.Select(c => CitationBlock.WithoutMaterial(c.Number, c.Kind, c.Title, c.Url) with
        {
            // Not to open it: shared again together with its summary, the citation is still known to be of it.
            Target = c.Summary is { } of && NodeOf(connection, transaction, of, workspace) is { } node ? node.ToString() : null,
        }));
        return new Message(message.Id, conversation, message.Seq, message.Role, blocks, message.State, 0, null, null, null, message.At, message.At);
    }

    private static SummaryNodeId? NodeOf(SqliteConnection connection, SqliteTransaction? transaction, ConversationId conversation, WorkspaceId? workspace = null) =>
        connection.Scalar<string>(transaction, "SELECT id FROM summary_node WHERE conversation_id = $id AND ($ws IS NULL OR workspace_id = $ws);",
            ("$id", conversation.ToString()), ("$ws", workspace?.ToString())) is { } node ? SummaryNodeId.Parse(node) : null;

    /// <summary>It arrived by import, so it is read-only (decision I4).</summary>
    internal static bool IsReceived(SqliteConnection connection, SqliteTransaction? transaction, ConversationId conversation) =>
        connection.Scalar<long>(transaction, "SELECT EXISTS (SELECT 1 FROM import_conversation WHERE conversation_id = $id);", ("$id", conversation.ToString())) != 0;

    /// <summary>What the store has of what the file says.</summary>
    private static StoredContents Contents(SqliteConnection connection, SqliteTransaction? transaction, ExchangeDocument document)
    {
        var conversations = new Dictionary<ConversationId, StoredConversation>();
        var messages = new Dictionary<MessageId, ConversationId>();
        var versions = new Dictionary<SummaryVersionId, ConversationId>();
        foreach (var conversation in ImportRules.Compared(document))
        {
            if (Stored(connection, transaction, conversation) is { } stored)
            {
                conversations[conversation] = stored;
                foreach (var message in stored.Messages.Values)
                {
                    messages[message.Id] = conversation;
                }
            }
        }

        foreach (var content in document.Conversations.Select(c => c.Content).OfType<ExchangeContent>())
        {
            foreach (var message in content.Messages.Where(m => !messages.ContainsKey(m.Id)))
            {
                if (connection.Scalar<string>(transaction, "SELECT conversation_id FROM message WHERE id = $id;", ("$id", message.Id.ToString())) is { } at)
                {
                    messages[message.Id] = ConversationId.Parse(at);
                }
            }

            if (content.Summary is { } summary && connection.Scalar<string>(transaction,
                "SELECT n.conversation_id FROM summary_version v JOIN summary_node n ON n.id = v.node_id WHERE v.id = $id;", ("$id", summary.Id.ToString())) is { } of)
            {
                versions[summary.Id] = ConversationId.Parse(of);
            }
        }

        var linked = new HashSet<(ConversationId, ConversationId)>();
        foreach (var link in document.Links)
        {
            if (NodeOf(connection, transaction, link.A) is { } one && NodeOf(connection, transaction, link.B) is { } other)
            {
                var (a, b) = SummaryLinkPair.Normalize(one, other);
                if (connection.Scalar<long>(transaction, """
                    SELECT EXISTS (SELECT 1 FROM summary_link WHERE workspace_id = (SELECT workspace_id FROM summary_node WHERE id = $a) AND node_a = $a AND node_b = $b);
                    """, ("$a", a.ToString()), ("$b", b.ToString())) != 0)
                {
                    linked.Add(StoredContents.Pair(link.A, link.B));
                }
            }
        }

        return new StoredContents(conversations, messages, versions, linked);
    }

    private static StoredConversation? Stored(SqliteConnection connection, SqliteTransaction? transaction, ConversationId id)
    {
        ConversationId? parent;
        bool received, source, summary;
        using (var command = connection.Command(transaction, """
            SELECT s.parent_id, s.source_message_id IS NOT NULL,
                   EXISTS (SELECT 1 FROM import_conversation i WHERE i.conversation_id = c.id),
                   EXISTS (SELECT 1 FROM summary_node n WHERE n.conversation_id = c.id)
            FROM conversation c LEFT JOIN branch_source s ON s.child_id = c.id WHERE c.id = $id;
            """, ("$id", id.ToString())))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                return null;
            }

            parent = reader.NullableString(0) is { } up ? ConversationId.Parse(up) : null;
            (source, received, summary) = (reader.GetInt64(1) != 0, reader.GetInt64(2) != 0, reader.GetInt64(3) != 0);
        }

        var numbers = new Dictionary<int, StoredMessage>();
        using var rows = connection.Command(transaction, "SELECT seq, id, role FROM message WHERE conversation_id = $id;", ("$id", id.ToString()));
        using var row = rows.ExecuteReader();
        while (row.Read())
        {
            numbers[row.GetInt32(0)] = new StoredMessage(MessageId.Parse(row.GetString(1)), SqlNames.ParseRole(row.GetString(2)));
        }

        return new StoredConversation(parent, received, source, summary, numbers);
    }

    /// <summary>The deepest of the new conversations, counted from its root in the store: several files together may nest no deeper than one.</summary>
    private static long DeepestUnder(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<ExchangeConversation> parentsFirst)
    {
        var depths = new Dictionary<ConversationId, long>(parentsFirst.Count);
        foreach (var conversation in parentsFirst)
        {
            depths[conversation.Id] = 1 + (conversation.Parent is not { } parent ? 0
                : depths.TryGetValue(parent, out var above) ? above
                : 1 + connection.Scalar<long>(transaction, "SELECT count(*) FROM branch_context_range WHERE child_id = $id;", ("$id", parent.ToString())));
        }

        return depths.Values.Max();
    }

    /// <summary>A conversation as every share carries it: its identity, its name, its start and its parent. Its content follows.</summary>
    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, ExchangeConversation conversation, WorkspaceId workspace, string import)
    {
        // A name from a file is one the user did not write, so a title of the user's own replaces it like any automatic one.
        var title = ImportRules.OneLine(conversation.Name ?? string.Empty) is { Length: > 0 } name ? name : null;
        SqliteConversationStore.InsertConversation(connection, transaction, new Conversation(
            conversation.Id, workspace, title, title is null ? TitleSource.None : TitleSource.Auto, conversation.StartedAt, conversation.StartedAt));
        if (conversation.Parent is { } parent)
        {
            SqliteBranchStore.InsertWithoutSource(connection, transaction, conversation.Id, parent, conversation.StartedAt);
        }

        connection.Execute(transaction,
            "INSERT INTO import_conversation (conversation_id, import_id) VALUES ($id, $import);",
            ("$id", conversation.Id.ToString()), ("$import", import));
    }

    private static Dictionary<ConversationId, WorkspaceId> Holdings(SqliteConnection connection, SqliteTransaction? transaction, IEnumerable<ConversationId> conversations)
    {
        var holdings = new Dictionary<ConversationId, WorkspaceId>();
        foreach (var conversation in conversations)
        {
            if (connection.Scalar<string>(transaction, "SELECT workspace_id FROM conversation WHERE id = $id;", ("$id", conversation.ToString())) is { } workspace)
            {
                holdings[conversation] = WorkspaceId.Parse(workspace);
            }
        }

        return holdings;
    }
}
