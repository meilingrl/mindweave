namespace Mindweave.Core.Conversations;

/// <summary>One [n] in an answer's text: the number, where the marker starts and its length (brackets included).</summary>
public readonly record struct CitationMarker(int Number, int Index, int Length);

/// <summary>
/// Finds the 出处 markers an answer writes (requirements §10.4.5): <c>[n]</c> with n of one to three digits, not part of a
/// Markdown link (<c>[1](…)</c>, <c>[1][ref]</c> as a reference) or a reference definition (<c>[1]: …</c>), and not inside code
/// (<c>`…`</c> spans or fenced blocks). Which of them point to a real source is decided by the answer's citation blocks.
/// </summary>
public static class CitationMarkers
{
    public static IReadOnlyList<CitationMarker> Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = new List<CitationMarker>();
        var inFence = false;
        var lineStart = 0;
        while (lineStart <= text.Length)
        {
            var lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            var trimmed = text.AsSpan(lineStart, lineEnd - lineStart).TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
            else if (!inFence)
            {
                ScanLine(text, lineStart, lineEnd, found);
            }

            lineStart = lineEnd + 1;
        }

        return found;
    }

    /// <summary>The sentence a marker stands in (for locating the cited passage): back to the previous sentence end, up to the marker.</summary>
    public static string SentenceBefore(string text, CitationMarker marker)
    {
        ArgumentNullException.ThrowIfNull(text);
        var end = marker.Index;
        var start = end;
        while (start > 0 && !IsSentenceEnd(text[start - 1]))
        {
            start--;
        }

        // A marker right after the sentence's own end ("…。[1]") cites that sentence.
        if (start == end && start > 0)
        {
            start--;
            while (start > 0 && !IsSentenceEnd(text[start - 1]))
            {
                start--;
            }
        }

        return text[start..end].Trim();
    }

    private static bool IsSentenceEnd(char c) => c is '。' or '！' or '？' or '\n' or '!' or '?' or ';' or '；';

    private static void ScanLine(string text, int start, int end, List<CitationMarker> found)
    {
        var inCode = false;
        for (var i = start; i < end; i++)
        {
            var c = text[i];
            if (c == '`')
            {
                inCode = !inCode;
                continue;
            }

            if (inCode || c != '[' || (i > start && text[i - 1] == '\\'))
            {
                continue;
            }

            var j = i + 1;
            while (j < end && j - i <= 3 && char.IsAsciiDigit(text[j]))
            {
                j++;
            }

            var digits = j - i - 1;
            if (digits is < 1 or > 3 || j >= end || text[j] != ']')
            {
                continue;
            }

            var after = j + 1 < end ? text[j + 1] : '\0';
            if (after is '(' or ':' || (after == '[' && !IsMarkerAt(text, j + 1, end)))
            {
                continue;
            }

            if (i > start && text[i - 1] == ']' && !(found.Count > 0 && found[^1].Index + found[^1].Length == i))
            {
                // "[text][1]" is a reference-style link, unless the previous one was itself a marker.
                continue;
            }

            var number = int.Parse(text.AsSpan(i + 1, digits), System.Globalization.CultureInfo.InvariantCulture);
            if (number > 0)
            {
                found.Add(new CitationMarker(number, i, j + 1 - i));
            }

            i = j;
        }
    }

    private static bool IsMarkerAt(string text, int i, int end)
    {
        var j = i + 1;
        while (j < end && j - i <= 3 && char.IsAsciiDigit(text[j]))
        {
            j++;
        }

        return j - i - 1 is >= 1 and <= 3 && j < end && text[j] == ']';
    }
}
