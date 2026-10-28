using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Storage;

/// <summary>
/// Last valid page, restored on restart. Section and content keys are opaque to this layer;
/// the desktop navigation layer owns their meaning. Back history is never persisted.
/// </summary>
public sealed record PageLocation(WorkspaceId WorkspaceId, string SectionKey, string? ContentKey)
{
    /// <summary>
    /// The content key of a conversation page. Shared with storage for one reason: deleting a conversation clears a
    /// last page that points at it in the same transaction.
    /// </summary>
    public static string ConversationKey(Core.Conversations.ConversationId conversation) => "conversation:" + conversation;
}

public interface ILocationStore
{
    Task<PageLocation?> GetLastAsync(CancellationToken cancellationToken);

    Task SetLastAsync(PageLocation location, CancellationToken cancellationToken);
}
