using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.SummaryLinks;

public readonly record struct LinkSuggestionId(Guid Value)
{
    public static LinkSuggestionId New() => new(Guid.CreateVersion7());

    public static LinkSuggestionId Parse(string text) => new(Guid.ParseExact(text, "D"));

    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// A pending AI-suggested link between two summaries of one workspace (requirements §10.3.4, architecture "LinkSuggestion"),
/// with both summaries' shown names. The pair is kept in the links' order (<see cref="SummaryLinkPair"/>). It is only a
/// hint: accepted, it becomes a <see cref="SummaryLink"/> with origin <see cref="SummaryLinkOrigin.Confirmed"/>; ignored,
/// it is kept without its reason so the pair is never suggested again.
/// </summary>
public sealed record LinkSuggestion(
    LinkSuggestionId Id,
    WorkspaceId Workspace,
    SummaryNodeId NodeA,
    SummaryNodeId NodeB,
    string NameA,
    string NameB,
    string? Note,
    string? Reason,
    Guid Generation,
    DateTimeOffset CreatedAt)
{
    public bool Touches(SummaryNodeId node) => NodeA == node || NodeB == node;
}

/// <summary>One validated pair of a new batch, in the links' order, with its note (at most 16 characters) and reason.</summary>
public sealed record NewLinkSuggestion(SummaryNodeId NodeA, SummaryNodeId NodeB, string? Note, string? Reason);

/// <summary>What one 建议关联 request made: its pairs, in order, with the request's identity and time.</summary>
public sealed record LinkSuggestionBatch(Guid Generation, DateTimeOffset At, IReadOnlyList<NewLinkSuggestion> Items);

/// <summary>
/// What the network shows of the workspace's link suggestions: the pending ones (oldest batch first, in batch order),
/// whether a 建议关联 request runs, and what the last finished one said when it stored none (kept in memory only, so the
/// page can say it quietly again after being reopened; null after a request that stored some).
/// </summary>
public sealed record LinkSuggestionOffer(IReadOnlyList<LinkSuggestion> Items, bool IsRunning, Recommendations.RecommendationOutcome? Outcome)
{
    public static LinkSuggestionOffer None { get; } = new([], false, null);
}
