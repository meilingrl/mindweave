using Microsoft.Data.Sqlite;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>
/// Archive, restore and delete with a stated scope (requirements v0.12 §8.2, §9.5; D-021). Hidden is derived on every read: a
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

    /// <summary>The per-member deletes, each run over the whole scope before the next.</summary>
    private static readonly string[] MemberSteps =
    [
        "DELETE FROM generation_task WHERE conversation_id = $id;",
        "DELETE FROM branch_source WHERE child_id = $id;",
        "DELETE FROM branch_context_range WHERE child_id = $id;",
        "UPDATE message SET supersedes_id = NULL WHERE conversation_id = $id AND supersedes_id IS NOT NULL;",
        "DELETE FROM message WHERE conversation_id = $id;",
    ];

    public Task<ConversationDeleteScope> InspectDeleteAsync(IReadOnlyCollection<ConversationId> selected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return database.ReadAsync(connection => DeleteScope(connection, null, [.. selected]), cancellationToken);
    }

    public Task<ConversationDeleteResult> DeleteAsync(ConversationDeleteScope confirmed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                // Recomputed inside the transaction: a branch or summary that came (or went) since the confirmation, or
                // work that started, refuses the whole delete; nothing of it is done in part.
                var scope = DeleteScope(connection, transaction, confirmed.Selected);
                if (scope.IsEmpty)
                {
                    return new ConversationDeleteResult(ConversationDeleteStatus.Missing, scope);
                }

                if (scope.Busy.Count > 0)
                {
                    return new ConversationDeleteResult(ConversationDeleteStatus.Busy, scope);
                }

                if (!scope.SameContents(confirmed))
                {
                    return new ConversationDeleteResult(ConversationDeleteStatus.ScopeChanged, scope);
                }

                // Every RESTRICT reference cleared before its target, step by step over the whole scope (a member may be
                // another member's parent or ancestor until both are gone). Summary nodes first: layout rows, edit drafts
                // (their baselines restrict versions), versions (read states cascade), nodes; node and version keys are
                // deferred and checked at COMMIT. Then task rows (request traces cascade), branch sources and ranges of
                // the members, retry links, messages (blocks and reading positions cascade), last pages pointing at a
                // member or one of its summaries, and the conversations (drafts, context summaries and archive rows
                // cascade). The scope is closed (architecture §4.2): no row outside it names anything in it, so an
                // ancestor's context summaries that a member reused stay with the ancestor.
                foreach (var node in scope.Summaries)
                {
                    var n = ("$n", (object?)node.ToString());
                    connection.Execute(transaction, "DELETE FROM node_layout WHERE node_id = $n;", n);
                    connection.Execute(transaction, "DELETE FROM summary_edit_draft WHERE node_id = $n;", n);
                    connection.Execute(transaction, "DELETE FROM summary_version WHERE node_id = $n;", n);
                    connection.Execute(transaction, "DELETE FROM summary_node WHERE id = $n;", n);
                    connection.Execute(transaction, "UPDATE workspace_location SET content_key = NULL WHERE content_key = $key;", ("$key", PageLocation.SummaryKey(node)));
                }

                foreach (var step in MemberSteps)
                {
                    foreach (var member in scope.Conversations)
                    {
                        connection.Execute(transaction, step, ("$id", member.ToString()));
                    }
                }

                foreach (var member in scope.Conversations)
                {
                    connection.Execute(transaction, "UPDATE workspace_location SET content_key = NULL WHERE content_key = $key;", ("$key", PageLocation.ConversationKey(member)));
                    connection.Execute(transaction, "DELETE FROM conversation WHERE id = $id;", ("$id", member.ToString()));
                }

                return new ConversationDeleteResult(ConversationDeleteStatus.Deleted, scope);
            },
            cancellationToken);
    }

    public Task<IReadOnlySet<ConversationId>> ArchivedAmongAsync(IReadOnlyCollection<ConversationId> conversations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        return database.ReadAsync<IReadOnlySet<ConversationId>>(
            connection => conversations
                .Where(c => connection.Scalar<long>(null, "SELECT EXISTS (SELECT 1 FROM conversation_archive WHERE conversation_id = $id);", ("$id", c.ToString())) != 0)
                .ToHashSet(),
            cancellationToken);
    }

    /// <summary>
    /// The union of each existing selected conversation's subtree with the summary nodes of its members (architecture
    /// §9.1). A selected conversation inside another selected one's subtree adds nothing (and is not a root).
    /// </summary>
    private static ConversationDeleteScope DeleteScope(SqliteConnection connection, SqliteTransaction? transaction, IReadOnlyList<ConversationId> selected)
    {
        var members = new HashSet<ConversationId>();
        foreach (var conversation in selected.Distinct())
        {
            if (connection.Scalar<long>(transaction, "SELECT EXISTS (SELECT 1 FROM conversation WHERE id = $id);", ("$id", conversation.ToString())) != 0)
            {
                members.UnionWith(Subtree(connection, transaction, conversation));
            }
        }

        if (members.Count == 0)
        {
            return ConversationDeleteScope.Empty(selected);
        }

        var parentOf = new Dictionary<ConversationId, ConversationId>();
        var roots = new List<ConversationId>();
        foreach (var conversation in selected.Distinct().Where(members.Contains))
        {
            var parent = connection.Scalar<string>(transaction, "SELECT parent_id FROM branch_source WHERE child_id = $id;", ("$id", conversation.ToString())) is { } p
                ? ConversationId.Parse(p)
                : (ConversationId?)null;
            if (parent is { } outside && members.Contains(outside))
            {
                continue;
            }

            roots.Add(conversation);
            if (parent is { } q)
            {
                parentOf[conversation] = q;
            }
        }

        var ordered = new List<ConversationId>(members.Count);
        var rootOf = new Dictionary<ConversationId, ConversationId>();
        foreach (var root in roots)
        {
            foreach (var member in Subtree(connection, transaction, root))
            {
                ordered.Add(member);
                rootOf[member] = root;
            }
        }

        var nodes = new List<SummaryNodeId>();
        var busy = new List<BusyConversation>();
        foreach (var member in ordered)
        {
            if (connection.Scalar<string>(transaction, "SELECT id FROM summary_node WHERE conversation_id = $id;", ("$id", member.ToString())) is { } node)
            {
                nodes.Add(SummaryNodeId.Parse(node));
            }

            var (answer, summary) = SqliteArchive.UnfinishedUserWork(connection, transaction, member);
            if (answer || summary)
            {
                busy.Add(new BusyConversation(member, answer, summary));
            }
        }

        var visible = roots.Where(r => !SqliteArchive.IsHidden(connection, transaction, r)).ToList();
        var memberList = string.Join(", ", ordered.Select((_, i) => "$m" + i));
        var memberParameters = ordered.Select((m, i) => ("$m" + i, (object?)m.ToString())).ToArray();
        return new ConversationDeleteScope(selected, roots, ordered, nodes, busy, rootOf, parentOf, visible)
        {
            Links = SqliteSummaryLinkStore.CountTouching(connection, transaction, nodes),
            Attachments = SqliteResourceScope.Attachments(connection, transaction, memberList, memberParameters),
            ResourceHashes = SqliteResourceScope.Hashes(connection, transaction, $"r.conversation_id IN ({memberList})", memberParameters),
        };
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
