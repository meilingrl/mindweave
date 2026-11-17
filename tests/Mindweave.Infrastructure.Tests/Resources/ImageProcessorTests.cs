using Mindweave.Application.Resources;
using Mindweave.Core.Resources;
using Mindweave.Infrastructure.Resources;
using SkiaSharp;

namespace Mindweave.Infrastructure.Tests.Resources;

/// <summary>
/// Image probing and scaling for sending (architecture §15.4): the type and size come from the header; a PNG or JPEG
/// within the size is sent as it is; larger ones are scaled to the long side, a JPEG staying a JPEG and everything else
/// becoming a PNG; a JPEG's EXIF orientation is applied; bad bytes are never an exception.
/// </summary>
public sealed class ImageProcessorTests
{
    /// <summary>A 1×1 GIF89a (the classic transparent pixel).</summary>
    private static readonly byte[] Gif =
    [
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00,
        0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02,
        0x02, 0x44, 0x01, 0x00, 0x3B,
    ];

    private static readonly SkiaImageProcessor Images = new();

    [Fact]
    public void Probe_reads_type_and_size_of_png_jpeg_gif_and_webp()
    {
        Assert.Equal(new ImageInfo(MediaTypes.Png, 30, 20), Images.Probe(Encode(30, 20, SKEncodedImageFormat.Png)));
        Assert.Equal(new ImageInfo(MediaTypes.Jpeg, 31, 21), Images.Probe(Encode(31, 21, SKEncodedImageFormat.Jpeg)));
        Assert.Equal(new ImageInfo(MediaTypes.WebP, 32, 22), Images.Probe(Encode(32, 22, SKEncodedImageFormat.Webp)));
        Assert.Equal(new ImageInfo(MediaTypes.Gif, 1, 1), Images.Probe(Gif));
    }

    [Fact]
    public void Probe_refuses_bytes_that_are_no_supported_image()
    {
        Assert.Null(Images.Probe([]));
        Assert.Null(Images.Probe("not an image at all"u8.ToArray()));
        Assert.Null(Images.Probe([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]));
        Assert.Null(Images.Probe(Bmp()));
    }

    [Fact]
    public void A_png_or_jpeg_within_the_size_is_sent_as_it_is()
    {
        Assert.Null(Images.ScaleForSending(Encode(300, 200, SKEncodedImageFormat.Png), ResourceLimits.SentImageLongSide));
        Assert.Null(Images.ScaleForSending(Encode(2048, 100, SKEncodedImageFormat.Jpeg), ResourceLimits.SentImageLongSide));
    }

    [Fact]
    public void A_large_png_is_scaled_to_the_long_side_and_stays_a_png()
    {
        var scaled = Images.ScaleForSending(Encode(4000, 3000, SKEncodedImageFormat.Png), ResourceLimits.SentImageLongSide);

        Assert.NotNull(scaled);
        Assert.Equal((MediaTypes.Png, 2048, 1536), (scaled.MediaType, scaled.Width, scaled.Height));
        Assert.Equal(new ImageInfo(MediaTypes.Png, 2048, 1536), Images.Probe(scaled.Bytes));
    }

    [Fact]
    public void A_large_jpeg_is_scaled_and_stays_a_jpeg()
    {
        var scaled = Images.ScaleForSending(Encode(3000, 4000, SKEncodedImageFormat.Jpeg), 1000);

        Assert.NotNull(scaled);
        Assert.Equal((MediaTypes.Jpeg, 750, 1000), (scaled.MediaType, scaled.Width, scaled.Height));
        Assert.Equal(new ImageInfo(MediaTypes.Jpeg, 750, 1000), Images.Probe(scaled.Bytes));
    }

    [Fact]
    public void Webp_and_gif_become_png_even_when_small()
    {
        var webp = Images.ScaleForSending(Encode(40, 30, SKEncodedImageFormat.Webp), ResourceLimits.SentImageLongSide);
        Assert.Equal((MediaTypes.Png, 40, 30), (webp!.MediaType, webp.Width, webp.Height));
        Assert.Equal(MediaTypes.Png, FileTypes.ImageTypeOf(webp.Bytes));

        var gif = Images.ScaleForSending(Gif, ResourceLimits.SentImageLongSide);
        Assert.Equal((MediaTypes.Png, 1, 1), (gif!.MediaType, gif.Width, gif.Height));
        Assert.Equal(MediaTypes.Png, FileTypes.ImageTypeOf(gif.Bytes));
    }

    [Fact]
    public void Bad_bytes_and_images_beyond_the_pixel_limit_are_not_scaled()
    {
        Assert.Null(Images.ScaleForSending([], 100));
        Assert.Null(Images.ScaleForSending("garbage"u8.ToArray(), 100));

        // A header announcing 10 000 × 10 000 pixels (100 megapixels) with no image data behind it: refused before decoding.
        var huge = PngHeaderOnly(10_000, 10_000);
        Assert.Equal(new ImageInfo(MediaTypes.Png, 10_000, 10_000), Images.Probe(huge));
        Assert.Null(Images.ScaleForSending(huge, 100));
    }

    [Fact]
    public void A_jpeg_turned_by_its_exif_orientation_is_reported_and_scaled_upright()
    {
        // Stored 300 × 200 with a red top-left quarter; orientation 6 means "rotate 90° clockwise to display".
        using var bitmap = new SKBitmap(300, 200);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Blue);
            canvas.DrawRect(new SKRect(0, 0, 150, 100), new SKPaint { Color = SKColors.Red });
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
        var jpeg = WithExifOrientation(data.ToArray(), 6);

        Assert.Equal(new ImageInfo(MediaTypes.Jpeg, 200, 300), Images.Probe(jpeg));
        var scaled = Images.ScaleForSending(jpeg, 150);
        Assert.Equal((MediaTypes.Jpeg, 100, 150), (scaled!.MediaType, scaled.Width, scaled.Height));

        // Turned clockwise, the stored top-left corner is now the top-right one.
        using var upright = SKBitmap.Decode(scaled.Bytes);
        Assert.True(IsRed(upright.GetPixel(upright.Width - 10, 10)), upright.GetPixel(upright.Width - 10, 10).ToString());
        Assert.False(IsRed(upright.GetPixel(10, 10)), upright.GetPixel(10, 10).ToString());
    }

    private static bool IsRed(SKColor color) => color.Red > 180 && color.Blue < 80;

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SteelBlue);
            canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 3f, new SKPaint { Color = SKColors.Orange });
        }

        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>A JPEG with an EXIF APP1 segment, holding only the orientation tag, inserted after its start marker.</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, byte orientation)
    {
        byte[] tiff =
        [
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, // big-endian header, first IFD at 8
            0x00, 0x01, // one entry
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, orientation, 0x00, 0x00, // Orientation, SHORT, 1
            0x00, 0x00, 0x00, 0x00, // no next IFD
        ];
        byte[] exif = [.. "Exif\0\0"u8, .. tiff];
        var length = exif.Length + 2;
        return [jpeg[0], jpeg[1], 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. exif, .. jpeg.AsSpan(2)];
    }

    /// <summary>A PNG signature, a valid IHDR chunk and the start of an IDAT chunk (the codec reads up to it), no pixels.</summary>
    private static byte[] PngHeaderOnly(int width, int height)
    {
        byte[] chunk =
        [
            (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
            (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
            8, 6, 0, 0, 0, // 8-bit RGBA, no interlace
        ];
        byte[] idat = [(byte)'I', (byte)'D', (byte)'A', (byte)'T', 0x78, 0x9C];
        return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, .. chunk, .. BigEndian(Crc32(chunk)), 0, 0, 0, 2, .. idat, .. BigEndian(Crc32(idat))];
    }

    private static byte[] BigEndian(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>A 1×1 24-bit BMP, a format Skia decodes but this layer does not accept.</summary>
    private static byte[] Bmp() =>
    [
        (byte)'B', (byte)'M', 58, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0,
        40, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 24, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0x13, 0x0B, 0, 0, 0x13, 0x0B, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 255, 0,
    ];
}
