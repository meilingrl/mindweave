using Mindweave.Core.Conversations;

namespace Mindweave.Core.Reading;

/// <summary>
/// Navigation state belongs to a viewer slot (evolution seam E1). The first version has exactly one slot;
/// keys are stable strings so a second, side-by-side slot needs no migration.
/// </summary>
public readonly record struct ViewerSlotKey(string Value)
{
    public static ViewerSlotKey Primary { get; } = new("primary");

    public override string ToString() => Value;
}

/// <summary>
/// Where the reader was in a conversation: the message at the top of the viewport and, inside it, a block
/// and an opaque renderer-specific locator. The message alone stays a valid fallback when the locator
/// cannot be resolved any more (§6.3 allows returning to the corresponding paragraph).
/// </summary>
public sealed record ReadingPosition(MessageId Message, int Block, string? Locator, int LocatorVersion)
{
    public const int MaxLocatorLength = 16 * 1024;
}
