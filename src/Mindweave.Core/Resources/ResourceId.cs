namespace Mindweave.Core.Resources;

/// <summary>A stored resource's identity: a time-ordered UUIDv7 stored as lowercase text, like the other ids.</summary>
public readonly record struct ResourceId(Guid Value)
{
    public static ResourceId New() => new(Guid.CreateVersion7());

    public static ResourceId Parse(string text) => new(Guid.ParseExact(text, "D"));

    public override string ToString() => Value.ToString("D");
}
