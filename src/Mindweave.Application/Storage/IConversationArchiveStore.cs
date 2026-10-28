using Mindweave.Core.Conversations;

namespace Mindweave.Application.Storage;

/// <summary>Why a conversation cannot be deleted now. Every reason that applies is reported together.</summary>
[Flags]
public enum DeleteBlockers
{
    None = 0,

    /// <summary>Branches were created from it (they depend on its messages, MW-C02).</summary>
    HasBranches = 1,

    /// <summary>It is the source of a summary node.</summary>
    HasSummary = 2,

    /// <summary>An answer of it is queued or being generated.</summary>
    AnswerUnfinished = 4,

    /// <summary>A summary of it is queued or being generated.</summary>
    SummaryUnfinished = 8,
}

/// <summary>
/// Outcome of a delete: removed (no blockers), refused with every blocker, or <paramref name="Missing"/> when the
/// conversation no longer existed. <paramref name="Parent"/> is the conversation it was branched from, if any.
/// </summary>
public sealed record DeleteResult(DeleteBlockers Blockers, ConversationId? Parent, bool Missing = false)
{
    public bool Deleted => !Missing && Blockers == DeleteBlockers.None;
}

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

    /// <summary>
    /// Deletes a conversation without dependents together with everything that belongs only to it, after checking in
    /// the same transaction that nothing depends on it; the last page pointing at it is cleared too.
    /// </summary>
    Task<DeleteResult> DeleteAsync(ConversationId conversation, CancellationToken cancellationToken);

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
