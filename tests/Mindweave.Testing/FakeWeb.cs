using System.Collections.Concurrent;
using Mindweave.Application.Tools;

namespace Mindweave.Testing;

/// <summary>A search service that answers from a script: hits per query (or for any), or a failure; records every query and key.</summary>
public sealed class FakeSearchProvider : ISearchProvider
{
    private readonly ConcurrentQueue<Func<string, IReadOnlyList<SearchHit>>> _next = new();

    public string Id => "tavily";

    public string DisplayName => "Tavily";

    public string Host => "api.tavily.com";

    /// <summary>Every (query, key, max) searched, in order.</summary>
    public ConcurrentQueue<(string Query, string Key, int Max)> Searches { get; } = new();

    /// <summary>What a search without a script finds: two results about the query.</summary>
    public Func<string, IReadOnlyList<SearchHit>> Default { get; set; } = query =>
    [
        new SearchHit("蒸汽机 - 百科", new Uri("https://example.org/steam"), "瓦特在 1769 年改良了蒸汽机。"),
        new SearchHit("铁路的历史", new Uri("https://example.com/rail?a=1"), "第一条公共铁路 1825 年通车。"),
    ];

    public void Returns(params SearchHit[] hits) => _next.Enqueue(_ => hits);

    public void Fails(SearchFailureKind kind, string message) => _next.Enqueue(_ => throw new SearchProviderException(kind, message));

    public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, string apiKey, CancellationToken cancellationToken)
    {
        Searches.Enqueue((query, apiKey, maxResults));
        var answer = _next.TryDequeue(out var scripted) ? scripted : Default;
        return Task.FromResult(answer(query));
    }
}

/// <summary>A page reader that answers from a table of addresses; anything else fails as unreachable. Records every address asked for.</summary>
public sealed class FakePageFetcher : IWebPageFetcher
{
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task<FetchedPage>>> _pages = new(StringComparer.Ordinal);

    public ConcurrentQueue<Uri> Fetched { get; } = new();

    public void Page(string url, string title, string text, params string[] links) =>
        _pages[url] = _ => Task.FromResult(new FetchedPage(new Uri(url), title, text, "text/html", [.. links.Select(l => new Uri(l))], text.Length));

    public void Fails(string url, FetchFailureKind kind, string message) =>
        _pages[url] = _ => Task.FromException<FetchedPage>(new PageFetchException(kind, message));

    /// <summary>The page is read only when <paramref name="release"/> completes (or the call is cancelled).</summary>
    public void Slow(string url, Task release, string title = "慢网页", string text = "慢网页的正文。") =>
        _pages[url] = async ct =>
        {
            await release.WaitAsync(ct).ConfigureAwait(false);
            return new FetchedPage(new Uri(url), title, text, "text/html", [], text.Length);
        };

    public Task<FetchedPage> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        Fetched.Enqueue(url);
        return _pages.TryGetValue(url.AbsoluteUri, out var page)
            ? page(cancellationToken)
            : Task.FromException<FetchedPage>(new PageFetchException(FetchFailureKind.Network, "找不到这个网站：" + url.Host));
    }
}
