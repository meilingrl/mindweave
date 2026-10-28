namespace Mindweave.Desktop.Network;

public sealed record LayoutLeavePermit(Guid WorkspaceId, long Revision, bool Discard);
