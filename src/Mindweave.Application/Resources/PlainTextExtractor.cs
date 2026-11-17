using System.Text;

namespace Mindweave.Application.Resources;

/// <summary>
/// Text files and source code. UTF-8 (with or without a byte order mark) and UTF-16 with a byte order mark are read as
/// such; anything else is tried as GB18030, the usual encoding of older Chinese text files. Bytes that decode in none of
/// these, contain NUL, or decode to mostly control characters are not text. Line endings become "\n".
/// </summary>
public sealed class PlainTextExtractor : ITextExtractor
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public string Producer => "plain-text/1";

    public bool CanExtract(string mediaType) => FileTypes.IsTextMediaType(mediaType);

    public TextExtraction Extract(byte[] original, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(original);
        var text = Decode(original);
        return text is null || LooksBinary(text) ? TextExtraction.Failed(ExtractionFailure.NotReadable) : ResourceLimits.Bounded(NormalizeLineEndings(text));
    }

    public static string NormalizeLineEndings(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static string? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return TryDecode(StrictUtf8, bytes[3..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return TryDecode(new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true), bytes[2..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return TryDecode(new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true), bytes[2..]);
        }

        if (bytes.Contains((byte)0))
        {
            return null;
        }

        return TryDecode(StrictUtf8, bytes) ?? TryDecode(Gb18030(), bytes);
    }

    private static Encoding Gb18030() =>
        CodePagesEncodingProvider.Instance.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
        ?? throw new InvalidOperationException("GB18030 is not available.");

    private static string? TryDecode(Encoding encoding, ReadOnlySpan<byte> bytes)
    {
        try
        {
            return encoding.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>More than one control character (other than tab, line feed, form feed, carriage return) per hundred.</summary>
    private static bool LooksBinary(string text)
    {
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            return true;
        }

        var controls = 0;
        foreach (var c in text)
        {
            if (char.IsControl(c) && c is not ('\t' or '\n' or '\f' or '\r'))
            {
                controls++;
            }
        }

        return controls * 100 > text.Length;
    }
}
