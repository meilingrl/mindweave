using Mindweave.Core.Resources;

namespace Mindweave.Application.Resources;

/// <summary>
/// Which files become resources, by extension: text and source code (read as text), documents whose text an extractor
/// reads (PDF and Word .docx) and common images (stored as originals, checked against their file signature). Other
/// Office formats and the old binary .doc are not supported.
/// </summary>
public static class FileTypes
{
    private static readonly Dictionary<string, string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = MediaTypes.PlainText,
        [".text"] = MediaTypes.PlainText,
        [".log"] = MediaTypes.PlainText,
        [".md"] = MediaTypes.Markdown,
        [".markdown"] = MediaTypes.Markdown,
        [".csv"] = "text/csv",
        [".tsv"] = "text/tab-separated-values",
        [".json"] = "application/json",
        [".jsonl"] = "application/jsonl",
        [".xml"] = "application/xml",
        [".yaml"] = "application/yaml",
        [".yml"] = "application/yaml",
        [".toml"] = "application/toml",
        [".ini"] = MediaTypes.PlainText,
        [".cfg"] = MediaTypes.PlainText,
        [".conf"] = MediaTypes.PlainText,
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".css"] = "text/css",
        [".tex"] = "application/x-tex",
        [".srt"] = MediaTypes.PlainText,
        [".sql"] = "application/sql",
        [".cs"] = "text/x-csharp",
        [".py"] = "text/x-python",
        [".js"] = "text/javascript",
        [".mjs"] = "text/javascript",
        [".ts"] = "text/x-typescript",
        [".tsx"] = "text/x-typescript",
        [".jsx"] = "text/javascript",
        [".java"] = "text/x-java",
        [".kt"] = "text/x-kotlin",
        [".swift"] = "text/x-swift",
        [".c"] = "text/x-c",
        [".h"] = "text/x-c",
        [".cpp"] = "text/x-c++",
        [".cc"] = "text/x-c++",
        [".hpp"] = "text/x-c++",
        [".go"] = "text/x-go",
        [".rs"] = "text/x-rust",
        [".rb"] = "text/x-ruby",
        [".php"] = "text/x-php",
        [".lua"] = "text/x-lua",
        [".r"] = "text/x-r",
        [".m"] = "text/x-objectivec",
        [".scala"] = "text/x-scala",
        [".sh"] = "application/x-sh",
        [".zsh"] = "application/x-sh",
        [".ps1"] = "text/x-powershell",
        [".bat"] = "text/x-bat",
        [".axaml"] = "application/xml",
        [".xaml"] = "application/xml",
        [".csproj"] = "application/xml",
        [".props"] = "application/xml",
    };

    private static readonly Dictionary<string, string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = MediaTypes.Pdf,
        [".docx"] = MediaTypes.Docx,
    };

    private static readonly Dictionary<string, string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = MediaTypes.Png,
        [".jpg"] = MediaTypes.Jpeg,
        [".jpeg"] = MediaTypes.Jpeg,
        [".gif"] = MediaTypes.Gif,
        [".webp"] = MediaTypes.WebP,
    };

    /// <summary>The media type a file of this name is read as, or null when such files are not supported yet.</summary>
    public static string? MediaTypeOf(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var extension = Path.GetExtension(fileName);
        return TextExtensions.TryGetValue(extension, out var text) ? text
            : DocumentExtensions.TryGetValue(extension, out var document) ? document
            : ImageExtensions.TryGetValue(extension, out var image) ? image
            : null;
    }

    public static bool IsTextMediaType(string mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        return mediaType.StartsWith("text/", StringComparison.Ordinal) || TextExtensions.ContainsValue(mediaType);
    }

    /// <summary>Whether the type is a document (PDF, .docx) whose text an extractor reads from the original.</summary>
    public static bool IsDocumentMediaType(string mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        return DocumentExtensions.ContainsValue(mediaType);
    }

    /// <summary>The image type the bytes themselves say they are, or null when they are no image this layer knows.</summary>
    public static string? ImageTypeOf(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ? MediaTypes.Png
        : bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? MediaTypes.Jpeg
        : bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8) ? MediaTypes.Gif
        : bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8) ? MediaTypes.WebP
        : null;
}
