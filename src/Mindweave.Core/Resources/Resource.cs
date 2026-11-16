namespace Mindweave.Core.Resources;

/// <summary>Who or what produced a resource. Stored as text and checked by the application, so later origins (a web page, a tool's output) need no table rebuild.</summary>
public enum ResourceOrigin
{
    /// <summary>Text the user pasted; it has no name.</summary>
    Paste,

    /// <summary>A file the user chose or dropped.</summary>
    File,
}

/// <summary>
/// One piece of stored material (evolution D5, docs/design/resources.md): the original is a blob on disk named by its
/// hash; this record holds what it is, where it came from and whether it is trusted. It belongs to a workspace and, once
/// a message refers to it, to that message's conversation.
/// </summary>
public sealed record Resource(
    ResourceId Id,
    Workspaces.WorkspaceId WorkspaceId,
    Conversations.ConversationId? ConversationId,
    ContentHash Hash,
    string MediaType,
    long Size,
    ResourceOrigin Origin,
    string? Name,
    Uri? SourceUrl,
    bool Trusted,
    DateTimeOffset CreatedAt)
{
    public bool IsImage => MediaTypes.IsImage(MediaType);
}

/// <summary>Kinds of derivative made from an original. Only extracted text exists today; summaries, captions and transcripts come later (chunks get their own table).</summary>
public enum DerivativeKind
{
    /// <summary>The whole text of the resource, as sent in context.</summary>
    Text,
}

/// <summary>
/// A derivative of a resource's original, one per kind. <paramref name="Producer"/> names the extractor and its version
/// (for example <c>plain-text/1</c>) so rows made by an older extractor can be found and derived again.
/// </summary>
public sealed record ResourceDerivative(DerivativeKind Kind, string Text, string Producer);
