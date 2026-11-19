using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;

namespace Mindweave.Application.Tools;

/// <summary>
/// One numbered source of a run (requirements §10.4.5, decisions W4 and L11): its number, its title, what it is, the stored
/// resource that holds it and the range of that resource's text that is this source (one search result, a whole page, one
/// part of an attachment, one summary in a search's results), and when it was fetched. A web source has its address; a
/// summary has its node as <see cref="Target"/>.
/// </summary>
public sealed record RunSource(int Number, Uri? Url, string Title, ResourceId Resource, int Start, int End, DateTimeOffset FetchedAt, bool IsPage)
{
    public CitationKind Kind { get; init; } = CitationKind.Web;

    public string? Target { get; init; }
}

/// <summary>
/// The numbering of a run's sources (architecture §15.9 and §15.13; docs/design/agent-loop.md L11): web sources by normalized
/// address, local ones by what they are (an attachment's part, a summary), all sharing one sequence of [n]; the sources by
/// number; and the texts of what the run stored, to locate a cited passage. Used by one run, from tools that may run at once.
/// </summary>
public sealed class RunSources
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _numbers = new(StringComparer.Ordinal);
    private readonly Dictionary<int, RunSource> _sources = [];
    private readonly Dictionary<ResourceId, string> _texts = [];

    /// <summary>The key a web address is numbered by.</summary>
    public static string KeyOf(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return WebRun.Normalize(url) ?? url.AbsoluteUri;
    }

    /// <summary>The key a part of a stored resource is numbered by.</summary>
    public static string KeyOf(ResourceId resource, int part) => $"resource:{resource}#{part}";

    /// <summary>The key a saved summary is numbered by.</summary>
    public static string SummaryKey(string node) => "summary:" + node;

    /// <summary>The sources numbered so far, by number.</summary>
    public IReadOnlyList<RunSource> All
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

    /// <summary>The number of <paramref name="key"/>: the one it already has in this run, or the next one.</summary>
    public int NumberFor(string key)
    {
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

    /// <summary>A second key (where a page redirected to) takes the same number as the first.</summary>
    public void Alias(string key, int number)
    {
        lock (_gate)
        {
            _numbers.TryAdd(key, number);
        }
    }

    /// <summary>
    /// Records what a stored resource holds: its text (when given), and the sources in it. A page replaces a search result of
    /// the same number (the page itself is the better place to show); otherwise the first registration stays.
    /// </summary>
    public void Register(ResourceId resource, string? text, IEnumerable<RunSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        lock (_gate)
        {
            if (text is not null)
            {
                _texts[resource] = text;
            }

            foreach (var source in sources)
            {
                if (!_sources.TryGetValue(source.Number, out var existing) || (source.IsPage && !existing.IsPage))
                {
                    _sources[source.Number] = source;
                }
            }
        }
    }

    /// <summary>
    /// The 出处 of an answer's text (requirements §10.4.5): one block per number the text cites that is a source of this run, in
    /// number order, a page's passage located by the sentence of its first citation (decision W6). Numbers that name nothing are left.
    /// </summary>
    public IReadOnlyList<CitationBlock> CitationsFor(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var sources = All.ToDictionary(s => s.Number);
        var blocks = new List<CitationBlock>();
        foreach (var marker in CitationMarkers.Find(answer).GroupBy(m => m.Number).Select(g => g.First()))
        {
            if (!sources.TryGetValue(marker.Number, out var source))
            {
                continue;
            }

            var (start, end) = Locate(source, CitationMarkers.SentenceBefore(answer, marker));
            blocks.Add(new CitationBlock(source.Number, source.Title, source.Url, source.FetchedAt, source.Resource, start, end) { Kind = source.Kind, Target = source.Target });
        }

        return [.. blocks.OrderBy(b => b.Number)];
    }

    /// <summary>The part of a source's range whose chunk shares the most terms with the citing sentence; the source's start when none does.</summary>
    private (int Start, int End) Locate(RunSource source, string sentence)
    {
        // A search result is short and exact; a page, or a part of local material (L11), is narrowed to the citing paragraph.
        var text = TextOf(source.Resource);
        if (text is null || (!source.IsPage && source.Kind == CitationKind.Web) || source.End > text.Length)
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

        // Within the chunk, the paragraph that shares the most terms (a chunk holds several paragraphs of a page).
        var narrowed = best;
        var at = best.Start;
        while (at < best.End && best.Score > 0)
        {
            var next = text.IndexOf("\n\n", at, best.End - at, StringComparison.Ordinal);
            var stop = next < 0 ? best.End : next;
            var paragraph = text[at..stop];
            var score = terms.Count(t => paragraph.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (score > 0 && (score > narrowed.Score || narrowed == best) && paragraph.Trim().Length > 0)
            {
                narrowed = (score, at, stop);
            }

            at = stop + 2;
        }

        return (narrowed.Start, narrowed.End);
    }
}
