using System.Globalization;
using System.Text;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Recommendations;

public readonly record struct RecommendationId(Guid Value)
{
    public static RecommendationId New() => new(Guid.CreateVersion7());

    public static RecommendationId Parse(string text) => new(Guid.ParseExact(text, "D"));

    public override string ToString() => Value.ToString("D");
}

/// <summary>What is recommended (requirements §10.3): a question for 新探索, or a node for the summary network.</summary>
public enum RecommendationKind
{
    Question,
    Node,
}

/// <summary>Pending is shown; used and ignored are kept only so they are not offered again.</summary>
public enum RecommendationState
{
    Pending,
    Used,
    Ignored,
}

/// <summary>
/// One recommendation as stored (architecture "Recommendation"). <see cref="Basis"/> is the summary it extends, with its
/// shown name (<see cref="BasisName"/>) for "延伸自"; required for a node, optional for a question. An ignored row's
/// <see cref="Question"/> is only its normalized form.
/// </summary>
public sealed record Recommendation(
    RecommendationId Id,
    WorkspaceId Workspace,
    RecommendationKind Kind,
    string Question,
    string? Reason,
    SummaryNodeId? Basis,
    string? BasisName,
    Guid Generation,
    DateTimeOffset GeneratedAt,
    int Order,
    RecommendationState State)
{
    /// <summary>When it was made, or last became used or ignored (counts a network's spent slots, §10.3.2).</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// The recommendation a new root conversation was started from (requirements §10.3.5, architecture
/// "StartingRecommendation"): its identity, kind and basis summary as the page knew them when the question was sent (the
/// question may have been edited). Not a branch source: it only lets the first summary be linked to the basis.
/// </summary>
public sealed record StartingRecommendation(RecommendationId Recommendation, RecommendationKind Kind, SummaryNodeId? Basis)
{
    public static StartingRecommendation From(Recommendation recommendation)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        return new(recommendation.Id, recommendation.Kind, recommendation.Basis);
    }
}

/// <summary>One item of a new batch, already cleaned and validated.</summary>
public sealed record NewRecommendation(string Question, string? Reason, SummaryNodeId? Basis);

/// <summary>What one request made: its items, in order, with the request's identity and time.</summary>
public sealed record RecommendationBatch(Guid Generation, DateTimeOffset At, IReadOnlyList<NewRecommendation> Items);

/// <summary>How questions are compared to tell a repeat (pure; tested directly).</summary>
public static class RecommendationText
{
    /// <summary>
    /// Compatibility-normalized, lower case, without white space, punctuation or symbols: "火车 是怎么发明的？" and
    /// "火车是怎么发明的" match. Text made only of such characters is kept trimmed, so it still matches itself.
    /// </summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var folded = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var kept = new StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            var category = char.GetUnicodeCategory(ch);
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch) || category == UnicodeCategory.Format)
            {
                continue;
            }

            kept.Append(ch);
        }

        return kept.Length > 0 ? kept.ToString() : folded.Trim();
    }
}
