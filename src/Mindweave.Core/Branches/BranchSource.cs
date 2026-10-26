namespace Mindweave.Core.Branches;

public sealed record BranchSource(Guid ChildConversationId, Guid ParentConversationId, Guid MessageId, string Quote, SourceAnchor Anchor);
public sealed record FrozenContextRange(Guid ConversationId, IReadOnlyList<Guid> MessageIds);
