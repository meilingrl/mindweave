using Mindweave.Core.Context;

namespace Mindweave.Core.Resources;

/// <summary>
/// Cuts extracted text into chunks of about <see cref="TargetTokens"/> estimated tokens (architecture §15.2): at a paragraph
/// break when one is near, else at a sentence end, else (one very long sentence) where the size is reached. Boundaries depend
/// only on the text, so chunks, their keyword index and later summaries are reusable.
/// </summary>
public static class ResourceChunker
{
    public const int TargetTokens = 800;

    /// <summary>A paragraph break is preferred when the chunk up to it is at least this full.</summary>
    private const int ParagraphPreference = TargetTokens / 2;

    public static IReadOnlyList<ResourceChunk> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var chunks = new List<ResourceChunk>();
        if (text.Length == 0)
        {
            return chunks;
        }

        // Prefix counts: tokens of [a, b) are the non-ASCII runes plus a third of the ASCII characters, as TokenEstimator counts.
        var ascii = new int[text.Length + 1];
        var other = new int[text.Length + 1];
        for (var i = 0; i < text.Length; i++)
        {
            ascii[i + 1] = ascii[i] + (text[i] < 0x80 ? 1 : 0);
            other[i + 1] = other[i] + (text[i] >= 0x80 && !char.IsLowSurrogate(text[i]) ? 1 : 0);
        }

        int Tokens(int a, int b) => (other[b] - other[a]) + ((ascii[b] - ascii[a] + 2) / 3);

        var start = 0;
        while (start < text.Length)
        {
            if (Tokens(start, text.Length) <= TargetTokens)
            {
                chunks.Add(new ResourceChunk(chunks.Count, start, text.Length, Tokens(start, text.Length)));
                break;
            }

            var end = Boundary(text, start, Tokens);
            chunks.Add(new ResourceChunk(chunks.Count, start, end, Tokens(start, end)));
            start = end;
        }

        return chunks;
    }

    private static int Boundary(string text, int start, Func<int, int, int> tokens)
    {
        int lastParagraph = -1, lastSentence = -1, hard = -1;
        for (var i = start + 1; i <= text.Length; i++)
        {
            if (tokens(start, i) > TargetTokens)
            {
                break;
            }

            if (!(i < text.Length && char.IsLowSurrogate(text[i])))
            {
                hard = i;
            }

            var previous = text[i - 1];
            if (previous == '\n' && (i >= text.Length || text[i] != '\n'))
            {
                lastParagraph = i;
            }
            else if (IsSentenceEnd(previous) && (i >= text.Length || char.IsWhiteSpace(text[i]) || previous > 0x2E00))
            {
                lastSentence = i;
            }
        }

        if (lastParagraph > start && tokens(start, lastParagraph) >= ParagraphPreference)
        {
            return lastParagraph;
        }

        if (lastSentence > start)
        {
            return Math.Max(lastSentence, lastParagraph);
        }

        return lastParagraph > start ? lastParagraph : Math.Max(hard, start + 1);
    }

    private static bool IsSentenceEnd(char c) => c is '.' or '!' or '?' or '。' or '！' or '？' or '；' or ';' or '…';
}
