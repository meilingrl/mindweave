using System.Text.RegularExpressions;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;

namespace Mindweave.Application.Tools;

/// <summary>
/// A run's web state (architecture §15.13): the switch it was accepted with, the question (searched first when the switch is
/// 开, decision W1), the numbering of its sources by normalized address, the addresses it may read without asking (Q10,
/// decision W9), the texts of what it stored (to locate cited passages) and the pages already read (decision W12). Used by
/// one run at a time; guarded anyway, as tools may be cancelled on another thread.
/// </summary>
public sealed partial class WebRun
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _allowed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolOutput> _pages = new(StringComparer.Ordinal);

    public WebRun(WebSearchMode mode, string question, IEnumerable<string?>? userTexts = null)
    {
        Mode = mode;
        Question = question ?? string.Empty;
        foreach (var text in userTexts ?? [])
        {
            AllowIn(text);
        }
    }

    public WebSearchMode Mode { get; }

    /// <summary>The question as the user typed it.</summary>
    public string Question { get; }

    /// <summary>The 开 search's query (decision W1, L9); null searches <see cref="Question"/>.</summary>
    public string? PreSearch { get; init; }

    /// <summary>A question shorter than this many characters searches with its context (L9).</summary>
    public const int ShortQuestion = 30;

    /// <summary>Characters of the title and of the quoted passage that go before the question (L9).</summary>
    public const int ContextPart = 100;

    /// <summary>
    /// The 开 search's query (docs/design/agent-loop.md L9): the question as typed, or, when it is short or points back
    /// (它、这个、第二个…), the conversation's title and the branch's quoted passage before it (each at most
    /// <see cref="ContextPart"/> characters); without a title yet, the previous question stands in for it. No model call.
    /// </summary>
    public static string PreSearchQuery(string question, string? title, string? quote, string? previous)
    {
        question = (question ?? string.Empty).Trim();
        if (question.Length >= ShortQuestion && !PointsBack().IsMatch(question))
        {
            return question;
        }

        var parts = new[] { title is { Length: > 0 } ? title : previous, quote }
            .Select(p => WebSearchTool.OneLine(p ?? string.Empty))
            .Where(p => p.Length > 0)
            .Select(p => p.Length > ContextPart ? p[..ContextPart] : p)
            .Distinct(StringComparer.Ordinal)
            .Append(question);
        return string.Join(' ', parts);
    }

    /// <summary>
    /// The query of the search for matching summaries before an answer (requirements §10.9, docs/design/reply-quality.md Q7):
    /// <see cref="PreSearchQuery"/>'s rule, so a short or referring question searches with its title and selection; and in a
    /// branch the selected passage always goes with the question (at most <see cref="ContextPart"/> characters of it).
    /// </summary>
    public static string SummaryQuery(string question, string? title, string? quote, string? previous)
    {
        var query = PreSearchQuery(question, title, quote, previous);
        var part = WebSearchTool.OneLine(quote ?? string.Empty);
        part = part.Length > ContextPart ? part[..ContextPart] : part;
        return part.Length == 0 || query.Contains(part, StringComparison.Ordinal) ? query : part + " " + query;
    }

    /// <summary>The numbering of the run's sources, shared with its local results (L11).</summary>
    public RunSources Numbering { get; } = new();

    /// <summary>The sources numbered so far, by number.</summary>
    public IReadOnlyList<RunSource> Sources => Numbering.All;

    /// <summary>The stored text of a resource this run made, for locating a cited passage.</summary>
    public string? TextOf(ResourceId resource) => Numbering.TextOf(resource);

    /// <summary>
    /// The address as compared and numbered: lower-case scheme and host, no default port, no fragment; path and query kept.
    /// Null for anything but an absolute http(s) address.
    /// </summary>
    public static string? Normalize(Uri? url) =>
        url is { IsAbsoluteUri: true } && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
            ? url.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped)
            : null;

    /// <summary>Whether <paramref name="url"/> may be read without asking: it appeared in this run's results, the user's messages or pages read.</summary>
    public bool IsAllowed(Uri url)
    {
        var key = Normalize(url);
        lock (_gate)
        {
            return key is not null && _allowed.Contains(key);
        }
    }

    public void Allow(Uri url)
    {
        if (Normalize(url) is { } key)
        {
            lock (_gate)
            {
                _allowed.Add(key);
            }
        }
    }

    /// <summary>Every http(s) address written in <paramref name="text"/> may be read without asking.</summary>
    public void AllowIn(string? text)
    {
        foreach (var url in AddressesIn(text))
        {
            Allow(url);
        }
    }

    /// <summary>The http(s) addresses written in a text, trailing punctuation left out.</summary>
    public static IEnumerable<Uri> AddressesIn(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        foreach (Match match in Address().Matches(text))
        {
            var value = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '\'', '"', '*', '_', '>');
            if (Uri.TryCreate(value, UriKind.Absolute, out var url) && Normalize(url) is not null)
            {
                yield return url;
            }
        }
    }

    /// <summary>The number of <paramref name="url"/>: the one it already has in this run, or the next one.</summary>
    public int NumberFor(Uri url) => Numbering.NumberFor(RunSources.KeyOf(url));

    /// <summary>A second address (where a page redirected to) takes the same number as the first.</summary>
    public void Alias(Uri url, int number)
    {
        if (Normalize(url) is { } key)
        {
            Numbering.Alias(key, number);
        }
    }

    /// <summary>Records what a stored resource holds: its text, and the sources in it (see <see cref="RunSources.Register"/>).</summary>
    public void Register(ResourceId resource, string text, IEnumerable<RunSource> sources) => Numbering.Register(resource, text, sources);

    /// <summary>A page this run has read already (decision W12), by its requested or final address.</summary>
    public ToolOutput? PageRead(Uri url)
    {
        var key = Normalize(url);
        lock (_gate)
        {
            return key is null ? null : _pages.GetValueOrDefault(key);
        }
    }

    public void RememberPage(Uri url, ToolOutput output)
    {
        if (Normalize(url) is { } key)
        {
            lock (_gate)
            {
                _pages[key] = output;
            }
        }
    }

    /// <summary>The 出处 of an answer's text (see <see cref="RunSources.CitationsFor"/>).</summary>
    public IReadOnlyList<CitationBlock> CitationsFor(string answer) => Numbering.CitationsFor(answer);

    [GeneratedRegex(@"它|这个|那个|这些|那些|这种|那种|这里|那里|这件|那件|该|其中|上面|上述|前面|刚才|^[这那]|第[一二三四五六七八九十0-9]+[个条项点本种部位次篇]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PointsBack();

    [GeneratedRegex(@"https?://[^\s<>""'`）】」』，。、；！？]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Address();
}
