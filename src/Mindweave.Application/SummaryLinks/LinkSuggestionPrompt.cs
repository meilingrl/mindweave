using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mindweave.Application.Ai;
using Mindweave.Application.Recommendations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Text;

namespace Mindweave.Application.SummaryLinks;

/// <summary>
/// What a 建议关联 request sends (requirements §10.3 共同规则 and §10.3.4: summary titles and short excerpts of their current
/// versions only, archived conversations' summaries included): the listed summaries, each with a label ("S1") the answer
/// refers to them by, how many the workspace has, and the listed pairs not to suggest (linked, ignored, or whose link the
/// user deleted).
/// </summary>
public sealed record LinkSuggestionInput(IReadOnlyList<RecommendationSource> Summaries, int SummaryCount, IReadOnlyList<(string A, string B)> Avoid);

/// <summary>
/// The link-suggestion prompt, its bounded input and the strict reading of what comes back (pure; tested directly). The
/// summaries and their excerpts are made the same way as for recommendations (<see cref="RecommendationPrompt"/>), and the
/// answer is read with the same forgiving JSON reading (<see cref="LooseJson"/>), then every pair is checked.
/// </summary>
public static partial class LinkSuggestionPrompt
{
    /// <summary>A request needs at least this many summaries (requirements §10.3.4).</summary>
    public const int MinSummaries = 3;

    /// <summary>At most this many pairs are suggested by one request.</summary>
    public const int MaxPairs = 5;

    /// <summary>
    /// At most this many summaries are sent, the most recently updated (a pair needs both ends in view, so more than the 12
    /// a recommendation draws on; 30 short entries stay a small request, design note 2026-11-16).
    /// </summary>
    public const int MaxSummaries = 30;

    /// <summary>At most this many pairs not to suggest are listed (only pairs of listed summaries mean anything to the model).</summary>
    public const int MaxAvoid = 40;

    /// <summary>A longer reason is shortened (with "…"): the reason is what the user judges a suggestion by, so it is kept.</summary>
    public const int MaxReasonLength = 60;

    /// <summary>Output cap: reasoning models count their hidden reasoning against it (see ConversationTitler).</summary>
    public const int OutputTokens = 2048;

    /// <summary>The phrase every link-suggestion prompt starts its task with (tests tell the request apart by it).</summary>
    public const string Marker = "可能相关的总结对";

    public static LinkSuggestionInput SelectInput(IReadOnlyList<RecommendationBasis> summaries, int summaryCount, IEnumerable<(SummaryNodeId A, SummaryNodeId B)> avoid)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        ArgumentNullException.ThrowIfNull(avoid);
        var sources = summaries.Take(MaxSummaries)
            .Select((s, i) => new RecommendationSource(
                $"S{i + 1}",
                s.Node,
                ConversationRules.Shorten(s.DisplayName, RecommendationPrompt.TitleLength),
                ConversationRules.Shorten(s.Title, RecommendationPrompt.TitleLength),
                RecommendationPrompt.Excerpt(s.BodyStart, s.Title)))
            .ToList();
        var labels = sources.ToDictionary(s => s.Node, s => s.Label);
        var avoided = avoid
            .Where(p => labels.ContainsKey(p.A) && labels.ContainsKey(p.B))
            .Select(p => (labels[p.A], labels[p.B]))
            .Distinct()
            .Take(MaxAvoid)
            .ToList();
        return new LinkSuggestionInput(sources, Math.Max(summaryCount, sources.Count), avoided);
    }

    public static string SystemPrompt()
    {
        var c = CultureInfo.InvariantCulture;
        return "下面是一个学习工作区里的总结，每个有编号、标题和简短摘录。请从中找出最多 " + MaxPairs.ToString(c) + " 对" + Marker +
            "：两个总结的内容有值得互相参照的联系（如对比、因果、例子、前提、同一主题的不同侧面）。" +
            "要求：a 和 b 写两个不同总结的编号（如 S1、S3）；不要提出“不要再建议”里的总结对；没有把握的不要硬凑，可以少于 " + MaxPairs.ToString(c) + " 对，没有就输出 []；" +
            "note 是不超过 " + SummaryLinkNote.MaxLength.ToString(c) + " 个字的简短说明（如“对比”“原因”“例子”）；reason 用一句话说明为什么相关，不超过 40 个字。" +
            "使用这些标题主要使用的语言。只输出 JSON 数组，不加其他文字，格式：[{\"a\":\"S1\",\"b\":\"S3\",\"note\":\"对比\",\"reason\":\"…\"}]";
    }

    public static ChatRequest Request(Core.Models.ModelSnapshot model, LinkSuggestionInput input)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(input);
        return new ChatRequest(model with { MaxOutputTokens = OutputTokens },
        [
            new ChatTurn(ChatRole.System, SystemPrompt()),
            new ChatTurn(ChatRole.User, UserText(input)),
        ]);
    }

    public static string UserText(LinkSuggestionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var c = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.Append(c, $"总结（共 {input.SummaryCount} 个，列出 {input.Summaries.Count} 个）：\n");
        foreach (var s in input.Summaries)
        {
            text.Append(c, $"[{s.Label}] {s.Name}");
            if (RecommendationText.Normalize(s.Title) != RecommendationText.Normalize(s.Name))
            {
                text.Append('：').Append(s.Title);
            }

            text.Append('\n');
            if (s.Excerpt.Length > 0)
            {
                text.Append("摘录：").Append(s.Excerpt).Append('\n');
            }
        }

        if (input.Avoid.Count > 0)
        {
            text.Append("\n不要再建议（已经关联，或用户忽略过）：\n");
            foreach (var (a, b) in input.Avoid)
            {
                text.Append("- ").Append(a).Append(" 与 ").Append(b).Append('\n');
            }
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// The valid pairs in the output, in its order, at most <see cref="MaxPairs"/>: a JSON array (possibly in a code fence or
    /// after a lead-in), JSON read by its fields when it does not parse, or else one pair per line ("S1 | S3 | 对比 | 理由").
    /// A pair is kept only when both labels name listed summaries, they differ, the pair (in either order) is not in
    /// <paramref name="avoid"/> and not repeated. A note is trimmed and dropped (the pair kept) when longer than 16
    /// characters; a reason is trimmed and shortened to <see cref="MaxReasonLength"/>.
    /// </summary>
    public static IReadOnlyList<NewLinkSuggestion> Parse(string output, LinkSuggestionInput input, IEnumerable<(SummaryNodeId A, SummaryNodeId B)> avoid)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(avoid);
        var labels = input.Summaries.ToDictionary(s => s.Label, s => s.Node, StringComparer.OrdinalIgnoreCase);
        var seen = avoid.Select(p => SummaryLinkPair.Normalize(p.A, p.B)).ToHashSet();
        var items = new List<NewLinkSuggestion>();
        foreach (var (rawA, rawB, rawNote, rawReason) in FromJson(output) ?? FromJsonFields(output) ?? FromLines(output))
        {
            if (NodeOf(rawA, labels) is not { } first || NodeOf(rawB, labels) is not { } second || first == second)
            {
                continue;
            }

            var pair = SummaryLinkPair.Normalize(first, second);
            if (!seen.Add(pair))
            {
                continue;
            }

            items.Add(new NewLinkSuggestion(pair.A, pair.B, CleanNote(rawNote), CleanReason(rawReason)));
            if (items.Count == MaxPairs)
            {
                break;
            }
        }

        return items;
    }

    /// <summary>The note as stored: one line, trimmed, without enclosing quotes; null when empty or longer than 16 characters.</summary>
    public static string? CleanNote(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var note = SummaryLinkNote.Normalize(EnclosingMarks.Strip(ConversationRules.Shorten(raw, 200)).Trim());
        return note is null || SummaryLinkNote.IsTooLong(note) ? null : note;
    }

    private static string? CleanReason(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var reason = EnclosingMarks.Strip(ReasonLead().Replace(ConversationRules.Shorten(raw, 1000), string.Empty)).Trim();
        return reason.Length == 0 ? null : ConversationRules.Shorten(reason, MaxReasonLength);
    }

    private static SummaryNodeId? NodeOf(string? raw, Dictionary<string, SummaryNodeId> labels) =>
        raw is not null && Label().Match(raw) is { Success: true } m && labels.TryGetValue("S" + int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), out var node)
            ? node
            : null;

    /// <summary>A field name the answer is asked for (or a near variant) as the field it fills; null for any other.</summary>
    private static string? Canonical(string name) => name.ToUpperInvariant() switch
    {
        "A" or "FROM" or "FIRST" or "SOURCE" => "a",
        "B" or "TO" or "SECOND" or "TARGET" => "b",
        "NOTE" or "LABEL" or "说明" => "note",
        "REASON" or "WHY" or "理由" => "reason",
        _ => null,
    };

    private static List<(string? A, string? B, string? Note, string? Reason)>? FromJson(string output) =>
        LooseJson.FromArray(output, Canonical)?.Select(Item).ToList();

    private static List<(string? A, string? B, string? Note, string? Reason)>? FromJsonFields(string output) =>
        LooseJson.FromFields(output, JsonKey(), Canonical)?.Select(Item).ToList();

    private static (string? A, string? B, string? Note, string? Reason) Item(Dictionary<string, string?> fields) =>
        (fields.GetValueOrDefault("a"), fields.GetValueOrDefault("b"), fields.GetValueOrDefault("note"), fields.GetValueOrDefault("reason"));

    private static IEnumerable<(string? A, string? B, string? Note, string? Reason)> FromLines(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            if (LinePair().Match(raw.Trim()) is not { Success: true } m)
            {
                continue;
            }

            var rest = m.Groups[3].Value.Split(['|', '｜'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            yield return ("S" + m.Groups[1].Value, "S" + m.Groups[2].Value, rest.ElementAtOrDefault(0), rest.ElementAtOrDefault(1));
        }
    }

    /// <summary>"S2", "s2", "[S2]" or "2" as a summary's label.</summary>
    [GeneratedRegex(@"^\s*\[?\s*[Ss]?\s*(\d{1,3})\s*\]?\s*$")]
    private static partial Regex Label();

    /// <summary>A JSON field name the answer is asked for (or a near variant), quoted, with its colon.</summary>
    [GeneratedRegex("\"(a|b|from|to|first|second|source|target|note|label|说明|reason|why|理由)\"\\s*:\\s*", RegexOptions.IgnoreCase)]
    private static partial Regex JsonKey();

    /// <summary>A line naming a pair first: "1. S1 | S3 | 对比 | 理由", "S1 与 S3：…", "[S2]-[S5] …".</summary>
    [GeneratedRegex(@"^(?:[-*+•·]\s*|\d{1,2}\s*[.、)）]\s*)?\[?[Ss](\d{1,3})\]?\s*(?:[|｜/、,，~–—-]|与|和|↔|\s)+\s*\[?[Ss](\d{1,3})\]?\s*[|｜:：]?\s*(.*)$")]
    private static partial Regex LinePair();

    /// <summary>"理由：" before a reason.</summary>
    [GeneratedRegex(@"^(?:关联理由|理由|reason)\s*[:：]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ReasonLead();
}
