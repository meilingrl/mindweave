using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Mindweave.Desktop.Views.Conversations;

/// <summary>Encoded image bytes to a bitmap for the view (view models hold bytes, never Avalonia types).</summary>
public static class AttachmentImages
{
    /// <summary>A chip's small picture: decoded at the chip's size, so a large image costs little.</summary>
    public static FuncValueConverter<byte[]?, Bitmap?> Thumbnail { get; } = new(bytes => Decode(bytes, 96));

    /// <summary>The viewer's picture, at most as wide as the viewer shows it.</summary>
    public static FuncValueConverter<byte[]?, Bitmap?> Full { get; } = new(bytes => Decode(bytes, 1_400));

    private static Bitmap? Decode(byte[]? bytes, int width)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
        }
#pragma warning disable CA1031 // A picture that does not decode shows the icon instead.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
