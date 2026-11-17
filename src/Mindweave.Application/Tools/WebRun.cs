using System.Text.RegularExpressions;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;

namespace Mindweave.Application.Tools;

/// <summary>
/// One numbered source of a run (requirements §10.4.5, decision W4): its number, address, title, the stored resource that holds
/// it, the range of that resource's text that is this source (one search result, or a whole page), and when it was fetched.
/// </summary>
public sealed record WebSource(int Number, Uri Url, string Title, ResourceId Resource, int Start, int End, DateTimeOffset FetchedAt, bool IsPage);

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
    private readonly Dictionary<string, int> _numbers = new(StringComparer.Ordinal);
    private readonly Dictionary<int, WebSource> _sources = [];
    private readonly Dictionary<ResourceId, string> _texts = [];
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

    /// <summary>The question as the user typed it (the 开 search's query).</summary>
    public string Question { get; }

    /// <summary>The sources numbered so far, by number.</summary>
    public IReadOnlyList<WebSource> Sources
    {
        get
        {
            lock (_gate)
            {
                return [.. _sources.Values.OrderBy(s => s.Number)];
            }
        }
    }

    /// <summary>The stored text of a resource this run made, for locating a cited passage.</summary>
    public string? TextOf(ResourceId resource)
    {
        lock (_gate)
        {
            return _texts.GetValueOrDefault(resource);
        }
    }

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
    public int NumberFor(Uri url)
    {
        var key = Normalize(url) ?? url.AbsoluteUri;
        lock (_gate)
        {
            if (_numbers.TryGetValue(key, out var number))
            {
                return number;
            }

            number = _numbers.Count == 0 ? 1 : _numbers.Values.Max() + 1;
            _numbers[key] = number;
            return number;
        }
    }

    /// <summary>A second address (where a page redirected to) takes the same number as the first.</summary>
    public void Alias(Uri url, int number)
    {
        if (Normalize(url) is { } key)
        {
            lock (_gate)
            {
                _numbers.TryAdd(key, number);
            }
        }
    }

    /// <summary>
    /// Records what a stored resource holds: its text, and the sources in it. A page replaces a search result of the same number
    /// (the page itself is the better place to show); otherwise the first registration stays.
    /// </summary>
    public void Register(ResourceId resource, string text, IEnumerable<WebSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        lock (_gate)
        {
            _texts[resource] = text;
            foreach (var source in sources)
            {
                if (!_sources.TryGetValue(source.Number, out var existing) || (source.IsPage && !existing.IsPage))
                {
                    _sources[source.Number] = source;
                }
            }
        }
    }

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

    /// <summary>
    /// The 出处 of an answer's text (requirements §10.4.5): one block per number the text cites that is a source of this run, in
    /// number order, its passage located by the sentence of its first citation (decision W6). Numbers that name nothing are left.
    /// </summary>
    public IReadOnlyList<CitationBlock> CitationsFor(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var sources = Sources.ToDictionary(s => s.Number);
        var blocks = new List<CitationBlock>();
        foreach (var marker in CitationMarkers.Find(answer).GroupBy(m => m.Number).Select(g => g.First()))
        {
            if (!sources.TryGetValue(marker.Number, out var source))
            {
                continue;
            }

            var (start, end) = Locate(source, CitationMarkers.SentenceBefore(answer, marker));
            blocks.Add(new CitationBlock(source.Number, source.Title, source.Url, source.FetchedAt, source.Resource, start, end));
        }

        return [.. blocks.OrderBy(b => b.Number)];
    }

    /// <summary>The part of a source's range whose chunk shares the most terms with the citing sentence; the source's start when none does.</summary>
    private (int Start, int End) Locate(WebSource source, string sentence)
    {
        var text = TextOf(source.Resource);
        if (text is null || !source.IsPage)
        {
            return (source.Start, source.End);
        }

        var terms = ToolArguments.Terms(sentence);
        var best = (Score: 0, Start: source.Start, End: Math.Min(source.End, source.Start + 400));
        foreach (var chunk in ResourceChunker.Split(text))
        {
            var start = Math.Max(chunk.Start, source.Start);
            var end = Math.Min(chunk.End, source.End);
            if (end <= start)
            {
                continue;
            }

            var part = text[start..end];
            var score = terms.Count(t => part.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (score > best.Score)
            {
                best = (score, start, end);
            }
        }

        return (best.Start, best.End);
    }

    [GeneratedRegex(@"https?://[^\s<>""'`）】」』，。、；！？]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Address();
}
