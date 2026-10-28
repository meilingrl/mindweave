using Microsoft.Data.Sqlite;
using Mindweave.Application.Storage;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;

namespace Mindweave.Infrastructure.Sqlite;

internal sealed class SqliteBranchStore(SqliteDatabase database) : IBranchStore
{
    private const string SourceColumns =
        "child_id, parent_id, source_message_id, source_block, quote, anchor, anchor_version, render_profile, created_at";

    public Task<Conversation> CreateAsync(ConversationId parent, ConversationId child, BranchSelection selection, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                var parentConversation = SqliteConversationStore.GetConversation(connection, transaction, parent)
                    ?? throw new BranchRejectedException(BranchRejection.SourceNotFound);
                if (SqliteArchive.IsHidden(connection, transaction, parent))
                {
                    // A branch is new work in a hidden subtree (D-021): restore first.
                    throw new BranchRejectedException(BranchRejection.ConversationArchived);
                }

                var source = ReadSource(connection, transaction, parent, selection);

                // The child is a brand-new ID and ranges are copied only from existing ancestors, so neither a
                // self-reference nor a cycle can arise; the checks above cover dangling and foreign sources.
                var conversation = new Conversation(child, parentConversation.WorkspaceId, null, TitleSource.None, at, at);
                connection.Execute(transaction, """
                    INSERT INTO conversation (id, workspace_id, title, title_source, created_at, updated_at)
                    VALUES ($id, $ws, NULL, 'none', $at, $at);
                    """, ("$id", child.ToString()), ("$ws", conversation.WorkspaceId.ToString()), ("$at", at.Ms()));
                connection.Execute(transaction, $"""
                    INSERT INTO branch_source ({SourceColumns})
                    VALUES ($child, $parent, $message, $block, $quote, $anchor, $version, $profile, $at);
                    """,
                    ("$child", child.ToString()),
                    ("$parent", parent.ToString()),
                    ("$message", selection.SourceMessage.ToString()),
                    ("$block", selection.SourceBlock),
                    ("$quote", selection.Quote),
                    ("$anchor", selection.AnchorJson),
                    ("$version", selection.AnchorFormatVersion),
                    ("$profile", selection.RenderProfile),
                    ("$at", at.Ms()));
                foreach (var range in BranchRules.RangesForChild(parent, ReadRanges(connection, transaction, parent), source.Seq))
                {
                    connection.Execute(transaction, """
                        INSERT INTO branch_context_range (child_id, ancestor_id, depth, max_seq) VALUES ($child, $ancestor, $depth, $max);
                        """, ("$child", child.ToString()), ("$ancestor", range.Ancestor.ToString()), ("$depth", range.Depth), ("$max", range.MaxSeq));
                }

                return conversation;
            },
            cancellationToken);
    }

    public Task<BranchSource?> GetSourceAsync(ConversationId child, CancellationToken cancellationToken) =>
        database.ReadAsync(connection => GetSource(connection, child), cancellationToken);

    public Task<IReadOnlyList<BranchSource>> ListChildrenAsync(ConversationId parent, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<BranchSource>>(
            connection =>
            {
                using var command = connection.Command(null,
                    $"SELECT {SourceColumns} FROM branch_source WHERE parent_id = $parent ORDER BY created_at, rowid;",
                    ("$parent", parent.ToString()));
                using var reader = command.ExecuteReader();
                var list = new List<BranchSource>();
                while (reader.Read())
                {
                    list.Add(ReadSourceRow(reader));
                }

                return list;
            },
            cancellationToken);

    public Task<IReadOnlyList<ContextSegment>> GetAncestorContextAsync(ConversationId conversation, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<ContextSegment>>(
            connection => ReadRanges(connection, null, conversation)
                .Select(range => new ContextSegment(
                    range.Ancestor,
                    GetSource(connection, range.Ancestor)?.Quote,
                    SqliteConversationStore.GetMessages(connection, null, range.Ancestor, range.MaxSeq)))
                .ToArray(),
            cancellationToken);

    public Task<IReadOnlyList<LineageEntry>> GetLineageAsync(ConversationId conversation, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<LineageEntry>>(
            connection =>
            {
                var chain = ReadRanges(connection, null, conversation).Select(r => r.Ancestor).Append(conversation);
                var entries = new List<LineageEntry>();
                foreach (var id in chain)
                {
                    if (SqliteConversationStore.GetConversation(connection, null, id) is { } found)
                    {
                        entries.Add(new LineageEntry(found, GetSource(connection, id)));
                    }
                }

                return entries;
            },
            cancellationToken);

    /// <summary>The source message, checked to be a finished answer of the parent with saved text in the chosen block.</summary>
    private static Message ReadSource(SqliteConnection connection, SqliteTransaction transaction, ConversationId parent, BranchSelection selection)
    {
        var seq = connection.Scalar<long?>(transaction,
            "SELECT seq FROM message WHERE id = $id AND conversation_id = $parent;",
            ("$id", selection.SourceMessage.ToString()), ("$parent", parent.ToString()));
        if (seq is null)
        {
            throw new BranchRejectedException(BranchRejection.SourceNotFound);
        }

        var message = SqliteConversationStore.GetMessages(connection, transaction, parent, (int)seq.Value)[^1];
        if (BranchRules.CheckSource(message) is { } rejection)
        {
            throw new BranchRejectedException(rejection);
        }

        if (selection.SourceBlock >= message.Blocks.Count || message.Blocks[selection.SourceBlock] is not TextBlock { Markdown.Length: > 0 })
        {
            throw new BranchRejectedException(BranchRejection.EmptyAnswer);
        }

        return message;
    }

    private static List<BranchContextRange> ReadRanges(SqliteConnection connection, SqliteTransaction? transaction, ConversationId child)
    {
        using var command = connection.Command(transaction,
            "SELECT ancestor_id, depth, max_seq FROM branch_context_range WHERE child_id = $child ORDER BY depth;",
            ("$child", child.ToString()));
        using var reader = command.ExecuteReader();
        var ranges = new List<BranchContextRange>();
        while (reader.Read())
        {
            ranges.Add(new BranchContextRange(ConversationId.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetInt32(2)));
        }

        return ranges;
    }

    private static BranchSource? GetSource(SqliteConnection connection, ConversationId child)
    {
        using var command = connection.Command(null, $"SELECT {SourceColumns} FROM branch_source WHERE child_id = $child;", ("$child", child.ToString()));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSourceRow(reader) : null;
    }

    private static BranchSource ReadSourceRow(SqliteDataReader reader) =>
        new(
            ConversationId.Parse(reader.GetString(0)),
            ConversationId.Parse(reader.GetString(1)),
            MessageId.Parse(reader.GetString(2)),
            reader.GetInt32(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.Time(8));
}
