using Mindweave.Core.Workspaces;

namespace Mindweave.Core.Conversations;

public enum TitleSource
{
    /// <summary>No stored title yet; the UI derives one from the first question.</summary>
    None,
    Auto,

    /// <summary>Set by the user; automatic titling never overwrites it.</summary>
    Manual,
}

public sealed record Conversation(
    ConversationId Id,
    WorkspaceId WorkspaceId,
    string? Title,
    TitleSource TitleSource,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
