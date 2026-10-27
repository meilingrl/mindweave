using System.Globalization;
using System.Text;

namespace Mindweave.Core.Summaries;

/// <summary>Pure rules for summaries: parsing generated output, naming, markers and version deletion.</summary>
public static class SummaryRules
{
    /// <summary>Progress past which a version counts as read (U2, MW-C30).</summary>
    public const double ReadThreshold = 0.9;

    /// <summary>Visible time a version needs in one opening before progress can mark it read, so a short body is not read on opening.</summary>
    public static readonly TimeSpan MinimumReadTime = TimeSpan.FromSeconds(8);

    private const int MaxTitleLength = 40;

    /// <summary>
    /// Splits generated output into title and body: the first non-empty line, with an optional "标题：" or "Title:"
    /// prefix and Markdown heading marks removed, is the title; the rest is the body. Null when either is empty.
    /// </summary>
    public static (string Title, string Body)? ParseGenerated(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var first = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (first < 0)
        {
            return null;
        }

        var title = lines[first].Trim().TrimStart('#').Trim();
        foreach (var prefix in new[] { "标题：", "标题:", "Title:", "title:" })
        {
            if (title.StartsWith(prefix, StringComparison.Ordinal))
            {
                title = title[prefix.Length..].Trim();
                break;
            }
        }

        title = title.Trim('*', '"', '“', '”', '《', '》').Trim();
        var body = string.Join('\n', lines[(first + 1)..]).Trim();
        if (title.Length == 0 || body.Length == 0)
        {
            return null;
        }

        return (title.Length > MaxTitleLength ? title[..MaxTitleLength] : title, body);
    }

    /// <summary>
    /// Name comparison key (§10.1, implementer's choice): NFKC (full/half width), surrounding whitespace and
    /// punctuation removed, inner whitespace collapsed, case-insensitive. Synonyms are not merged.
    /// </summary>
    public static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var text = name.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(Rune.ToLowerInvariant(rune));
        }

        var result = builder.ToString();
        var start = 0;
        var end = result.Length;
        while (start < end && (char.IsPunctuation(result[start]) || char.IsSymbol(result[start])))
        {
            start++;
        }

        while (end > start && (char.IsPunctuation(result[end - 1]) || char.IsSymbol(result[end - 1])))
        {
            end--;
        }

        return result[start..end].Trim();
    }

    /// <summary>The display name for a base title and an optional distinguishing term, e.g. "蒸汽机－原理".</summary>
    public static string DisplayName(string baseTitle, string? term) =>
        string.IsNullOrWhiteSpace(term) ? baseTitle : $"{baseTitle}－{term.Trim()}";

    /// <summary>
    /// Which version becomes current after deleting <paramref name="deleted"/> from the saved versions (MW-C17): the
    /// nearest remaining version created before the current one, otherwise the newest remaining one. Null when none
    /// remain, which deletes the node. Only matters when the current version is among those deleted.
    /// </summary>
    public static SummaryVersionId? NextCurrent(IReadOnlyList<SummaryVersion> saved, SummaryVersionId current, IReadOnlySet<SummaryVersionId> deleted)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(deleted);
        if (!deleted.Contains(current))
        {
            return current;
        }

        var currentSeq = saved.Single(v => v.Id == current).Seq;
        var remaining = saved.Where(v => !deleted.Contains(v.Id)).ToList();
        var earlier = remaining.Where(v => v.Seq < currentSeq).OrderByDescending(v => v.Seq).FirstOrDefault();
        return (earlier ?? remaining.OrderByDescending(v => v.Seq).FirstOrDefault())?.Id;
    }

    /// <summary>Markers for a node (MW-C07): unread by the current version's own record, candidate, later finished messages.</summary>
    public static SummaryMarkers Markers(SummaryNode node, SummaryVersion current, SummaryReadState? currentRead, int? lastFinishedSeq)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(current);
        return new(currentRead is not { IsRead: true }, node.Pending is not null, lastFinishedSeq is { } last && last > current.ToSeq);
    }
}
