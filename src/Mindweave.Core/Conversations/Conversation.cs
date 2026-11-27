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
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// It arrived by import of a shared file, so it is read-only: no message is ever added to it, and a question asked in it
    /// starts a conversation of the user's own (requirements §10.7, decision I4).
    /// </summary>
    public bool IsImported { get; init; }
}
