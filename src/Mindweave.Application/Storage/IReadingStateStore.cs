using Mindweave.Core.Conversations;
using Mindweave.Core.Reading;

namespace Mindweave.Application.Storage;

/// <summary>Reading positions and unsent drafts per conversation and viewer slot. Pure page state: never content.</summary>
public interface IReadingStateStore
{
    Task<ReadingPosition?> GetPositionAsync(ConversationId conversation, ViewerSlotKey slot, CancellationToken cancellationToken);

    /// <summary>Stores the position; ignored if the message does not belong to the conversation.</summary>
    Task SavePositionAsync(ConversationId conversation, ViewerSlotKey slot, ReadingPosition position, DateTimeOffset at, CancellationToken cancellationToken);

    Task<string?> GetDraftAsync(ConversationId conversation, ViewerSlotKey slot, CancellationToken cancellationToken);

    /// <summary>Stores the draft; an empty draft removes the row.</summary>
    Task SaveDraftAsync(ConversationId conversation, ViewerSlotKey slot, string text, DateTimeOffset at, CancellationToken cancellationToken);
}
