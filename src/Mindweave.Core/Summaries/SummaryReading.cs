using System.Collections.Immutable;

namespace Mindweave.Core.Summaries;

// Half-open Unicode scalar offsets in NormalizeReadableText(renderer's actual readable text), not Markdown/scroll percentages.
public sealed record ReadableInterval(int Start, int End);
public sealed record SummaryReadState(Guid VersionId, long Epoch, long Revision, bool IsRead, bool SuppressAutomatic,
    string TextHash, int TextLength, ImmutableArray<ReadableInterval> Seen, TimeSpan VisibleDwell,
    long ObservationSequence, TimeSpan? LastObservationTime, bool WasActiveVisible);
public sealed record SummaryReadObservation(long Epoch, long ExpectedRevision, long Sequence, TimeSpan MonotonicTime,
    bool IsActive, bool IsVisible, bool FitsViewport, string TextHash, ImmutableArray<ReadableInterval> Intervals);
public sealed record SummaryReadLocation(int ScalarOffset, double OffsetWithinBlock, string RendererProfile);
public sealed record SummaryReadingSnapshot(SummaryReadState State, SummaryReadLocation? Location);
public sealed record SummaryDraftToken(Guid NodeId, Guid BaseVersionId, long Epoch, long Revision);
public sealed record SummaryEditDraft(SummaryDraftToken Token, string? Title, string? Markdown, bool IsDirty);
public sealed record SummaryDraftCommit(bool CreatesVersion, SummaryContent Content, Guid BaselineVersionId);
