namespace Mindweave.Core.Resources;

/// <summary>How a resource entered Mindweave. Stored as text and checked by the application, so later origins (a web page, a tool's output) need no table rebuild.</summary>
public enum ResourceOrigin
{
    /// <summary>Text the user pasted; it has no name.</summary>
    Paste,

    /// <summary>A file the user chose or dropped.</summary>
    File,

    /// <summary>An image, chosen, dropped or pasted.</summary>
    Image,
}

/// <summary>
/// Where material came from (architecture §15.2). It decides marks and, later, what may be fetched; it never makes material
/// an instruction: only the question the user typed is one, every resource is sent as material.
/// </summary>
public enum ResourceProvenance
{
    /// <summary>Pasted or attached by the user.</summary>
    User,

    /// <summary>This workspace's own content (phase 2: results of searching the summary network).</summary>
    Workspace,

    /// <summary>Web pages, search results, third-party tools; marked as untrusted when sent.</summary>
    External,
}

/// <summary>
/// One piece of stored material (architecture §15.2, docs/design/resources.md): the original is a blob on disk named by its
/// hash; this record holds what it is and where it came from. It belongs to a workspace and, once a message (or a
/// conversation's draft) refers to it, to that conversation.
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
    ResourceProvenance Provenance,
    DateTimeOffset CreatedAt)
{
    public bool IsImage => MediaTypes.IsImage(MediaType);
}

/// <summary>Kinds of derivative made from an original (architecture §15.2). Chunks have their own table.</summary>
public enum DerivativeKind
{
    /// <summary>The whole text of the resource, as extracted.</summary>
    Text,

    /// <summary>A model-written summary of the text, made once per prompt version and reused.</summary>
    Summary,

    /// <summary>An image scaled for sending; its bytes are a blob named by <see cref="ResourceDerivative.BlobHash"/>.</summary>
    ImageScaled,
}

/// <summary>
/// A derivative of a resource's original. <paramref name="Producer"/> names the extractor and its version (for example
/// <c>plain-text/1</c>) or the model that wrote it, so rows made by an older one can be found and made again.
/// </summary>
public sealed record ResourceDerivative(
    DerivativeKind Kind,
    string Text,
    string Producer,
    string? PromptVersion = null,
    ContentHash? BlobHash = null,
    int? EstimatedTokens = null,
    bool Truncated = false);

/// <summary>A part of a resource's extracted text: characters [<paramref name="Start"/>, <paramref name="End"/>) of it.</summary>
public sealed record ResourceChunk(int Ordinal, int Start, int End, int EstimatedTokens);
