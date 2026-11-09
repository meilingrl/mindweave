using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// One summary in the list view: display name, markers in words, and the same tags as the canvas details
/// (未读 / 已读, 有候选更新, and <paramref name="Other"/> for the rest, such as "来源已归档").
/// </summary>
public sealed record SummaryListItem(SummaryNodeId Node, string DisplayName, string Markers, bool HasMarkers, bool Unread = false, bool HasCandidate = false, string Other = "")
{
    public bool HasOther => Other.Length > 0;
}

/// <summary>
/// A node as the canvas draws it, in canvas coordinates. <paramref name="Parent"/> is its nearest summarized source
/// (the edge it hangs from); <paramref name="IsRoot"/> marks the summary of an exploration's root conversation.
/// </summary>
public sealed record NodeView(SummaryNodeId Id, string ShortTitle, string DisplayName, double X, double Y, bool Unread, bool HasCandidate, double LabelWidth, SummaryNodeId? Parent = null, bool IsRoot = false);

/// <summary>
/// "探索 · root conversation" for an exploration. <paramref name="Anchor"/> is its root's node, or its top-left node
/// when the root has no summary; the canvas draws the caption above the bounding box of <paramref name="Members"/>,
/// so no edge of the group crosses it.
/// </summary>
public sealed record GroupCaption(string Text, SummaryNodeId Anchor, IReadOnlyList<SummaryNodeId> Members);

/// <summary>A source edge as the canvas draws it; dashed with a label when it passes unsummarized conversations.</summary>
public sealed record EdgeView(SummaryNodeId From, SummaryNodeId To, double FromX, double FromY, double ToX, double ToY, bool IsDirect, string? Label);

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
/// </summary>
public sealed record NodeDetail(SummaryNodeId Id, ConversationId Conversation, string DisplayName, string ShortTitle, string Markers, string Source, string Created, string? Via,
    bool Unread, bool HasCandidate, bool SourceArchived, string Path, IReadOnlyList<PathStep> Steps);
