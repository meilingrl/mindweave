using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.Share;

/// <summary>
/// A conversation's content as it is stored, before anything is left out. Only <see cref="ShareContentService"/> makes it
/// and only <see cref="ShareContentFilter"/> reads it: it holds what must not leave.
/// </summary>
/// <param name="Messages">In order, every attempt included.</param>
public sealed record ShareableContent(IReadOnlyList<Message> Messages)
{
    /// <summary>Where it branched off; null for a root.</summary>
    public BranchSource? Source { get; init; }

    /// <summary>Where the quote starts in the answer's Markdown, when the app knows it exactly.</summary>
    public int? SourceOffset { get; init; }

    public SummaryNodeId? SummaryNode { get; init; }

    /// <summary>The current version.</summary>
    public SummaryVersion? Summary { get; init; }
}

/// <summary>The stored content of the ticked conversations, and the workspace's summary links with their notes.</summary>
public sealed record ShareContents(IReadOnlyDictionary<ConversationId, ShareableContent> Conversations, IReadOnlyList<SummaryLink> Links);
