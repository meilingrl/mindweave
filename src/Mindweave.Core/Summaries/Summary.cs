using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Core.Summaries;

public readonly record struct SummaryNodeId(Guid Value)
{
    public static SummaryNodeId New() => new(Guid.CreateVersion7());

    public static SummaryNodeId Parse(string text) => new(Guid.ParseExact(text, "D"));

    public override string ToString() => Value.ToString("D");
}

public readonly record struct SummaryVersionId(Guid Value)
{
    public static SummaryVersionId New() => new(Guid.CreateVersion7());

    public static SummaryVersionId Parse(string text) => new(Guid.ParseExact(text, "D"));

    public override string ToString() => Value.ToString("D");
}

public enum SummaryOrigin
{
    Generated,
    Edited,
}

public enum NameSource
{
    /// <summary>The base title of the current version.</summary>
    Base,

    /// <summary>Base title plus a distinguishing term from same-name naming.</summary>
    Auto,

    /// <summary>Set by the user; automatic naming never changes it.</summary>
    Manual,
}

public enum ReadSource
{
    Progress,
    Manual,
    Edit,
}

/// <summary>
/// One stable summary node per source conversation (architecture §8). Its identity does not change across
/// versions or renames; the display name is separate from any version's title (§10.2).
/// </summary>
public sealed record SummaryNode(
    SummaryNodeId Id,
    WorkspaceId Workspace,
    ConversationId Conversation,
    SummaryVersionId Current,
    SummaryVersionId? Pending,
    string BaseTitle,
    string DisplayName,
    NameSource NameSource,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <param name="Seq">Creation order within the node, from 1.</param>
/// <param name="ToSeq">Coverage: the source conversation up to this message sequence number.</param>
/// <param name="Baseline">For an edit, the version it was edited from; for a generated version, the version that was current when it was requested.</param>
/// <param name="ShortTitle">The network node label for this version; null for versions without one.</param>
public sealed record SummaryVersion(
    SummaryVersionId Id,
    SummaryNodeId Node,
    int Seq,
    string Title,
    string Body,
    int ToSeq,
    SummaryOrigin Origin,
    SummaryVersionId? Baseline,
    GenerationTaskId? Task,
    string? Model,
    DateTimeOffset CreatedAt,
    string? ShortTitle = null);

/// <summary>Read state of one version (U2). <see cref="Source"/> is null while unread.</summary>
public sealed record SummaryReadState(SummaryVersionId Version, ReadSource? Source, double MaxProgress, string? Locator)
{
    public bool IsRead => Source is not null;

    public static SummaryReadState Unread(SummaryVersionId version) => new(version, null, 0, null);
}

/// <summary>The autosaved edit of a node's current version; not a version until saved explicitly.</summary>
public sealed record SummaryEditDraft(SummaryNodeId Node, SummaryVersionId Baseline, string Title, string Body, DateTimeOffset UpdatedAt);

/// <summary>A parsed summary output: title, optional network label, Markdown body.</summary>
public sealed record GeneratedSummary(string Title, string? ShortTitle, string Body);

/// <summary>Node markers derived from version state (MW-C07), shown separately.</summary>
public sealed record SummaryMarkers(bool Unread, bool HasCandidate, bool HasLaterContent);
