using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.Exchange;

/// <summary>How often an answer used tools, by kind: the one-line count and nothing else (S7). A third party's tools have no name here.</summary>
public sealed record ExchangeActivity(int AttachmentSearches, int SummarySearches, int WebSearches, int PagesRead, int OtherCalls);

/// <summary>
/// A citation as far as it may leave (S7): for the web its title and address; for the user's own material only the kind,
/// with a title only for a summary that is itself in the share (<paramref name="Summary"/> is that summary's conversation).
/// </summary>
public sealed record ExchangeCitation(int Number, CitationKind Kind, string? Title = null, Uri? Url = null, ConversationId? Summary = null);

/// <summary>A question or an answer. <paramref name="At"/> is when it was asked or finished, in UTC.</summary>
/// <param name="Seq">Its stored number in its conversation, unchanged: what is left out leaves a gap. An import places it by this.</param>
/// <param name="Text">What was typed or answered, as Markdown.</param>
public sealed record ExchangeMessage(MessageId Id, int Seq, MessageRole Role, DateTimeOffset At, MessageState State, string Text)
{
    /// <summary>The file names of a question's attachments; null for pasted text, which has none.</summary>
    public IReadOnlyList<string?> Attachments { get; init; } = [];

    /// <summary>Null when the answer used no tools.</summary>
    public ExchangeActivity? Activity { get; init; }

    public IReadOnlyList<ExchangeCitation> Citations { get; init; } = [];

    /// <summary>For an earlier attempt a shared branch left from: the answer that stands (§6.6, point 4).</summary>
    public MessageId? ReplacedBy { get; init; }

    /// <summary>The message as stored, in a manual backup only (docs/design/sharing-backup.md §1).</summary>
    public ExchangeStoredMessage? Stored { get; init; }

    /// <summary>The answer was stopped, or failed after some of its text had come.</summary>
    public bool IsIncomplete => State != MessageState.Complete;

    public bool Equals(ExchangeMessage? other) =>
        other is not null && Id == other.Id && Seq == other.Seq && Role == other.Role && At == other.At && State == other.State && Text == other.Text && Activity == other.Activity
        && ReplacedBy == other.ReplacedBy && Attachments.SequenceEqual(other.Attachments) && Citations.SequenceEqual(other.Citations) && Stored == other.Stored;

    public override int GetHashCode() => HashCode.Combine(Id, State, Text);
}

/// <summary>A place in an answer: the content block, and the offset in its Markdown (E5).</summary>
public sealed record ExchangePosition(int Block, int Offset);

/// <summary>The stored anchor of a branch as it is (§6.6, point 3); only the app that made it, in that version and profile, can read it.</summary>
public sealed record ExchangeAnchor(int Version, string Profile, string Data);

/// <summary>
/// Where a branch left from: the answer, the quoted sentence and, when known, its place. A continuation of a received
/// conversation left from its last message and has no sentence (docs/design/sharing-import.md I4).
/// </summary>
public sealed record ExchangeBranchSource(MessageId Message, string? Quote, ExchangePosition? Position, ExchangeAnchor? Anchor)
{
    /// <summary>The answer's block the sentence is in, as stored; a manual backup carries it also without an offset.</summary>
    public int? Block { get; init; }

    /// <summary>When the branch was made, in a manual backup only.</summary>
    public DateTimeOffset? At { get; init; }
}

/// <summary>A conversation's current summary version.</summary>
/// <param name="ToSeq">It covers the conversation up to the message of this number.</param>
public sealed record ExchangeSummary(SummaryVersionId Id, string Title, string Body, DateTimeOffset At, int ToSeq);

/// <summary>What was said in a conversation, in a share with content.</summary>
public sealed record ExchangeContent(IReadOnlyList<ExchangeMessage> Messages, ExchangeBranchSource? Source, ExchangeSummary? Summary)
{
    /// <summary>Every saved version of the summary, in a manual backup only; <see cref="Summary"/> is its current one.</summary>
    public ExchangeSummaryHistory? History { get; init; }

    public bool Equals(ExchangeContent? other) =>
        other is not null && Source == other.Source && Summary == other.Summary && History == other.History && Messages.SequenceEqual(other.Messages);

    public override int GetHashCode() => HashCode.Combine(Messages.Count, Source, Summary);
}
