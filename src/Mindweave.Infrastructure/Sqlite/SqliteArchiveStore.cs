using Microsoft.Data.Sqlite;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>
/// Archive, restore and dependency-free delete (requirements §9.5, D-021). Hidden is derived on every read: a
/// conversation is hidden when it or any ancestor in its frozen <c>branch_context_range</c> has an archive row.
/// </summary>
internal sealed class SqliteArchiveStore(SqliteDatabase database) : IConversationArchiveStore
{
    public Task<ArchiveResult> ArchiveAsync(ConversationId conversation, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                SqliteArchive.RequireConversation(connection, transaction, conversation);
                var subtree = Subtree(connection, transaction, conversation);
                var busy = new List<ConversationId>();
                foreach (var id in subtree)
                {
                    if (SqliteArchive.UnfinishedUserWork(connection, transaction, id) is not (false, false))
                    {
                        busy.Add(id);
                    }
                }

                if (busy.Count > 0)
                {
                    return new ArchiveResult(busy, subtree);
                }

                connection.Execute(transaction,
                    "INSERT INTO conversation_archive (conversation_id, archived_at) VALUES ($id, $at) ON CONFLICT (conversation_id) DO NOTHING;",
                    ("$id", conversation.ToString()), ("$at", at.Ms()));

                // Queued titles and naming of the subtree are cancelled in the same commit (MW-C14), so one whose row was
                // stored just before but that reaches the queue only afterwards cannot be marked running. Running ones are
                // stopped by the caller once this has committed.
                foreach (var id in subtree)
                {
                    connection.Execute(transaction, """
                        UPDATE generation_task SET state = 'cancelled', finished_at = $at
                        WHERE conversation_id = $id AND kind IN ('title', 'naming') AND state = 'queued';
                        """, ("$id", id.ToString()), ("$at", at.Ms()));
                }

                return new ArchiveResult([], subtree);
            },
            cancellationToken);

    public Task<RestorePlan> PlanRestoreAsync(ConversationId conversation, CancellationToken cancellationToken) =>
        database.ReadAsync(
            connection =>
            {
                var path = ArchivedOnPath(connection, null, conversation);
                var reappearing = Reappearing(connection, null, path);
                return new RestorePlan(conversation, path, reappearing.Count(c => c != conversation && !path.Contains(c)));
            },
            cancellationToken);

    public Task<IReadOnlyList<ConversationId>> RestoreAsync(ConversationId conversation, CancellationToken cancellationToken) =>
        database.WriteAsync<IReadOnlyList<ConversationId>>(
            (connection, transaction) =>
            {
                var path = ArchivedOnPath(connection, transaction, conversation);
                var reappearing = Reappearing(connection, transaction, path);
                foreach (var id in path)
                {
                    connection.Execute(transaction, "DELETE FROM conversation_archive WHERE conversation_id = $id;", ("$id", id.ToString()));
                }

                return reappearing;
            },
            cancellationToken);

    public Task<DeleteResult> DeleteAsync(ConversationId conversation, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                var id = ("$id", (object?)conversation.ToString());
                if (SqliteConversationStore.GetConversation(connection, transaction, conversation) is null)
                {
                    return new DeleteResult(DeleteBlockers.None, null, Missing: true);
                }

                var parent = connection.Scalar<string>(transaction, "SELECT parent_id FROM branch_source WHERE child_id = $id;", id) is { } p
                    ? ConversationId.Parse(p)
                    : (ConversationId?)null;
                var blockers = DeleteBlockers.None;
                if (connection.Scalar<long>(transaction, "SELECT EXISTS (SELECT 1 FROM branch_source WHERE parent_id = $id);", id) != 0)
                {
                    blockers |= DeleteBlockers.HasBranches;
                }

                if (connection.Scalar<long>(transaction, "SELECT EXISTS (SELECT 1 FROM summary_node WHERE conversation_id = $id);", id) != 0)
                {
                    blockers |= DeleteBlockers.HasSummary;
                }

                var (answer, summary) = SqliteArchive.UnfinishedUserWork(connection, transaction, conversation);
                blockers |= (answer ? DeleteBlockers.AnswerUnfinished : DeleteBlockers.None) | (summary ? DeleteBlockers.SummaryUnfinished : DeleteBlockers.None);
                if (blockers != DeleteBlockers.None)
                {
                    return new DeleteResult(blockers, parent);
                }

                // Everything that references the conversation with RESTRICT, cleared by hand in dependency order. With no
                // branches and no summary node, nothing outside the conversation references its messages (MW-C02). Task
                // rows of every kind go, and their request traces cascade; the messages lose their retry links first so
                // the self-reference never points at a deleted row. Drafts, reading positions, context summaries, message
                // blocks, ancestor ranges and the archive row cascade with the conversation.
                connection.Execute(transaction, "DELETE FROM generation_task WHERE conversation_id = $id;", id);
                connection.Execute(transaction, "DELETE FROM branch_source WHERE child_id = $id;", id);
                connection.Execute(transaction, "UPDATE message SET supersedes_id = NULL WHERE conversation_id = $id AND supersedes_id IS NOT NULL;", id);
                connection.Execute(transaction, "DELETE FROM message WHERE conversation_id = $id;", id);
                connection.Execute(transaction, "UPDATE workspace_location SET content_key = NULL WHERE content_key = $key;", ("$key", PageLocation.ConversationKey(conversation)));
                connection.Execute(transaction, "DELETE FROM conversation WHERE id = $id;", id);
                return new DeleteResult(DeleteBlockers.None, parent);
            },
            cancellationToken);

    public Task<IReadOnlySet<ConversationId>> ArchivedAmongAsync(IReadOnlyCollection<ConversationId> conversations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        return database.ReadAsync<IReadOnlySet<ConversationId>>(
            connection => conversations
                .Where(c => connection.Scalar<long>(null, "SELECT EXISTS (SELECT 1 FROM conversation_archive WHERE conversation_id = $id);", ("$id", c.ToString())) != 0)
                .ToHashSet(),
            cancellationToken);
    }

    private static List<ConversationId> Subtree(SqliteConnection connection, SqliteTransaction? transaction, ConversationId conversation)
    {
        using var command = connection.Command(transaction,
            "SELECT child_id FROM branch_context_range WHERE ancestor_id = $id ORDER BY depth, child_id;",
            ("$id", conversation.ToString()));
        using var reader = command.ExecuteReader();
        var list = new List<ConversationId> { conversation };
        while (reader.Read())
        {
            list.Add(ConversationId.Parse(reader.GetString(0)));
        }

        return list;
    }

    /// <summary>The archived conversations from the root down to <paramref name="conversation"/>, itself included.</summary>
    private static List<ConversationId> ArchivedOnPath(SqliteConnection connection, SqliteTransaction? transaction, ConversationId conversation)
    {
        using var command = connection.Command(transaction, """
            SELECT r.ancestor_id FROM branch_context_range r JOIN conversation_archive a ON a.conversation_id = r.ancestor_id
            WHERE r.child_id = $id ORDER BY r.depth;
            """, ("$id", conversation.ToString()));
        using var reader = command.ExecuteReader();
        var path = new List<ConversationId>();
        while (reader.Read())
        {
            path.Add(ConversationId.Parse(reader.GetString(0)));
        }

        if (connection.Scalar<long>(transaction, "SELECT EXISTS (SELECT 1 FROM conversation_archive WHERE conversation_id = $id);", ("$id", conversation.ToString())) != 0)
        {
            path.Add(conversation);
        }

        return path;
    }

    /// <summary>
    /// The conversations that are hidden now and would be visible once the archive rows of <paramref name="cleared"/>
    /// are gone: only the cleared ones and their subtrees can change.
    /// </summary>
    private static List<ConversationId> Reappearing(SqliteConnection connection, SqliteTransaction? transaction, List<ConversationId> cleared)
    {
        var candidates = cleared.SelectMany(c => Subtree(connection, transaction, c)).Distinct().ToList();
        var blocked = cleared.Select(c => c.ToString()).ToHashSet(StringComparer.Ordinal);
        var result = new List<ConversationId>();
        foreach (var candidate in candidates)
        {
            // Still hidden afterwards if it, or an ancestor, has an archive row that is not being cleared.
            using var command = connection.Command(transaction, """
                SELECT conversation_id FROM conversation_archive WHERE conversation_id = $id
                UNION ALL
                SELECT r.ancestor_id FROM branch_context_range r JOIN conversation_archive a ON a.conversation_id = r.ancestor_id
                WHERE r.child_id = $id;
                """, ("$id", candidate.ToString()));
            using var reader = command.ExecuteReader();
            var remaining = false;
            while (reader.Read())
            {
                remaining |= !blocked.Contains(reader.GetString(0));
            }

            if (!remaining)
            {
                result.Add(candidate);
            }
        }

        return result;
    }
}

/// <summary>The derived hidden state and the in-transaction guards shared by the stores that start new work.</summary>
internal static class SqliteArchive
{
    /// <summary>
    /// SQL that is true when the conversation named by <paramref name="column"/> is hidden: archived itself, or an
    /// ancestor is. The ancestor lookup is by <c>branch_context_range</c>'s primary key (child_id first).
    /// </summary>
    public static string HiddenCondition(string column) => $"""
        (EXISTS (SELECT 1 FROM conversation_archive ha WHERE ha.conversation_id = {column})
         OR EXISTS (SELECT 1 FROM branch_context_range hr JOIN conversation_archive ha ON ha.conversation_id = hr.ancestor_id
                    WHERE hr.child_id = {column}))
        """;

    public static bool IsHidden(SqliteConnection connection, SqliteTransaction? transaction, ConversationId conversation) =>
        connection.Scalar<long>(transaction, $"SELECT {HiddenCondition("$id")};", ("$id", conversation.ToString())) != 0;

    /// <summary>Throws <see cref="ConversationMissingException"/> when the conversation was deleted.</summary>
    public static void RequireConversation(SqliteConnection connection, SqliteTransaction transaction, ConversationId conversation)
    {
        if (connection.Scalar<long>(transaction, "SELECT EXISTS (SELECT 1 FROM conversation WHERE id = $id);", ("$id", conversation.ToString())) == 0)
        {
            throw new ConversationMissingException();
        }
    }

    /// <summary>
    /// Guard for new user work (an answer, retry, branch or summary): the conversation must exist and must not be
    /// hidden by an archive, so a hidden conversation never gets a task even if the interface is bypassed.
    /// </summary>
    public static void RequireOpenForWork(SqliteConnection connection, SqliteTransaction transaction, ConversationId conversation)
    {
        RequireConversation(connection, transaction, conversation);
        if (IsHidden(connection, transaction, conversation))
        {
            throw new ConversationArchivedException();
        }
    }

    /// <summary>Whether an answer, and whether a summary, of the conversation is queued or running (task rows decide).</summary>
    public static (bool Answer, bool Summary) UnfinishedUserWork(SqliteConnection connection, SqliteTransaction? transaction, ConversationId conversation)
    {
        using var command = connection.Command(transaction, """
            SELECT
                EXISTS (SELECT 1 FROM generation_task WHERE conversation_id = $id AND kind = 'answer' AND state IN ('queued', 'running'))
                    OR EXISTS (SELECT 1 FROM message WHERE conversation_id = $id AND state IN ('pending', 'streaming')),
                EXISTS (SELECT 1 FROM generation_task WHERE conversation_id = $id AND kind = 'summary' AND state IN ('queued', 'running'));
            """, ("$id", conversation.ToString()));
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0) != 0, reader.GetInt64(1) != 0);
    }
}
