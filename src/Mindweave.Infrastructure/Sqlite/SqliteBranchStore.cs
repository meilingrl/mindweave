using Microsoft.Data.Sqlite;
using Mindweave.Application.Exchange;
using Mindweave.Application.Storage;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;

namespace Mindweave.Infrastructure.Sqlite;

internal sealed class SqliteBranchStore(SqliteDatabase database) : IBranchStore
{
    private const string SourceColumns =
        "child_id, parent_id, source_message_id, source_block, quote, anchor, anchor_version, render_profile, created_at, source_offset";

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
                    VALUES ($child, $parent, $message, $block, $quote, $anchor, $version, $profile, $at, NULL);
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
                InsertRanges(connection, transaction, child, BranchRules.RangesForChild(parent, ReadRanges(connection, transaction, parent), source.Seq));
                return conversation;
            },
            cancellationToken);
    }

    /// <summary>Makes a stored conversation a branch that left from no message: its parent only, and nothing of any ancestor.</summary>
    internal static void InsertWithoutSource(SqliteConnection connection, SqliteTransaction transaction, ConversationId child, ConversationId parent, DateTimeOffset at)
    {
        connection.Execute(transaction,
            "INSERT INTO branch_source (child_id, parent_id, created_at) VALUES ($child, $parent, $at);",
            ("$child", child.ToString()), ("$parent", parent.ToString()), ("$at", at.Ms()));
        var ancestors = ReadRanges(connection, transaction, parent).Select(r => r.Ancestor).Append(parent).ToList();
        InsertRanges(connection, transaction, child, BranchRules.RangesWithoutSource(ancestors));
    }

    /// <summary>
    /// Makes a stored conversation a continuation of <paramref name="parent"/>: it left from the last message of
    /// <paramref name="context"/>, which is the parent's, with no sentence, and uses exactly that context, whatever has
    /// arrived since it was read (decision I4). False when the parent does not hold the message.
    /// </summary>
    internal static bool InsertContinuation(SqliteConnection connection, SqliteTransaction transaction, ConversationId child, ConversationId parent, IReadOnlyList<ContextSegment> context, DateTimeOffset at)
    {
        var message = context[^1].Messages[^1].Id;
        if (SeqOf(connection, transaction, parent, message) is null)
        {
            return false;
        }

        connection.Execute(transaction,
            "INSERT INTO branch_source (child_id, parent_id, source_message_id, created_at) VALUES ($child, $parent, $message, $at);",
            ("$child", child.ToString()), ("$parent", parent.ToString()), ("$message", message.ToString()), ("$at", at.Ms()));
        InsertRanges(connection, transaction, child, BranchRules.RangesOf([.. ReadRanges(connection, transaction, parent).Select(r => r.Ancestor), parent], context));
        return true;
    }

    /// <summary>
    /// Gives a branch that has only its parent the message it left from, as a shared file says. Its context is set apart
    /// (<see cref="SetContext"/>). False when the parent does not hold the message, or the sentence would not be an answer's.
    /// </summary>
    internal static bool SetSource(SqliteConnection connection, SqliteTransaction transaction, ConversationId child, ExchangeBranchSource source) =>
        connection.Execute(transaction, """
            UPDATE branch_source
            SET source_message_id = $message, source_block = $block, source_offset = $offset, quote = $quote, anchor = $anchor, anchor_version = $version, render_profile = $profile
            WHERE child_id = $child AND source_message_id IS NULL
              AND EXISTS (SELECT 1 FROM message m WHERE m.id = $message AND m.conversation_id = branch_source.parent_id AND ($quote IS NULL OR m.role = 'assistant'));
            """,
            ("$child", child.ToString()),
            ("$message", source.Message.ToString()),
            ("$block", source.Position?.Block),
            ("$offset", source.Position?.Offset),
            ("$quote", source.Quote),
            ("$anchor", source.Anchor?.Data),
            ("$version", source.Anchor?.Version),
            ("$profile", source.Anchor?.Profile)) == 1;

    /// <summary>
    /// Sets the context of received branches from their sources, after a file gave some of them one: of each of
    /// <paramref name="branches"/> and of the received branches below it. The user's own conversations are never rewritten,
    /// so what hangs off one of them keeps its context too (docs/design/sharing-import.md §2).
    /// </summary>
    internal static void SetContext(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<ConversationId> branches)
    {
        // Each once, and after its parent: a branch has one row for each ancestor.
        var due = new PriorityQueue<ConversationId, long>();
        var seen = new HashSet<ConversationId>();
        void Add(ConversationId id)
        {
            if (seen.Add(id))
            {
                due.Enqueue(id, connection.Scalar<long>(transaction, "SELECT count(*) FROM branch_context_range WHERE child_id = $id;", ("$id", id.ToString())));
            }
        }

        foreach (var branch in branches)
        {
            Add(branch);
        }

        while (due.TryDequeue(out var child, out _))
        {
            using (var command = connection.Command(transaction,
                "SELECT s.parent_id, m.seq FROM branch_source s LEFT JOIN message m ON m.id = s.source_message_id WHERE s.child_id = $id;", ("$id", child.ToString())))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    continue;
                }

                var parent = ConversationId.Parse(reader.GetString(0));
                var above = ReadRanges(connection, transaction, parent);
                var ranges = reader.NullableInt64(1) is { } seq
                    ? BranchRules.RangesForChild(parent, above, (int)seq)
                    : BranchRules.RangesWithoutSource([.. above.Select(r => r.Ancestor), parent]);
                foreach (var range in ranges)
                {
                    connection.Execute(transaction, "UPDATE branch_context_range SET max_seq = $max WHERE child_id = $child AND ancestor_id = $ancestor;",
                        ("$child", child.ToString()), ("$ancestor", range.Ancestor.ToString()), ("$max", range.MaxSeq));
                }
            }

            foreach (var below in ReceivedChildren(connection, transaction, child))
            {
                Add(below);
            }
        }
    }

    private static List<ConversationId> ReceivedChildren(SqliteConnection connection, SqliteTransaction transaction, ConversationId parent)
    {
        using var command = connection.Command(transaction,
            "SELECT s.child_id FROM branch_source s JOIN import_conversation i ON i.conversation_id = s.child_id WHERE s.parent_id = $id;", ("$id", parent.ToString()));
        using var reader = command.ExecuteReader();
        var children = new List<ConversationId>();
        while (reader.Read())
        {
            children.Add(ConversationId.Parse(reader.GetString(0)));
        }

        return children;
    }

    private static int? SeqOf(SqliteConnection connection, SqliteTransaction transaction, ConversationId conversation, MessageId message) =>
        (int?)connection.Scalar<long?>(transaction, "SELECT seq FROM message WHERE id = $id AND conversation_id = $conv;", ("$id", message.ToString()), ("$conv", conversation.ToString()));

    private static void InsertRanges(SqliteConnection connection, SqliteTransaction transaction, ConversationId child, IReadOnlyList<BranchContextRange> ranges)
    {
        foreach (var range in ranges)
        {
            connection.Execute(transaction, """
                INSERT INTO branch_context_range (child_id, ancestor_id, depth, max_seq) VALUES ($child, $ancestor, $depth, $max);
                """, ("$child", child.ToString()), ("$ancestor", range.Ancestor.ToString()), ("$depth", range.Depth), ("$max", range.MaxSeq));
        }
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
                .Where(range => range.MaxSeq != BranchContextRange.Nothing)
                .Select(range => new ContextSegment(
                    range.Ancestor,
                    GetSource(connection, range.Ancestor)?.Quote,
                    SqliteConversationStore.GetMessages(connection, null, range.Ancestor, range.MaxSeq))
                {
                    IsReceived = SqliteImportStore.IsReceived(connection, null, range.Ancestor),
                })
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
        var seq = SeqOf(connection, transaction, parent, selection.SourceMessage) ?? throw new BranchRejectedException(BranchRejection.SourceNotFound);
        var message = SqliteConversationStore.GetMessages(connection, transaction, parent, seq)[^1];
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

    internal static List<BranchContextRange> ReadRanges(SqliteConnection connection, SqliteTransaction? transaction, ConversationId child)
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
            reader.NullableString(2) is { } message ? MessageId.Parse(message) : null,
            (int?)reader.NullableInt64(3),
            reader.NullableString(4),
            reader.NullableString(5),
            (int?)reader.NullableInt64(6),
            reader.NullableString(7),
            reader.Time(8))
        {
            SourceOffset = (int?)reader.NullableInt64(9),
        };
}
