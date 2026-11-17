using System.Globalization;
using System.Text;
using System.Text.Json;
using Mindweave.Application.Storage;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;

namespace Mindweave.Application.Tools;

/// <summary>
/// 检索总结网络 (requirements §10.4.4, architecture §15.6 <c>search_summaries</c>): keyword search over the current versions of
/// the workspace's saved summaries (archived conversations' included), titles weighing more than bodies. Scored in memory
/// with the attachments' search terms (decision R3: a workspace holds tens to hundreds of summaries; no index to keep in step).
/// </summary>
public sealed class SearchSummariesTool(ISummaryStore summaries) : ITool
{
    public const string ToolName = "search_summaries";

    public const int MaxHits = 8;

    public const int ExcerptLength = 200;

    /// <summary>Characters of a body that are scored (a summary is far shorter).</summary>
    private const int MaxBody = 20_000;

    public string Name => ToolName;

    public string DisplayName => "检索总结网络";

    public string Description =>
        "在本工作区已保存的知识总结（用户以前探索过的内容）中按关键词检索，返回总结标题、编号与相关片段。" +
        "回答可以说明依据了哪些总结。";

    public string ParametersSchema => """
        {"type":"object","properties":{
          "query":{"type":"string","description":"检索词，可以是几个关键词"}
        },"required":["query"],"additionalProperties":false}
        """;

    public ToolRisk Risk => ToolRisk.LocalRead;

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<bool> IsAvailableAsync(ToolScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return (await summaries.ListAsync(scope.Workspace, cancellationToken).ConfigureAwait(false)).Count > 0;
    }

    public string Describe(JsonElement arguments)
    {
        try
        {
            return ToolArguments.Text(arguments, "query") ?? string.Empty;
        }
        catch (ToolInputException)
        {
            return string.Empty;
        }
    }

    public async Task<ToolOutput> ExecuteAsync(ToolScope scope, JsonElement arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var query = ToolArguments.Text(arguments, "query") ?? throw new ToolInputException("请给出检索词 query。");
        var terms = ToolArguments.Terms(query);
        if (terms.Count == 0)
        {
            return Nothing(query);
        }

        var entries = await summaries.ListAsync(scope.Workspace, cancellationToken).ConfigureAwait(false);
        var bodies = await summaries.ExcerptsAsync([.. entries.Select(e => e.Current.Id)], MaxBody, cancellationToken).ConfigureAwait(false);

        // Each term counts once per place it appears, weighted by how rare it is across the summaries; title matches weigh triple.
        var documents = entries.Select(e => (Entry: e, Title: e.Current.Title, Body: bodies.GetValueOrDefault(e.Current.Id) ?? string.Empty)).ToList();
        var frequency = terms.ToDictionary(t => t, t => documents.Count(d => Has(d.Title, t) || Has(d.Body, t)), StringComparer.Ordinal);
        var scored = documents
            .Select(d => (d.Entry, d.Title, d.Body, Score: terms.Sum(t =>
            {
                var weight = Math.Log(1 + ((double)documents.Count / Math.Max(1, frequency[t])));
                return weight * ((Has(d.Title, t) ? 3 : 0) + Math.Min(3, Count(d.Body, t)));
            })))
            .Where(d => d.Score > 0)
            .OrderByDescending(d => d.Score)
            .ThenByDescending(d => d.Entry.Node.UpdatedAt)
            .Take(MaxHits)
            .ToList();
        if (scored.Count == 0)
        {
            return Nothing(query);
        }

        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"检索“{query}”找到 {scored.Count} 个总结：\n");
        var findings = new List<ToolFinding>();
        for (var i = 0; i < scored.Count; i++)
        {
            var (entry, _, body, _) = scored[i];
            text.Append(CultureInfo.InvariantCulture, $"\n[{i + 1}] {entry.Node.DisplayName}（总结 {entry.Node.Id.Value:D}）\n")
                .Append(ToolArguments.Snippet(body, terms, ExcerptLength))
                .Append('\n');
            findings.Add(new ToolFinding(ToolFindingKind.Summary, entry.Node.Id.Value.ToString("D"), entry.Node.DisplayName));
        }

        return new ToolOutput(text.ToString().TrimEnd(), $"找到 {scored.Count} 个总结", ResourceProvenance.Workspace, findings);
    }

    private static ToolOutput Nothing(string query) => new($"检索“{query}”没有找到相关的总结。", "没有找到", ResourceProvenance.Workspace, []);

    private static bool Has(string text, string term) => text.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static int Count(string text, string term)
    {
        var count = 0;
        for (var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(term, at + term.Length, StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }
}
