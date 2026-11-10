namespace Mindweave.Core.Text;

/// <summary>
/// Removes quotes, book-title marks or Markdown emphasis that a model wraps around a whole name (title, short title, naming
/// term). A pair is removed only when it encloses the entire text, so marks that belong inside the name stay: trimming
/// each end on its own turned “《死寂》电影剧情概括” into “死寂》电影剧情概括” (2026-11-10).
/// </summary>
public static class EnclosingMarks
{
    private static readonly (string Open, string Close)[] Pairs =
        [("**", "**"), ("《", "》"), ("“", "”"), ("‘", "’"), ("「", "」"), ("『", "』"), ("\"", "\""), ("'", "'"), ("`", "`"), ("*", "*")];

    /// <summary>The text without the enclosing pairs around all of it (nested ones too), trimmed.</summary>
    public static string Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var current = text.Trim();
        string previous;
        do
        {
            previous = current;
            foreach (var (open, close) in Pairs)
            {
                if (Encloses(current, open, close))
                {
                    current = current[open.Length..^close.Length].Trim();
                    break;
                }
            }
        }
        while (current != previous);

        return current;
    }

    /// <summary>Whether <paramref name="open"/> at the start and <paramref name="close"/> at the end are one pair around all of <paramref name="text"/>.</summary>
    public static bool Encloses(string text, string open, string close)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(close);
        if (text.Length <= open.Length + close.Length || !text.StartsWith(open, StringComparison.Ordinal) || !text.EndsWith(close, StringComparison.Ordinal))
        {
            return false;
        }

        var inner = text[open.Length..^close.Length];
        if (open == close)
        {
            // Same mark on both sides ("Steam" engines): the inner text must not contain it, or the ends belong to different pairs.
            return !inner.Contains(open, StringComparison.Ordinal);
        }

        // Distinct marks (“火箭号”与“机车”): the opening mark must close only at the very end.
        var depth = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            if (string.CompareOrdinal(inner, i, open, 0, open.Length) == 0)
            {
                depth++;
            }
            else if (string.CompareOrdinal(inner, i, close, 0, close.Length) == 0 && --depth < 0)
            {
                return false;
            }
        }

        return depth == 0;
    }
}
