using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Conversations;

/// <summary>
/// The light marks beside a conversation that its stored answers do not carry (S7 round 4, the user's acceptance test
/// 2026-11-02): a summary being written, and a new answer the user has not seen yet. Implemented by
/// <see cref="ConversationMarksService"/>.
/// </summary>
public interface IConversationMarks
{
    /// <summary>A conversation became unread or was seen. Raised on any thread.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// An answer just ended with text and marked its conversation (it was not marked yet). Raised on any thread, before
    /// <see cref="Changed"/>; never for marks restored from storage, so a hint that interrupts (2026-11-10) follows only
    /// answers that end while the application runs.
    /// </summary>
    event EventHandler<ConversationId>? Marked;

    /// <summary>
    /// An answer just ended with text while its conversation was not on screen: raised for every such answer, also when
    /// the conversation was marked already (a further answer there is news again), after <see cref="Marked"/> when both
    /// are raised. Raised on any thread; never for marks restored from storage. The new-answer hints follow it
    /// (2026-11-10).
    /// </summary>
    event EventHandler<ConversationId>? Answered;

    /// <summary>A summary of the conversation is queued or being generated.</summary>
    bool IsSummarizing(ConversationId conversation);

    /// <summary>An answer of the conversation finished with text after the user last had it on screen.</summary>
    bool IsUnread(ConversationId conversation);

    /// <summary>
    /// The conversation on screen now, or null. Showing a conversation clears its mark, and an answer that finishes while
    /// it is shown leaves none.
    /// </summary>
    void SetViewing(ConversationId? conversation);

    /// <summary>
    /// The conversations with a new answer that the sidebar would show (archived ones are left out), per workspace, for
    /// those of these workspaces that have any. The marks are kept per conversation; this tells the workspace picker and
    /// its hints where they are and how many (2026-11-10).
    /// </summary>
    Task<IReadOnlyDictionary<WorkspaceId, IReadOnlyList<ConversationId>>> UnreadByWorkspaceAsync(IReadOnlyCollection<WorkspaceId> workspaces, CancellationToken cancellationToken);

    /// <summary>Writes the newest marks if they changed since the last successful write; never faults.</summary>
    Task FlushAsync();
}
