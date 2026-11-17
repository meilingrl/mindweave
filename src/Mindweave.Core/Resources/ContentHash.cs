namespace Mindweave.Core.Resources;

/// <summary>
/// The SHA-256 of a resource's original bytes as 64 lowercase hex digits. Originals are stored under this name, so only
/// this exact form is accepted: a path built from it can never leave the blob folder.
/// </summary>
public readonly record struct ContentHash
{
    public const int HexLength = 64;

    private ContentHash(string hex) => Hex = hex;

    public string Hex { get; }

    public static ContentHash Parse(string hex) =>
        TryParse(hex, out var hash) ? hash : throw new FormatException($"Not a lowercase SHA-256 hex digest: '{hex}'.");

    public static bool TryParse(string? hex, out ContentHash hash)
    {
        hash = default;
        if (hex is not { Length: HexLength } || !hex.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            return false;
        }

        hash = new ContentHash(hex);
        return true;
    }

    public static ContentHash FromBytes(ReadOnlySpan<byte> sha256)
    {
        if (sha256.Length != HexLength / 2)
        {
            throw new ArgumentException("A SHA-256 digest is 32 bytes.", nameof(sha256));
        }

        return new ContentHash(Convert.ToHexStringLower(sha256));
    }

    public override string ToString() => Hex;
}
