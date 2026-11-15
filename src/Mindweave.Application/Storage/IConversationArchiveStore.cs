using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.Storage;

/// <summary>
/// What deleting a selection of conversations takes (requirements v0.12 §8.2, architecture §9.1): the union of each
/// selected conversation with all its descendants (archived ones included) and the summary nodes of those
/// conversations. <paramref name="Selected"/> is what was asked for, in that order; <paramref name="Roots"/> are the
/// members whose parent is outside the scope (always selected ones), <paramref name="Conversations"/> every member, each
/// root followed by its subtree. <paramref name="RootOf"/> maps every member to its root, <paramref name="ParentOf"/>
/// a root to the conversation it was branched from (outside the scope), and <paramref name="VisibleRoots"/> are the
/// roots not hidden by an archive (the ones "改为归档" can act on). <paramref name="Busy"/> names members with a
/// user-started answer or summary queued or running.
/// </summary>
public sealed record ConversationDeleteScope(
    IReadOnlyList<ConversationId> Selected,
    IReadOnlyList<ConversationId> Roots,
    IReadOnlyList<ConversationId> Conversations,
    IReadOnlyList<SummaryNodeId> Summaries,
    IReadOnlyList<BusyConversation> Busy,
    IReadOnlyDictionary<ConversationId, ConversationId> RootOf,
    IReadOnlyDictionary<ConversationId, ConversationId> ParentOf,
    IReadOnlyList<ConversationId> VisibleRoots)
{
    /// <summary>Nothing selected exists any more.</summary>
    public bool IsEmpty => Conversations.Count == 0;

    /// <summary>Members below a root: the branches that go with it.</summary>
    public int Branches => Conversations.Count - Roots.Count;

    /// <summary>
    /// The delete takes branches or summaries along: it is confirmed with counts and backed up first. A selection of
    /// conversations with neither is the plain, dependency-free delete.
    /// </summary>
    public bool IsCascade => Branches > 0 || Summaries.Count > 0;

    /// <summary>
    /// How many content links (requirements §10.3.3) go with <see cref="Summaries"/>: each link touching one of them, once.
    /// Stated in the confirmation (§8.2); a relation, not content, so it is not part of <see cref="SameContents"/>.
    /// </summary>
    public int Links { get; init; }

    /// <summary>The same conversations and summary nodes, whatever the order (the in-transaction check).</summary>
    public bool SameContents(ConversationDeleteScope other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Conversations.Count == other.Conversations.Count && Summaries.Count == other.Summaries.Count
            && Conversations.ToHashSet().SetEquals(other.Conversations) && Summaries.ToHashSet().SetEquals(other.Summaries);
    }

    public static ConversationDeleteScope Empty(IReadOnlyList<ConversationId> selected) =>
        new(selected, [], [], [], [], new Dictionary<ConversationId, ConversationId>(), new Dictionary<ConversationId, ConversationId>(), []);
}

public enum ConversationDeleteStatus
{
    /// <summary>Everything in the scope is gone.</summary>
    Deleted,

    /// <summary>A user-started answer or summary in the scope is queued or running; nothing was deleted.</summary>
    Busy,

    /// <summary>The scope is no longer what was confirmed (a branch or summary came or went); nothing was deleted.</summary>
    ScopeChanged,

    /// <summary>None of the selected conversations exists any more.</summary>
    Missing,

    /// <summary>The backup a cascading delete makes first did not succeed, so nothing was deleted.</summary>
    BackupFailed,
}

/// <summary>Outcome of a delete transaction; <paramref name="Scope"/> is the scope as computed inside it.</summary>
public sealed record ConversationDeleteResult(ConversationDeleteStatus Status, ConversationDeleteScope Scope);

/// <summary>
/// Outcome of archiving: refused when a conversation of the subtree still has an answer or summary queued or
/// running (<paramref name="Busy"/> names them); otherwise <paramref name="Subtree"/> is everything now hidden.
/// </summary>
public sealed record ArchiveResult(IReadOnlyList<ConversationId> Busy, IReadOnlyList<ConversationId> Subtree)
{
    public bool Archived => Busy.Count == 0;
}

/// <summary>
/// What restoring <paramref name="Conversation"/> clears (D-021): the archived conversations on its path from the root,
/// root first and itself included when archived, and how many other conversations become visible with it.
/// </summary>
public sealed record RestorePlan(ConversationId Conversation, IReadOnlyList<ConversationId> ArchivedOnPath, int OthersReappearing)
{
    /// <summary>Hidden: archived itself or below an archived ancestor.</summary>
    public bool IsHidden => ArchivedOnPath.Count > 0;

    /// <summary>Archived by the user itself, not only hidden by an ancestor.</summary>
    public bool IsArchived => ArchivedOnPath.Count > 0 && ArchivedOnPath[^1] == Conversation;

    /// <summary>Archived ancestors that a restore clears too; a deep restore is confirmed first.</summary>
    public IReadOnlyList<ConversationId> ArchivedAncestors => [.. ArchivedOnPath.Where(c => c != Conversation)];
}

/// <summary>
/// Archiving, restoring and deleting conversations (requirements §9.5, D-021). Every write is one transaction that
/// re-checks its preconditions inside it. An archive row marks only the conversation the user archived; being
/// hidden below an archived ancestor is derived on read and never written.
/// </summary>
public interface IConversationArchiveStore
{
    /// <summary>
    /// Archives the conversation unless its subtree has an answer or summary queued or running. Only its own archive row
    /// is written, and the queued title and naming task rows of the subtree are cancelled in the same commit; nothing
    /// changes when it is refused. Throws <see cref="ConversationMissingException"/> when it no longer exists.
    /// </summary>
    Task<ArchiveResult> ArchiveAsync(ConversationId conversation, DateTimeOffset at, CancellationToken cancellationToken);

    Task<RestorePlan> PlanRestoreAsync(ConversationId conversation, CancellationToken cancellationToken);

    /// <summary>
    /// Clears the archive rows on the conversation's path (itself and its archived ancestors); archives off the path
    /// stay. Returns the conversations that became visible.
    /// </summary>
    Task<IReadOnlyList<ConversationId>> RestoreAsync(ConversationId conversation, CancellationToken cancellationToken);

    /// <summary>The delete scope of <paramref name="selected"/> as it is now (for the confirmation).</summary>
    Task<ConversationDeleteScope> InspectDeleteAsync(IReadOnlyCollection<ConversationId> selected, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the scope of <paramref name="confirmed"/>'s selection in one transaction that recomputes it inside: when
    /// it is no longer the confirmed one, or a member has a user-started answer or summary queued or running, nothing
    /// changes. Otherwise every row of every member goes (messages, tasks and request traces, branch sources and
    /// ranges, reading positions, drafts, context summaries, archive rows), with the summary nodes of the members (versions,
    /// read states, edit drafts, layout rows); a last page pointing at any of them is cleared. Nothing outside the scope
    /// references it (architecture §4.2), so nothing dangles.
    /// </summary>
    Task<ConversationDeleteResult> DeleteAsync(ConversationDeleteScope confirmed, CancellationToken cancellationToken);

    /// <summary>Those of <paramref name="conversations"/> the user archived themselves (for markers on branch entries).</summary>
    Task<IReadOnlySet<ConversationId>> ArchivedAmongAsync(IReadOnlyCollection<ConversationId> conversations, CancellationToken cancellationToken);
}

/// <summary>New work was refused inside the write transaction because the conversation is archived or below an archived one.</summary>
public sealed class ConversationArchivedException : Exception
{
    public ConversationArchivedException()
        : base("The conversation is archived; restore it first.")
    {
    }

    public ConversationArchivedException(string message)
        : base(message)
    {
    }

    public ConversationArchivedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The conversation was deleted before this write reached the database.</summary>
public sealed class ConversationMissingException : Exception
{
    public ConversationMissingException()
        : base("The conversation no longer exists.")
    {
    }

    public ConversationMissingException(string message)
        : base(message)
    {
    }

    public ConversationMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
