using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mindweave.Application.Ai;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Text;

namespace Mindweave.Application.Recommendations;

/// <summary>What one request asks for: <paramref name="Count"/> questions, or nodes (each extending a summary).</summary>
public sealed record RecommendationAsk(RecommendationKind Kind, int Count);

/// <summary>A summary as sent: its label in the prompt ("S1"), the node it stands for, and what is sent of it.</summary>
public sealed record RecommendationSource(string Label, SummaryNodeId Node, string Name, string Title, string Excerpt);

/// <summary>
/// What is sent (requirements §10.3 共同规则: titles and short excerpts only): conversation titles, archived ones included;
/// summaries with their current version's title and a short excerpt; and questions not to offer again. The counts say
/// how many the workspace has, so the model knows the lists are cut.
/// </summary>
public sealed record RecommendationInput(
    IReadOnlyList<string> Conversations,
    int ConversationCount,
    IReadOnlyList<RecommendationSource> Summaries,
    int SummaryCount,
    IReadOnlyList<string> Avoid)
{
    /// <summary>Nothing to draw on: no titled conversation and no summary. Then nothing is recommended or sent.</summary>
    public bool IsEmpty => Conversations.Count == 0 && Summaries.Count == 0;
}

/// <summary>
/// The recommendation prompt, its bounded input and the cleaning of what comes back (pure; tested directly). Shared by
/// recommended questions (§10.3.1) and recommended nodes (§10.3.2): the input and output format are the same; a node
/// must name the summary it extends.
/// </summary>
public static partial class RecommendationPrompt
{
    /// <summary>At most this many titled conversations are sent, together at most <see cref="ConversationChars"/> characters.</summary>
    public const int MaxConversations = 30;

    public const int ConversationChars = 900;

    /// <summary>Each conversation title is cut to this many characters.</summary>
    public const int TitleLength = 30;

    /// <summary>At most this many summaries are sent, the most recently updated (decided 2026-11-12, design note).</summary>
    public const int MaxSummaries = 12;

    /// <summary>Each summary's excerpt: the start of its current version's body, at most this many characters.</summary>
    public const int ExcerptLength = 80;

    /// <summary>How much of a body storage reads to make an excerpt from (Markdown marks are removed before cutting).</summary>
    public const int BodyChars = 300;

    /// <summary>At most this many used, ignored or shown questions are listed as not to be offered again, newest first.</summary>
    public const int MaxAvoid = 30;

    /// <summary>A question longer than this (in characters) is an explanation, not a question, and is dropped.</summary>
    public const int MaxQuestionLength = 60;

    /// <summary>A question shorter than this is dropped.</summary>
    public const int MinQuestionLength = 4;

    /// <summary>A longer reason is dropped (the question stays).</summary>
    public const int MaxReasonLength = 40;

    /// <summary>Output cap: reasoning models count their hidden reasoning against it (see ConversationTitler).</summary>
    public const int OutputTokens = 1536;

    /// <summary>The phrase every recommendation prompt starts its task with (tests tell the request apart by it).</summary>
    public const string Marker = "值得接着探索的新问题";

    public static RecommendationInput SelectInput(
        IReadOnlyList<ConversationSummary> conversations,
        IReadOnlyList<RecommendationBasis> summaries,
        int summaryCount,
        IEnumerable<string> avoid)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(summaries);
        ArgumentNullException.ThrowIfNull(avoid);

        // Titles only (an untitled conversation's first question is content, not a title): visible roots first, then
        // branches, then archived ones; the store lists the most recently active first, which the stable sort keeps.
        var titled = conversations.Where(c => c.Conversation.Title is not null).ToList();
        var titles = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var used = 0;
        foreach (var title in titled.OrderBy(c => c.IsHidden).ThenBy(c => c.Parent is not null).Select(c => c.Conversation.Title!))
        {
            var item = ConversationRules.Shorten(title, TitleLength);
            if (!seen.Add(RecommendationText.Normalize(item)))
            {
                continue;
            }

            if (titles.Count == MaxConversations || used + item.Length > ConversationChars)
            {
                break;
            }

            titles.Add(item);
            used += item.Length;
        }

        var sources = summaries.Take(MaxSummaries)
            .Select((s, i) => new RecommendationSource($"S{i + 1}", s.Node, ConversationRules.Shorten(s.DisplayName, TitleLength), ConversationRules.Shorten(s.Title, TitleLength), Excerpt(s.BodyStart, s.Title)))
            .ToList();
        var avoided = avoid.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => ConversationRules.Shorten(a, MaxQuestionLength))
            .Distinct(StringComparer.Ordinal).Take(MaxAvoid).ToList();
        return new RecommendationInput(titles, titled.Count, sources, Math.Max(summaryCount, sources.Count), avoided);
    }

    /// <summary>
    /// The start of a body as plain text: headings, emphasis, list and quote marks, links and line breaks removed; a first
    /// line that only repeats the title is skipped; at most <see cref="ExcerptLength"/> characters.
    /// </summary>
    public static string Excerpt(string body, string title)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(title);
        var lines = new List<string>();
        foreach (var raw in body.Split('\n'))
        {
            var line = MarkdownLead().Replace(raw.Trim(), string.Empty);
            line = Link().Replace(line, "$1");
            line = line.Replace("**", string.Empty, StringComparison.Ordinal).Replace("__", string.Empty, StringComparison.Ordinal)
                .Replace("`", string.Empty, StringComparison.Ordinal).Replace("$", string.Empty, StringComparison.Ordinal).Trim();
            if (line.Length == 0 || (lines.Count == 0 && RecommendationText.Normalize(line) == RecommendationText.Normalize(title)))
            {
                continue;
            }

            lines.Add(line);
        }

        return ConversationRules.Shorten(string.Join(' ', lines), ExcerptLength);
    }

    public static string SystemPrompt(RecommendationAsk ask)
    {
        ArgumentNullException.ThrowIfNull(ask);
        var c = CultureInfo.InvariantCulture;
        var basis = ask.Kind == RecommendationKind.Node
            ? "每个问题必须延伸自上面列出的某一个总结，basis 写出它的编号（如 S2），不能留空。"
            : "若问题主要延伸自某个总结，basis 写出它的编号（如 S2），否则留空。";
        return "下面是一个学习工作区里已有的会话标题，以及总结的标题和简短摘录。请推荐 " + ask.Count.ToString(c) + " 个" + Marker +
            "：从这些内容出发，偏向相邻但还没有问过的方向；不要重复已有会话的问题，也不要与“不再推荐”里的问题相同或只是换个说法。" +
            "要求：每个问题是一句完整、具体的提问，不超过 30 个字；附一句不超过 20 个字的推荐理由；" + basis +
            "使用这些标题主要使用的语言。只输出 JSON 数组，不加其他文字，格式：[{\"question\":\"…\",\"reason\":\"…\",\"basis\":\"S2\"}]";
    }

    public static ChatRequest Request(Core.Models.ModelSnapshot model, RecommendationInput input, RecommendationAsk ask)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(input);
        return new ChatRequest(model with { MaxOutputTokens = OutputTokens },
        [
            new ChatTurn(ChatRole.System, SystemPrompt(ask)),
            new ChatTurn(ChatRole.User, UserText(input)),
        ]);
    }

    public static string UserText(RecommendationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var c = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        if (input.Conversations.Count > 0)
        {
            text.Append(c, $"会话标题（共 {input.ConversationCount} 个，列出 {input.Conversations.Count} 个）：\n");
            foreach (var title in input.Conversations)
            {
                text.Append("- ").Append(title).Append('\n');
            }
        }

        if (input.Summaries.Count > 0)
        {
            text.Append(c, $"\n总结（共 {input.SummaryCount} 个，列出 {input.Summaries.Count} 个）：\n");
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
        }

        if (input.Avoid.Count > 0)
        {
            text.Append("\n不再推荐：\n");
            foreach (var question in input.Avoid)
            {
                text.Append("- ").Append(question).Append('\n');
            }
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// The usable items in the output, in its order, at most <paramref name="ask"/>'s count: a JSON array (possibly in a
    /// code fence or after a lead-in), or else one item per line ("问题 | 理由 | S2"). A question is one line of a sensible
    /// length; a reason too long is dropped; a basis label not in <paramref name="input"/> is none, and for a node an item
    /// without a basis is dropped. Repeats of <paramref name="avoid"/> (normalized) and of each other are dropped.
    /// </summary>
    public static IReadOnlyList<NewRecommendation> Parse(string output, RecommendationInput input, RecommendationAsk ask, IReadOnlySet<string> avoid)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(avoid);
        var labels = input.Summaries.ToDictionary(s => s.Label, s => s.Node, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(avoid, StringComparer.Ordinal);
        var items = new List<NewRecommendation>();
        foreach (var (rawQuestion, rawReason, rawBasis) in FromJson(output) ?? FromJsonFields(output) ?? FromLines(output))
        {
            var question = CleanQuestion(rawQuestion);
            if (question is null || !seen.Add(RecommendationText.Normalize(question)))
            {
                continue;
            }

            SummaryNodeId? basis = rawBasis is { } b && BasisLabel().Match(b) is { Success: true } m && labels.TryGetValue("S" + m.Groups[1].Value, out var node) ? node : null;
            if (ask.Kind == RecommendationKind.Node && basis is null)
            {
                continue;
            }

            var reason = rawReason is null ? null : EnclosingMarks.Strip(ConversationRules.Shorten(ReasonLead().Replace(rawReason.Trim(), string.Empty), MaxReasonLength + 1));
            if (reason is { Length: 0 } || (reason is not null && new StringInfo(reason).LengthInTextElements > MaxReasonLength))
            {
                reason = null;
            }

            items.Add(new NewRecommendation(question, reason, basis));
            if (items.Count == ask.Count)
            {
                break;
            }
        }

        return items;
    }

    private static string? CleanQuestion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains('\n', StringComparison.Ordinal))
        {
            return null;
        }

        var question = EnclosingMarks.Strip(ListMarker().Replace(raw.Trim(), string.Empty)).Trim();
        var length = new StringInfo(question).LengthInTextElements;
        return length < MinQuestionLength || length > MaxQuestionLength ? null : question;
    }

    private static List<(string? Question, string? Reason, string? Basis)>? FromJson(string output)
    {
        var start = output.IndexOf('[', StringComparison.Ordinal);
        var end = output.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(output[start..(end + 1)], new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var list = new List<(string?, string?, string?)>();
            foreach (var item in json.RootElement.EnumerateArray())
            {
                switch (item.ValueKind)
                {
                    case JsonValueKind.String:
                        list.Add((item.GetString(), null, null));
                        break;
                    case JsonValueKind.Object:
                        list.Add((Field(item, "question", "q", "问题"), Field(item, "reason", "理由"), Field(item, "basis", "依据", "source")));
                        break;
                }
            }

            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Field(JsonElement item, params string[] names)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => "S" + property.Value.GetRawText(),
                    _ => null,
                };
            }
        }

        return null;
    }

    /// <summary>
    /// JSON that does not parse (review 2026-11-13: most often ASCII quotes left unescaped inside a Chinese question) is
    /// read by its fields: in each object, a known key's value runs up to the next known key or the object's end, its
    /// enclosing quotes and trailing comma removed. Null when the output has no such field.
    /// </summary>
    private static List<(string? Question, string? Reason, string? Basis)>? FromJsonFields(string output)
    {
        if (!JsonKey().IsMatch(output))
        {
            return null;
        }

        var list = new List<(string?, string?, string?)>();
        foreach (Match block in JsonObject().Matches(output))
        {
            var body = block.Groups[1].Value;
            var keys = JsonKey().Matches(body);
            string? question = null, reason = null, basis = null;
            for (var i = 0; i < keys.Count; i++)
            {
                var start = keys[i].Index + keys[i].Length;
                var end = i + 1 < keys.Count ? keys[i + 1].Index : body.Length;
                var value = body[start..end].Trim().TrimEnd(',').Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                {
                    value = value[1..^1];
                }

                switch (keys[i].Groups[1].Value.ToUpperInvariant())
                {
                    case "QUESTION" or "Q" or "问题":
                        question ??= value;
                        break;
                    case "REASON" or "理由":
                        reason ??= value;
                        break;
                    default:
                        basis ??= value.Length > 0 && value.All(char.IsAsciiDigit) ? "S" + value : value;
                        break;
                }
            }

            if (question is not null)
            {
                list.Add((question, reason, basis));
            }
        }

        return list;
    }

    private static IEnumerable<(string? Question, string? Reason, string? Basis)> FromLines(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal) || line.EndsWith(':') || line.EndsWith('：') || Preamble().IsMatch(line)
                || line[0] is '{' or '}' or '[' or ']' || JsonKey().IsMatch(line))
            {
                continue;
            }

            var parts = line.Split(['|', '｜'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            string? reason = null;
            string? basis = null;
            foreach (var part in parts.Skip(1))
            {
                if (BasisLabel().IsMatch(part))
                {
                    basis = part;
                }
                else
                {
                    reason ??= part;
                }
            }

            yield return (parts[0], reason, basis);
        }
    }

    /// <summary>A JSON field name the answer is asked for (or a near variant), quoted, with its colon.</summary>
    [GeneratedRegex("\"(question|q|问题|reason|理由|basis|依据|source)\"\\s*:\\s*", RegexOptions.IgnoreCase)]
    private static partial Regex JsonKey();

    /// <summary>One JSON object's body; the last one may lack its closing brace (cut-off output).</summary>
    [GeneratedRegex(@"\{([^{}]*)(?:\}|$)")]
    private static partial Regex JsonObject();

    /// <summary>"S2", "延伸自：S2", "[S2]" as a basis label.</summary>
    [GeneratedRegex(@"^\s*(?:延伸自|依据|basis)?\s*[:：]?\s*\[?\s*[Ss]\s*(\d{1,2})\s*\]?\s*$")]
    private static partial Regex BasisLabel();

    /// <summary>"理由：" before a reason.</summary>
    [GeneratedRegex(@"^(?:推荐理由|理由|reason)\s*[:：]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ReasonLead();

    /// <summary>"1. ", "2、", "(3)", "- ", "• " before a question.</summary>
    [GeneratedRegex(@"^(?:[-*+]\s+|[•·]\s*|\d{1,2}\s*[.、)）:：]\s*|[（(]\d{1,2}[)）]\s*)")]
    private static partial Regex ListMarker();

    /// <summary>A chatty model's lead-in ("以下是推荐的问题", "Here are three questions").</summary>
    [GeneratedRegex(@"^(?:以下|下面|这里|好的|当然|推荐问题|Here\b|Sure\b|Questions?\b)", RegexOptions.IgnoreCase)]
    private static partial Regex Preamble();

    /// <summary>Heading, quote and list marks at the start of a Markdown line.</summary>
    [GeneratedRegex(@"^(?:#{1,6}\s*|>\s*|[-*+]\s+|\d{1,3}[.)]\s+)+")]
    private static partial Regex MarkdownLead();

    /// <summary>A Markdown link, its text kept.</summary>
    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();
}
