namespace Mindweave.Core.Resources;

/// <summary>Media types the resource layer knows by name.</summary>
public static class MediaTypes
{
    public const string PlainText = "text/plain";
    public const string Markdown = "text/markdown";
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Gif = "image/gif";
    public const string WebP = "image/webp";
    public const string Pdf = "application/pdf";
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public static bool IsImage(string mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        return mediaType.StartsWith("image/", StringComparison.Ordinal);
    }
}
