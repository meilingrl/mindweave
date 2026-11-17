namespace Mindweave.Application.Resources;

/// <summary>Why an original yielded no usable text (requirements §10.4.3: the attachment chip says why and cannot be sent).</summary>
public enum ExtractionFailure
{
    /// <summary>The bytes are not what the type promised (binary data in a ".txt", a damaged PDF or DOCX).</summary>
    NotReadable,

    /// <summary>The file opened but holds no text layer, as a scanned PDF does. No OCR is attempted (phase 1).</summary>
    NoText,

    /// <summary>The file is protected by a password.</summary>
    Encrypted,

    /// <summary>The file is beyond what is parsed (too many pages, an archive that expands too far).</summary>
    TooLarge,
}

/// <summary>The text of an original, or why there is none. <paramref name="Truncated"/> when the text was cut at <see cref="ResourceLimits.MaxExtractedCharacters"/>.</summary>
public sealed record TextExtraction(string? Text, ExtractionFailure? Failure, bool Truncated = false)
{
    public static TextExtraction Of(string text, bool truncated = false) => new(text, null, truncated);

    public static TextExtraction Failed(ExtractionFailure failure) => new(null, failure);
}

/// <summary>
/// Turns an original into text (docs/design/resources.md, architecture §15.1). Plain text lives in Application; format
/// parsers (PDF, DOCX) live in Infrastructure, registered beside it. Line endings in the result are "\n".
/// </summary>
public interface ITextExtractor
{
    /// <summary>Extractor name and version, stored with the derived text (for example <c>plain-text/1</c>).</summary>
    string Producer { get; }

    bool CanExtract(string mediaType);

    /// <summary>Never throws for bad input: a file it cannot read is a <see cref="TextExtraction.Failure"/>.</summary>
    TextExtraction Extract(byte[] original, string mediaType);
}
