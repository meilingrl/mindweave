using System.Collections.Immutable;
using Mindweave.Core.Branches;
using Mindweave.Core.Chat;

namespace Mindweave.Core.Summaries;

public enum SummarySourceScope { Own, FrozenAncestor }
public sealed record KnowledgeSummarySourceRef(int Ordinal, SummarySourceScope Scope, Guid MessageId, Guid ConversationId,
    long Sequence, long Revision, string BodyHash, MessageRole Role, GenerationStatus Status, Guid? ReplyToUserId, Guid? SupersedesMessageId);
public sealed record SummaryFrozenRange(Guid ConversationId, ImmutableArray<Guid> MessageIds);
public sealed record SummaryQuoteRef(BranchSource Source, string QuoteHash, string BindingHash, string SourceBodyHash, long SourceRevision);
public sealed record KnowledgeSummaryCoverage(Guid ConversationId, long OwnHighWaterSequence,
    ImmutableArray<KnowledgeSummarySourceRef> OwnMessages, ImmutableArray<KnowledgeSummarySourceRef> FrozenAncestors,
    ImmutableArray<SummaryFrozenRange> FrozenRanges, SummaryQuoteRef? DirectQuote);
public enum SummaryMaterialKind { Instruction, OwnMessage, FrozenAncestor, DirectQuote }
// A transport envelope is not an original chat message. Path identifies each logical use within it.
public sealed record SummaryMaterialReference(int Ordinal, SummaryMaterialKind Kind, int TransportIndex, string JsonPath, Guid? MessageId, string Hash);
public sealed record KnowledgeSummaryInputPlan(int FormatVersion, string ProtocolVersion, string EstimatorProfile, int PreparedFormatVersion,
    KnowledgeSummaryCoverage Coverage, ImmutableArray<SummaryMaterialReference> Materials, AiConfiguration Configuration, string RequestHash);
public sealed record KnowledgeSummaryMaterial(Guid ConversationId, ImmutableArray<ChatMessage> OwnRows,
    ImmutableArray<ChatMessage> FrozenAncestors, ImmutableArray<SummaryFrozenRange> FrozenRanges, BranchSource? DirectSource);
