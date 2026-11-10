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
    /// Which of these workspaces have a conversation with a new answer that the sidebar would show (archived ones are left
    /// out). The marks are kept per conversation; this tells the workspace picker where they are (2026-11-10).
    /// </summary>
    Task<IReadOnlySet<WorkspaceId>> WorkspacesWithUnreadAsync(IReadOnlyCollection<WorkspaceId> workspaces, CancellationToken cancellationToken);
}
