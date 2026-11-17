namespace Mindweave.Application.Resources;

/// <summary>
/// Turns an original into text (docs/design/resources.md). The seam for later formats: a PDF or Office extractor is one
/// more implementation registered beside <see cref="PlainTextExtractor"/>; storage and blocks stay as they are.
/// </summary>
public interface ITextExtractor
{
    /// <summary>Extractor name and version, stored with the derived text (for example <c>plain-text/1</c>).</summary>
    string Producer { get; }

    bool CanExtract(string mediaType);

    /// <summary>The text, or null when the bytes are not what the media type promised (binary data in a ".txt").</summary>
    string? Extract(ReadOnlySpan<byte> original, string mediaType);
}
