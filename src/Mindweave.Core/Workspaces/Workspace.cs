namespace Mindweave.Core.Workspaces;

public sealed record Workspace(Guid Id, string Name, DateTimeOffset CreatedAt);
