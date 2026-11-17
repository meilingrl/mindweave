namespace Mindweave.Application.Resources;

/// <summary>
/// Sizes the resource layer accepts (requirements §10.4.3, architecture §15.2; owner's Q6 decision 2026-11-17: adopted as
/// recommended). Adjustable constants, not settings.
/// </summary>
public static class ResourceLimits
{
    /// <summary>
    /// A paste or a dropped text this long (estimated tokens) becomes a 粘贴的文字 attachment instead of input text (owner's
    /// Q1 decision 2026-11-17: about 2,000 Chinese characters or 6,000 English ones; a constant, not a setting).
    /// </summary>
    public const int LongPasteTokens = 2_000;

    /// <summary>Whether a pasted or dropped text becomes an attachment (<see cref="LongPasteTokens"/>); typed text never does.</summary>
    public static bool IsLongPaste(string text) => text is not null && Core.Context.TokenEstimator.Estimate(text) >= LongPasteTokens;

    /// <summary>The largest file attached (50 MB).</summary>
    public const long MaxOriginalBytes = 50L * 1000 * 1000;

    /// <summary>The largest image attached (20 MB).</summary>
    public const long MaxImageBytes = 20L * 1000 * 1000;

    /// <summary>The most pixels an image may have (50 megapixels); decoding is never attempted beyond this.</summary>
    public const long MaxImagePixels = 50_000_000;

    /// <summary>Attachments one question may carry.</summary>
    public const int MaxAttachmentsPerMessage = 10;

    /// <summary>Extracted text kept per resource (2 million characters); the viewer says when it was cut. A storage bound, not a sending rule.</summary>
    public const int MaxExtractedCharacters = 2_000_000;

    /// <summary>Long side, in pixels, of an image as sent to a model (architecture §15.4).</summary>
    public const int SentImageLongSide = 2_048;

    /// <summary>PDF pages read at most; a longer document is cut there and the text marked as truncated.</summary>
    public const int MaxPdfPages = 2_000;

    /// <summary>The text, cut to <see cref="MaxExtractedCharacters"/> when longer (never inside a surrogate pair).</summary>
    public static TextExtraction Bounded(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= MaxExtractedCharacters)
        {
            return TextExtraction.Of(text);
        }

        var end = MaxExtractedCharacters;
        if (char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return TextExtraction.Of(text[..end], truncated: true);
    }
}
