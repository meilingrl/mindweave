namespace Mindweave.Application.Resources;

/// <summary>What an image's bytes decode to: its real type (from its signature) and its size in pixels.</summary>
public sealed record ImageInfo(string MediaType, int Width, int Height)
{
    public long Pixels => (long)Width * Height;
}

/// <summary>An image re-encoded for sending (architecture §15.4): its type and bytes.</summary>
public sealed record EncodedImage(string MediaType, byte[] Bytes, int Width, int Height);

/// <summary>
/// Image decoding and scaling (architecture §15.1: image libraries only in Infrastructure). Decoding is bounded: callers
/// check <see cref="ImageInfo.Pixels"/> against <see cref="ResourceLimits.MaxImagePixels"/> before asking for a scaled copy.
/// </summary>
public interface IImageProcessor
{
    /// <summary>Type and dimensions read from the header, or null when the bytes are not a PNG, JPEG, GIF or WebP image that decodes.</summary>
    ImageInfo? Probe(byte[] bytes);

    /// <summary>
    /// The image as sent to a model: at most <paramref name="maxLongSide"/> pixels on its long side, a GIF as its first
    /// frame, in a type every service accepts (PNG, or JPEG for a JPEG). Null when the original can be sent as it is
    /// (a PNG or JPEG already within the size), or when it does not decode.
    /// </summary>
    EncodedImage? ScaleForSending(byte[] bytes, int maxLongSide);
}
