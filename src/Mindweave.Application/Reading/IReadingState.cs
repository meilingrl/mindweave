using Mindweave.Core.Conversations;
using Mindweave.Core.Reading;

namespace Mindweave.Application.Reading;

/// <summary>Reading positions and unsent drafts as the pages use them (course requirement C3); see <see cref="ReadingStateService"/>.</summary>
public interface IReadingState
{
    Task<ReadingPosition?> GetPositionAsync(ConversationId conversation, ViewerSlotKey slot, CancellationToken cancellationToken);

    void SetPosition(ConversationId conversation, ViewerSlotKey slot, ReadingPosition position);

    Task<string> GetDraftAsync(ConversationId conversation, ViewerSlotKey slot, CancellationToken cancellationToken);

    void SetDraft(ConversationId conversation, ViewerSlotKey slot, string text);

    /// <summary>The unsent first question on a workspace's new-exploration page (no conversation exists yet); empty when none.</summary>
    Task<string> GetNewDraftAsync(Core.Workspaces.WorkspaceId workspace, CancellationToken cancellationToken);

    void SetNewDraft(Core.Workspaces.WorkspaceId workspace, string text);

    Task<bool> FlushAsync();
}
