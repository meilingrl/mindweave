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

    /// <summary>Longest network label kept from generated output, and the cut for falling back to the title.</summary>
    public const int MaxShortTitleLength = 8;

    /// <summary>
    /// Splits generated output into title, optional short title and body: the first non-empty line, with an optional
    /// "标题：" or "Title:" prefix and Markdown heading marks removed, is the title; a following "短名：" line, if any,
    /// is the network label; the rest is the body. Null when title or body is empty.
    /// </summary>
    public static GeneratedSummary? ParseGenerated(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var first = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (first < 0)
        {
            return null;
        }

        var title = Clean(StripPrefix(lines[first].Trim().TrimStart('#').Trim(), "标题：", "标题:", "Title:", "title:"));
        var rest = first + 1;
        string? shortTitle = null;
        var next = Array.FindIndex(lines, rest, l => l.Trim().Length > 0);
        if (next >= 0 && StripPrefix(lines[next].Trim(), "短名：", "短名:", "Short:", "short:") is var candidate && candidate != lines[next].Trim())
        {
            shortTitle = Clean(candidate) is { Length: > 0 } s ? (s.Length > MaxShortTitleLength ? s[..MaxShortTitleLength] : s) : null;
            rest = next + 1;
        }

        var body = string.Join('\n', lines[rest..]).Trim();
        if (title.Length == 0 || body.Length == 0)
        {
            return null;
        }

        return new GeneratedSummary(title.Length > MaxTitleLength ? title[..MaxTitleLength] : title, shortTitle, body);

        static string StripPrefix(string line, params string[] prefixes)
        {
            foreach (var prefix in prefixes)
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return line[prefix.Length..].Trim();
                }
            }

            return line;
        }

        static string Clean(string text) => text.Trim('*', '"', '“', '”', '《', '》').Trim();
    }

    /// <summary>
    /// The label a network node shows (S4 trial feedback): the current version's short title, or the start of its
    /// title when the version has none (older versions, or output without a "短名" line).
    /// </summary>
    public static string ShortTitleOf(SummaryVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return ShortTitleOf(version.Title, version.ShortTitle);
    }

    /// <summary>See <see cref="ShortTitleOf(SummaryVersion)"/>; from the two fields it uses, so a body is never needed.</summary>
    public static string ShortTitleOf(string title, string? shortTitle)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (shortTitle is { Length: > 0 })
        {
            return shortTitle;
        }

        var runes = title.EnumerateRunes().Take(MaxShortTitleLength + 1).ToList();
        return runes.Count > MaxShortTitleLength ? string.Concat(runes.Take(MaxShortTitleLength)) + "…" : title;
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
        ArgumentNullException.ThrowIfNull(current);
        return Markers(node, current.ToSeq, currentRead, lastFinishedSeq);
    }

    /// <summary>See the overload taking the version; from the current version's coverage end alone.</summary>
    public static SummaryMarkers Markers(SummaryNode node, int currentToSeq, SummaryReadState? currentRead, int? lastFinishedSeq)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new(currentRead is not { IsRead: true }, node.Pending is not null, lastFinishedSeq is { } last && last > currentToSeq);
    }
}
