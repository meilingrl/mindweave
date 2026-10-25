namespace Mindweave.Core.Workspaces;

/// <summary>Stable identity of a topic workspace. Time-ordered UUIDv7, stored as lowercase text.</summary>
public readonly record struct WorkspaceId(Guid Value)
{
    public static WorkspaceId New() => new(Guid.CreateVersion7());

    public static WorkspaceId Parse(string text) => new(Guid.ParseExact(text, "D"));

    public override string ToString() => Value.ToString("D");
}
