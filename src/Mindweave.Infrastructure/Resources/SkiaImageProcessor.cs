using Mindweave.Application.Resources;
using Mindweave.Core.Resources;
using SkiaSharp;

namespace Mindweave.Infrastructure.Resources;

/// <summary>
/// Images through SkiaSharp (architecture §15.1, §15.4). The type and size come from the header without decoding; an
/// image is decoded only when it has to be scaled or converted, and never beyond <see cref="ResourceLimits.MaxImagePixels"/>.
/// A JPEG's EXIF orientation is applied when it is re-encoded, and reported dimensions are the upright ones. A JPEG stays
/// a JPEG (quality <see cref="JpegQuality"/>); everything else becomes PNG; a GIF or animated WebP gives its first frame.
/// </summary>
internal sealed class SkiaImageProcessor : IImageProcessor
{
    internal const int JpegQuality = 85;

    public ImageInfo? Probe(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            using var data = Data(bytes);
            using var codec = data is null ? null : SKCodec.Create(data);
            if (codec is null || MediaTypeOf(codec.EncodedFormat) is not { } mediaType)
            {
                return null;
            }

            var (width, height) = Upright(codec);
            return width > 0 && height > 0 ? new ImageInfo(mediaType, width, height) : null;
        }
#pragma warning disable CA1031 // Bad bytes can fail anywhere in the native codec; they are "not an image".
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    public EncodedImage? ScaleForSending(byte[] bytes, int maxLongSide)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLongSide, 1);
        try
        {
            using var data = Data(bytes);
            using var codec = data is null ? null : SKCodec.Create(data);
            if (codec is null || MediaTypeOf(codec.EncodedFormat) is not { } mediaType)
            {
                return null;
            }

            var stored = codec.Info;
            if (stored.Width <= 0 || stored.Height <= 0 || (long)stored.Width * stored.Height > ResourceLimits.MaxImagePixels)
            {
                return null;
            }

            var (width, height) = Upright(codec);
            var longSide = Math.Max(width, height);
            var keepsType = mediaType is MediaTypes.Png or MediaTypes.Jpeg;
            if (keepsType && longSide <= maxLongSide)
            {
                return null;
            }

            var scale = Math.Min(1.0, (double)maxLongSide / longSide);
            var target = (Width: Math.Max(1, (int)Math.Round(width * scale)), Height: Math.Max(1, (int)Math.Round(height * scale)));
            using var decoded = Decode(codec, scale);
            if (decoded is null)
            {
                return null;
            }

            using var upright = Orient(decoded, codec.EncodedOrigin);
            using var resized = upright.Width == target.Width && upright.Height == target.Height
                ? upright.Copy()
                : upright.Resize(new SKImageInfo(target.Width, target.Height, upright.ColorType, upright.AlphaType),
                    new SKSamplingOptions(SKCubicResampler.Mitchell));
            if (resized is null)
            {
                return null;
            }

            var jpeg = mediaType == MediaTypes.Jpeg;
            using var encoded = resized.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, jpeg ? JpegQuality : 100);
            return encoded is null ? null : new EncodedImage(jpeg ? MediaTypes.Jpeg : MediaTypes.Png, encoded.ToArray(), resized.Width, resized.Height);
        }
#pragma warning disable CA1031 // Bad bytes can fail anywhere in the native codec; they are "does not decode".
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static SKData? Data(byte[] bytes) => bytes.Length == 0 ? null : SKData.CreateCopy(bytes);

    private static string? MediaTypeOf(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Png => MediaTypes.Png,
        SKEncodedImageFormat.Jpeg => MediaTypes.Jpeg,
        SKEncodedImageFormat.Gif => MediaTypes.Gif,
        SKEncodedImageFormat.Webp => MediaTypes.WebP,
        _ => null,
    };

    private static bool SwapsSides(SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    private static (int Width, int Height) Upright(SKCodec codec) =>
        SwapsSides(codec.EncodedOrigin) ? (codec.Info.Height, codec.Info.Width) : (codec.Info.Width, codec.Info.Height);

    /// <summary>
    /// The first frame, decoded at the smallest size the codec offers that is still at least the target (a JPEG decodes
    /// at 1/2, 1/4 or 1/8 size far faster than whole), so the final resampling step stays small.
    /// </summary>
    private static SKBitmap? Decode(SKCodec codec, double scale)
    {
        var size = codec.Info.Size;
        if (scale < 1)
        {
            var scaled = codec.GetScaledDimensions((float)scale);
            if (scaled.Width >= Math.Ceiling(codec.Info.Width * scale) && scaled.Height >= Math.Ceiling(codec.Info.Height * scale))
            {
                size = scaled;
            }
        }

        var info = new SKImageInfo(size.Width, size.Height, SKImageInfo.PlatformColorType,
            codec.Info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
        {
            return bitmap;
        }

        bitmap.Dispose();
        return null;
    }

    /// <summary>The bitmap turned upright as its EXIF origin says (a copy, so the caller always owns what it gets).</summary>
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
        {
            return source.Copy();
        }

        float w = source.Width, h = source.Height;
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        var swaps = SwapsSides(origin);
        var result = new SKBitmap(new SKImageInfo(swaps ? source.Height : source.Width, swaps ? source.Width : source.Height, source.ColorType, source.AlphaType));
        using var canvas = new SKCanvas(result);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }
}
