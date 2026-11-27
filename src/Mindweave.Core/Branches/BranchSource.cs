using Mindweave.Core.Conversations;

namespace Mindweave.Core.Branches;

/// <summary>
/// The selection a branch is created from, as captured by the view's source adapter. The anchor is opaque to
/// Core: it is versioned and tied to the render profile that produced it, and only that adapter interprets it.
/// </summary>
public sealed record BranchSelection(MessageId SourceMessage, int SourceBlock, string Quote, string AnchorJson, int AnchorFormatVersion, string RenderProfile);

/// <summary>
/// Where a branch came from: the stored form of a <see cref="BranchSelection"/> plus its parent. A branch that came from a
/// structure-only share, and a user's own branch of an empty received conversation, have a parent and nothing else; a
/// source without a sentence, or a sentence without a stored anchor, is possible too (docs/design/sharing-import.md §6).
/// </summary>
public sealed record BranchSource(
    ConversationId Child,
    ConversationId Parent,
    MessageId? SourceMessage,
    int? SourceBlock,
    string? Quote,
    string? AnchorJson,
    int? AnchorFormatVersion,
    string? RenderProfile,
    DateTimeOffset CreatedAt)
{
    /// <summary>Where the quote starts in its block's Markdown, as a shared file said; a branch made here has its anchor instead.</summary>
    public int? SourceOffset { get; init; }

    /// <summary>It left from the end of a received conversation, not from a sentence in it (decision I4).</summary>
    public bool IsContinuation => SourceMessage is not null && Quote is null;

    /// <summary>A branch that left from no answer: it has its parent only.</summary>
    public static BranchSource WithoutMessage(ConversationId child, ConversationId parent, DateTimeOffset at) =>
        new(child, parent, null, null, null, null, null, null, at);
}

/// <summary>
/// An ancestor's messages a branch may use: everything up to and including <see cref="MaxSeq"/>, frozen when
/// the branch was created. <see cref="Depth"/> orders the chain from the root (0) to the direct parent.
/// </summary>
public sealed record BranchContextRange(ConversationId Ancestor, int Depth, int MaxSeq)
{
    /// <summary>The <see cref="MaxSeq"/> that says nothing of the ancestor may be used; messages are numbered from 0.</summary>
    public const int Nothing = -1;
}

public enum BranchRejection
{
    SourceNotFound,
    NotAnAnswer,
    AnswerNotFinished,
    EmptyAnswer,
    EmptyQuote,
    QuoteTooLong,
    AnchorTooLarge,
    StorageUnavailable,

    /// <summary>The conversation is archived or below an archived one; it must be restored first (D-021).</summary>
    ConversationArchived,
}

public static class BranchRules
{
    public const int MaxQuoteLength = 4000;
    public const int MaxAnchorLength = 64 * 1024;

    /// <summary>
    /// Whether an answer can be branched from: finished with its text saved (§6.2). Superseded attempts
    /// qualify too; cancelled answers never had text.
    /// </summary>
    public static BranchRejection? CheckSource(Message source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Role != MessageRole.Assistant)
        {
            return BranchRejection.NotAnAnswer;
        }

        if (!source.IsFinished)
        {
            return BranchRejection.AnswerNotFinished;
        }

        return string.IsNullOrWhiteSpace(source.Text) ? BranchRejection.EmptyAnswer : null;
    }

    public static BranchRejection? CheckSelection(BranchSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (string.IsNullOrWhiteSpace(selection.Quote))
        {
            return BranchRejection.EmptyQuote;
        }

        if (selection.Quote.Length > MaxQuoteLength)
        {
            return BranchRejection.QuoteTooLong;
        }

        return selection.AnchorJson.Length > MaxAnchorLength ? BranchRejection.AnchorTooLarge : null;
    }

    /// <summary>
    /// Ranges for a new child: the parent's own frozen ranges (so later growth of an ancestor never leaks in),
    /// plus the parent itself up to the source answer.
    /// </summary>
    public static IReadOnlyList<BranchContextRange> RangesForChild(ConversationId parent, IReadOnlyList<BranchContextRange> parentRanges, int sourceSeq)
    {
        ArgumentNullException.ThrowIfNull(parentRanges);
        return [.. parentRanges.OrderBy(r => r.Depth), new BranchContextRange(parent, parentRanges.Count, sourceSeq)];
    }

    /// <summary>Ranges that say exactly what <paramref name="context"/> holds: a row for every ancestor, and nothing of one that is not in it.</summary>
    /// <param name="ancestors">From the root to the parent.</param>
    public static IReadOnlyList<BranchContextRange> RangesOf(IReadOnlyList<ConversationId> ancestors, IReadOnlyList<ContextSegment> context)
    {
        ArgumentNullException.ThrowIfNull(ancestors);
        ArgumentNullException.ThrowIfNull(context);
        var used = context.Where(s => s.Messages.Count > 0).ToDictionary(s => s.Conversation, s => s.Messages[^1].Seq);
        return [.. ancestors.Select((a, i) => new BranchContextRange(a, i, used.TryGetValue(a, out var seq) ? seq : BranchContextRange.Nothing))];
    }

    /// <summary>
    /// Ranges for a branch without a source: a row for every ancestor, so that it counts as a branch everywhere, each saying
    /// that nothing of the ancestor is used (docs/design/sharing-import.md §2).
    /// </summary>
    /// <param name="ancestors">From the root to the parent.</param>
    public static IReadOnlyList<BranchContextRange> RangesWithoutSource(IReadOnlyList<ConversationId> ancestors)
    {
        ArgumentNullException.ThrowIfNull(ancestors);
        return [.. ancestors.Select((a, i) => new BranchContextRange(a, i, BranchContextRange.Nothing))];
    }
}
