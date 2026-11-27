using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Exchange;

// What only a manual backup carries (docs/design/sharing-backup.md §1): optional fields of version 1 of the format (E5). A
// share never has them, so a share's file is as it was.

/// <summary>The workspace a manual backup's document holds: one document per workspace (§4).</summary>
public sealed record ExchangeWorkspace(WorkspaceId Id, string Name, DateTimeOffset CreatedAt);

/// <summary>That a conversation was received from a shared file, with the share's title and when it came.</summary>
public sealed record ExchangeReceived(string Title, DateTimeOffset At);

/// <summary>
/// A message as it is stored, beyond what a share carries: its blocks in full (text, attachments, tool calls and results,
/// citations with what they point to, stand-ins), which answer it replaced, and the model's name as text.
/// </summary>
public sealed record ExchangeStoredMessage(IReadOnlyList<ContentBlock> Blocks, DateTimeOffset CreatedAt)
{
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>The answer this attempt replaced, as stored.</summary>
    public MessageId? Supersedes { get; init; }

    /// <summary>Why an answer failed; its detail stays home, since it can name a connection's address.</summary>
    public GenerationErrorKind? Error { get; init; }

    /// <summary>The model that wrote an answer, as text; nothing of its connection.</summary>
    public string? Model { get; init; }

    public ExchangeRun? Run { get; init; }
}

/// <summary>The steps of an answer's run, and the limit that ended its use of tools, if one did.</summary>
public sealed record ExchangeRun(IReadOnlyList<ExchangeRunStep> Steps, RunLimit? LimitReached);

/// <summary>
/// One step of a run: a model request or a tool call, with its short input and stored result. Not the request record a model
/// step names (a usage record), nor the host a request went to, nor an error's detail.
/// </summary>
public sealed record ExchangeRunStep(int Ordinal, RunStepKind Kind, RunStepState State, DateTimeOffset StartedAt)
{
    public DateTimeOffset? FinishedAt { get; init; }

    public string? CallId { get; init; }

    public string? Tool { get; init; }

    public ToolRisk? Risk { get; init; }

    public string? Input { get; init; }

    public ResourceId? Result { get; init; }

    public GenerationErrorKind? Error { get; init; }

    public Approval? Approval { get; init; }
}

/// <summary>A conversation's summary with every saved version, which one is current, its name and the read marks.</summary>
public sealed record ExchangeSummaryHistory(
    SummaryNodeId Node,
    string Name,
    NameSource NameSource,
    string BaseTitle,
    SummaryVersionId Current,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ExchangeSummaryVersion> Versions)
{
    public bool Equals(ExchangeSummaryHistory? other) =>
        other is not null && Node == other.Node && Name == other.Name && NameSource == other.NameSource && BaseTitle == other.BaseTitle
        && Current == other.Current && CreatedAt == other.CreatedAt && Versions.SequenceEqual(other.Versions);

    public override int GetHashCode() => HashCode.Combine(Node, Current, Versions.Count);
}

/// <summary>A saved summary version. <paramref name="Read"/> says whether and how it was read; how far is a reading position and stays home.</summary>
public sealed record ExchangeSummaryVersion(
    SummaryVersionId Id,
    int Seq,
    string Title,
    string? ShortTitle,
    string Body,
    int ToSeq,
    SummaryOrigin Origin,
    SummaryVersionId? Baseline,
    string? Model,
    DateTimeOffset At,
    ReadSource? Read);

/// <summary>
/// Stored material a message of the workspace refers to: an attachment, a saved web page or search result, a tool's full
/// result. The original travels beside the document by its hash; its extracted text travels here.
/// </summary>
public sealed record ExchangeResource(
    ResourceId Id,
    ConversationId? Conversation,
    ContentHash Hash,
    string MediaType,
    long Size,
    ResourceOrigin Origin,
    string? Name,
    Uri? SourceUrl,
    ResourceProvenance Provenance,
    DateTimeOffset CreatedAt)
{
    /// <summary>The whole extracted text; null when it has none (an image).</summary>
    public string? Text { get; init; }

    public bool TextTruncated { get; init; }

    /// <summary>The extractor and its version that made the text.</summary>
    public string? TextProducer { get; init; }
}

/// <summary>A summary link as stored, beyond the pair and the note a share carries.</summary>
public sealed record ExchangeStoredLink(SummaryLinkId Id, SummaryLinkOrigin Origin, DateTimeOffset At);
