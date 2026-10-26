namespace Mindweave.Core.Chat;

public sealed record ContextSourceSlice(Guid MessageId, long Revision, string BodyHash, int Start, int Length);
public sealed record ContextChunk(string CacheKey, IReadOnlyList<ContextSourceSlice> Slices, int MaxOutputTokens);
public abstract record ContextPart;
public sealed record VerbatimContextPart(Guid MessageId) : ContextPart;
public sealed record SummaryContextPart(ContextChunk Chunk) : ContextPart;
public sealed record ReplyContextPlan(int FormatVersion, string EstimatorProfile, IReadOnlyList<Guid> OriginalMessageIds,
    IReadOnlyList<ContextPart> Parts, Guid? DirectSourceMessageId, string? SourceQuote);
public sealed record ContextSummaryContent(string Background, string Requirements, string Corrections, string OpenQuestions, string References);
