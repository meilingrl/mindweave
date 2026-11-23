using Mindweave.Application.SummaryNetwork;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.SummaryNetwork;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// One summary in the list view: display name, markers in words, and the same tags as the canvas details
/// (未读 / 已读, 有候选更新, and <paramref name="Other"/> for the rest, such as "来源已归档").
/// </summary>
public sealed record SummaryListItem(SummaryNodeId Node, string DisplayName, string Markers, bool HasMarkers, bool Unread = false, bool HasCandidate = false, string Other = "")
{
    public bool HasOther => Other.Length > 0;

    /// <summary>The row is a summary; a dot without one has no reading state (no 未读 / 已读 tag).</summary>
    public bool HasSummary { get; init; } = true;

    public bool ShowsUnread => HasSummary && Unread;

    public bool ShowsRead => HasSummary && !Unread;

    /// <summary>Its content links in words ("关联：铁轨（对比）"), so the list view reaches them too (D-018); empty without links.</summary>
    public string Links { get; init; } = string.Empty;

    public bool HasLinks => Links.Length > 0;
}

/// <summary>
/// A dot as the canvas draws it, in canvas coordinates. <paramref name="Parent"/> is the conversation it branched from;
/// <paramref name="IsRoot"/> marks an exploration's root. <paramref name="HasSummary"/> says whether the conversation has
/// a summary (drawn as a ring), <paramref name="Weight"/> how many questions were asked in it and its branches (the dot's
/// size), and <paramref name="CreatedAt"/> when the conversation started (day labels and the hover date). In the spatial
/// view <paramref name="Placed"/> is where the layout put the label round the dot and its wrapped lines, and
/// <paramref name="Dated"/> says a date line goes under it (a root that starts a new day); on the timeline the label goes
/// to the right of the dot on one line.
/// </summary>
public sealed record NodeView(SummaryNodeId Id, string ShortTitle, string DisplayName, double X, double Y, bool Unread, bool HasCandidate, double LabelWidth, SummaryNodeId? Parent = null, bool IsRoot = false,
    bool HasSummary = true, int Weight = 1, DateTimeOffset CreatedAt = default, SpatialLabel? Placed = null, bool Dated = false);

/// <summary>
/// The date over (or under) the root that starts a new local day. <paramref name="Anchor"/> is that root, the only one of
/// <paramref name="Members"/>.
/// </summary>
public sealed record GroupCaption(string Text, SummaryNodeId Anchor, IReadOnlyList<SummaryNodeId> Members);

/// <summary>
/// A content link as the canvas draws it (requirements §10.3.3): between two summaries, with its note (null when none)
/// shown while it is active. Never part of the layout.
/// </summary>
public sealed record LinkEdgeView(Mindweave.Application.SummaryLinks.SummaryLinkId Id, SummaryNodeId A, SummaryNodeId B, double AX, double AY, double BX, double BY, string? Note);

/// <summary>
/// A pending link suggestion as the canvas draws it (requirements §10.3.4): between two summaries,
/// light and dash-dotted, with "建议" (and the note, when there is one) at the middle. Never part of the layout.
/// </summary>
public sealed record SuggestionEdgeView(Mindweave.Application.SummaryLinks.LinkSuggestionId Id, SummaryNodeId A, SummaryNodeId B, double AX, double AY, double BX, double BY, string? Note)
{
    /// <summary>The words on the line: "建议", or "建议 · 对比".</summary>
    public string Label => UiText.LinkSuggestionLabel(Note);
}

/// <summary>
/// A line as the canvas draws it. <paramref name="Kind"/> tells a branch from the time thread between explorations, and
/// <paramref name="Curved"/> marks a timeline branch that leaves its parent's lane (its ends differ in Y).
/// </summary>
public sealed record EdgeView(SummaryNodeId From, SummaryNodeId To, double FromX, double FromY, double ToX, double ToY,
    SummaryNetworkEdgeKind Kind = SummaryNetworkEdgeKind.Branch, bool Curved = false);

/// <summary>
/// One conversation on a node's source path: a summarized one by its short name, an unsummarized one by its title,
/// shown muted with "（未总结）" so it never passes for a summary (architecture §11).
/// </summary>
public sealed record PathStep(string Name, bool Summarized, bool IsFirst)
{
    public string Text => Summarized ? Name : UiText.Unsummarized(Name);
}

/// <summary>
/// What the side panel says about the selected node; never the summary body, never marks it read.
/// <paramref name="Steps"/> is the source path step by step, root first; <paramref name="Path"/> the same in one line.
/// <paramref name="HasSummary"/> is false for a dot without a summary: no 打开总结, 关联到… or reading state.
/// <paramref name="Question"/> is the conversation's first question.
/// </summary>
public sealed record NodeDetail(SummaryNodeId Id, ConversationId Conversation, string DisplayName, string ShortTitle, string Markers, string Source, string Created,
    bool Unread, bool HasCandidate, bool SourceArchived, string Path, IReadOnlyList<PathStep> Steps, bool HasSummary = true, string? Question = null)
{
    public bool HasQuestion => !string.IsNullOrWhiteSpace(Question);

    public bool ShowsUnread => HasSummary && Unread;

    public bool ShowsRead => HasSummary && !Unread;
}

/// <summary>
/// A recommended node as the canvas, the list view and the details draw it (requirements §10.3.2): its question,
/// reason and basis summary, its place worked out beside the basis (canvas units, never stored), and the short label
/// drawn after the "推荐" tag. Not a summary: never searched, never read or unread.
/// </summary>
public sealed record RecommendedNodeView(
    Mindweave.Application.Recommendations.Recommendation Recommendation,
    SummaryNodeId Basis,
    string BasisName,
    double X,
    double Y,
    double BasisX,
    double BasisY,
    string Label,
    double LabelWidth)
{
    public Mindweave.Application.Recommendations.RecommendationId Id => Recommendation.Id;

    public string Question => Recommendation.Question;

    public string? Reason => Recommendation.Reason;

    public bool HasReason => !string.IsNullOrWhiteSpace(Reason);

    /// <summary>"推荐：<question>，延伸自 <basis>".</summary>
    public string AutomationName => UiText.RecommendedNodeName(Question, BasisName);

    public string BasisText => UiText.ExtendsFrom(BasisName);

    public string IgnoreName => UiText.IgnoreRecommendedNodeName(Question);
}
