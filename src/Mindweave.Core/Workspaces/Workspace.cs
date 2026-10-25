namespace Mindweave.Core.Workspaces;

/// <summary>A topic workspace: the organizing boundary for related conversations and summaries.</summary>
public sealed record Workspace(WorkspaceId Id, WorkspaceName Name, DateTimeOffset CreatedAt);
