using System.Globalization;
using System.Text;
using System.Text.Json;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;
using static Mindweave.Application.Languages.Bilingual;

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

    public string DisplayName => T("Search summary network", "检索总结网络");

    public string Description => T(
        "Searches by keyword in the summaries saved in this workspace (what the user explored before), and returns each one's number, title and relevant excerpts. " +
        "When the answer relies on a summary, write its number after that sentence, for example [1].",
        "在本工作区已保存的知识总结（用户以前探索过的内容）中按关键词检索，返回编号、总结标题与相关片段。" +
        "回答中依据某个总结时，在那句话后写它的编号，例如 [1]。");

    public string ParametersSchema => T(
        """
        {"type":"object","properties":{
          "query":{"type":"string","description":"Search terms; a few keywords are fine"}
        },"required":["query"],"additionalProperties":false}
        """,
        """
        {"type":"object","properties":{
          "query":{"type":"string","description":"检索词，可以是几个关键词"}
        },"required":["query"],"additionalProperties":false}
        """);

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
        var query = ToolArguments.Text(arguments, "query") ?? throw new ToolInputException(T("Give search terms in query.", "请给出检索词 query。"));
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

        var text = new StringBuilder().Append(T(
            $"Searching for \"{query}\" found {Plural(scored.Count, "summary", "summaries")} (when relying on a summary, write its number):\n",
            string.Create(CultureInfo.InvariantCulture, $"检索“{query}”找到 {scored.Count} 个总结（依据某个总结时写它的编号）：\n")));
        var findings = new List<ToolFinding>();
        var sources = new List<RunSource>();
        for (var i = 0; i < scored.Count; i++)
        {
            var (entry, _, body, _) = scored[i];
            var node = entry.Node.Id.Value.ToString("D");
            var number = scope.Sources.NumberFor(RunSources.SummaryKey(node));
            text.Append('\n');
            var start = text.Length;
            text.Append(T(
                    $"[{number}] {entry.Node.DisplayName} (summary {node})\n",
                    $"[{number}] {entry.Node.DisplayName}（总结 {node}）\n"))
                .Append(ToolArguments.Snippet(body, terms, ExcerptLength))
                .Append('\n');

            // The 出处 opens the summary itself (L11); the stored result keeps the passage that was found.
            sources.Add(new RunSource(number, null, entry.Node.DisplayName, default, start, text.Length, entry.Node.UpdatedAt, IsPage: false) { Kind = CitationKind.Summary, Target = node });
            findings.Add(new ToolFinding(ToolFindingKind.Summary, node, entry.Node.DisplayName));
        }

        var result = text.ToString().TrimEnd();
        return new ToolOutput(result, T("Found " + Plural(scored.Count, "summary", "summaries"), $"找到 {scored.Count} 个总结"), ResourceProvenance.Workspace, findings)
        {
            Numbers = [.. sources.Select(s => s.Number)],
            Stored = id => scope.Sources.Register(id, result, sources.Select(s => s with { Resource = id, End = Math.Min(s.End, result.Length) })),
        };
    }

    private static ToolOutput Nothing(string query) => new(
        T($"Searching for \"{query}\" found no relevant summaries.", $"检索“{query}”没有找到相关的总结。"), T("Nothing found", "没有找到"), ResourceProvenance.Workspace, []);

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
