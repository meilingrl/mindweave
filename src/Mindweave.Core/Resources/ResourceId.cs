namespace Mindweave.Core.Resources;

/// <summary>A stored resource's identity: a time-ordered UUIDv7 stored as lowercase text, like the other ids.</summary>
public readonly record struct ResourceId(Guid Value)
{
    public static ResourceId New() => new(Guid.CreateVersion7());

    public static ResourceId Parse(string text) => new(Guid.ParseExact(text, "D"));

    /// <summary>Parses text that may come from elsewhere (a model's tool call): any usual GUID form, surrounding spaces ignored.</summary>
    public static bool TryParse(string? text, out ResourceId id)
    {
        var ok = Guid.TryParse(text?.Trim(), out var value);
        id = new ResourceId(value);
        return ok;
    }

    public override string ToString() => Value.ToString("D");
}
