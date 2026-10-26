namespace Mindweave.Core.Branches;

public sealed record SourceAnchor(int FormatVersion, string RendererProfile, string Payload);
public sealed record SourceSelection(Guid MessageId, long MessageRevision, string SourceBodyHash, string Quote, SourceAnchor Anchor);
public sealed record ReadingLocation(Guid MessageId, int SourceStart, double OffsetWithinBlock, int FormatVersion, string RendererProfile);
public sealed record ConversationReadState(Guid ConversationId, ReadingLocation? Location, long Revision);
