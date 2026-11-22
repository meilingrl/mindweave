using System.Globalization;
using System.Text;

namespace Mindweave.Core.Text;

/// <summary>
/// A length limit for short text a model writes (titles, names, questions, reasons), in either script (review finding
/// 2026-11-22). Text with any CJK character is measured in <see cref="Characters"/> (text elements), exactly as before;
/// other text in <see cref="Words"/>, with at most <see cref="MaxWordLength"/> text elements per word allowed as a
/// backstop for text without spaces. Decided by the text itself, never by the interface language: the model writes in
/// the language of what it reads.
/// </summary>
public readonly record struct TextLimit(int Characters, int Words)
{
    /// <summary>Average text elements a word may take before text without CJK counts as too long.</summary>
    public const int MaxWordLength = 12;

    /// <summary>The limit one larger in both measures (a bound to cut at before checking the limit itself).</summary>
    public TextLimit Plus(int more) => new(Characters + more, Words + more);

    /// <summary>Whether <paramref name="text"/> is within the limit.</summary>
    public bool Fits(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var length = new StringInfo(text).LengthInTextElements;
        return HasCjk(text) ? length <= Characters : WordCount(text) <= Words && length <= Words * MaxWordLength;
    }

    /// <summary>
    /// The start of <paramref name="text"/> within the limit: CJK text cut between whole characters, other text after
    /// its last whole word kept (then cut between characters if still longer than the backstop). The text itself when it fits.
    /// </summary>
    public string Cut(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (HasCjk(text))
        {
            return FirstTextElements(text, Characters);
        }

        var words = 0;
        var inWord = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                if (++words > Words)
                {
                    text = text[..i].TrimEnd();
                    break;
                }
            }
        }

        return FirstTextElements(text, Words * MaxWordLength);
    }

    /// <summary>Whether <paramref name="text"/> has a CJK character, which makes it measured in characters.</summary>
    public static bool HasCjk(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsCjk(rune))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Words separated by white space.</summary>
    public static int WordCount(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static string FirstTextElements(string text, int max)
    {
        var info = new StringInfo(text);
        return info.LengthInTextElements <= max ? text : info.SubstringByTextElements(0, max);
    }

    // Han, kana, Hangul and bopomofo, with CJK punctuation and full-width forms: a name in 《》 or with a full-width
    // colon was written by a model answering in Chinese.
    private static bool IsCjk(Rune rune) =>
        rune.Value is (>= 0x1100 and <= 0x11FF) or (>= 0x2E80 and <= 0x2FDF) or (>= 0x3000 and <= 0x30FF) or (>= 0x3100 and <= 0x31FF)
            or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xA960 and <= 0xA97F) or (>= 0xAC00 and <= 0xD7FF)
            or (>= 0xF900 and <= 0xFAFF) or (>= 0xFF00 and <= 0xFFEF) or (>= 0x20000 and <= 0x323AF);
}
