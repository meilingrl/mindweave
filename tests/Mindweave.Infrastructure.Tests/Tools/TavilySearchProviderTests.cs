using System.Net;
using System.Text.Json.Nodes;
using Mindweave.Application.Tools;
using Mindweave.Infrastructure.Tools;

namespace Mindweave.Infrastructure.Tests.Tools;

/// <summary>The Tavily adapter (decision W7, architecture §15.13) against a fake handler; no request leaves the process.</summary>
public class TavilySearchProviderTests
{
    /// <summary>Not credential-shaped, so only masking of the known key can hide it.</summary>
    private const string Key = "tvly-dev-0123456789abcdef";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sends_a_basic_search_with_the_key_only_in_the_bearer_header()
    {
        var handler = new FakeHandler(_ => Responses.Text("""{"results":[]}""", "application/json"));
        var provider = new TavilySearchProvider(new HttpClient(handler));

        await provider.SearchAsync("蒸汽机 历史", 5, Key, Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.tavily.com/search", request.Uri.ToString());
        Assert.Equal($"Bearer {Key}", request.Headers["Authorization"]);
        var body = JsonNode.Parse(request.Body)!.AsObject();
        Assert.Equal("蒸汽机 历史", body["query"]!.GetValue<string>());
        Assert.Equal("basic", body["search_depth"]!.GetValue<string>());
        Assert.Equal(5, body["max_results"]!.GetValue<int>());
        Assert.False(body["include_answer"]!.GetValue<bool>());
        Assert.False(body["include_raw_content"]!.GetValue<bool>());
        Assert.False(body["include_images"]!.GetValue<bool>());
        Assert.DoesNotContain(Key, request.Body, StringComparison.Ordinal);
        Assert.Equal(("tavily", "Tavily", "api.tavily.com"), (provider.Id, provider.DisplayName, provider.Host));
    }

    [Fact]
    public async Task Parses_results_skips_bad_addresses_falls_back_to_the_host_and_trims_snippets()
    {
        var longText = new string('长', 900);
        var json = new JsonObject
        {
            ["results"] = new JsonArray
            {
                new JsonObject { ["title"] = "  Steam   engine ", ["url"] = "https://en.wikipedia.org/wiki/Steam_engine", ["content"] = "A heat\n engine." },
                new JsonObject { ["title"] = "Bad", ["url"] = "ftp://files.example.com/x", ["content"] = "x" },
                new JsonObject { ["title"] = "Relative", ["url"] = "/not/absolute", ["content"] = "x" },
                new JsonObject { ["title"] = "", ["url"] = "http://example.org/page", ["content"] = longText },
                new JsonObject { ["title"] = "No url", ["content"] = "x" },
            },
        };
        var provider = new TavilySearchProvider(new HttpClient(new FakeHandler(_ => Responses.Text(json.ToJsonString(), "application/json"))));

        var hits = await provider.SearchAsync("steam", 5, Key, Ct);

        Assert.Equal(2, hits.Count);
        Assert.Equal(new SearchHit("Steam engine", new Uri("https://en.wikipedia.org/wiki/Steam_engine"), "A heat engine."), hits[0]);
        Assert.Equal("example.org", hits[1].Title);
        Assert.Equal(TavilySearchProvider.MaxSnippet + 1, hits[1].Snippet.Length);
        Assert.EndsWith("…", hits[1].Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Returns_at_most_the_requested_number_of_hits()
    {
        var results = new JsonArray();
        for (var i = 0; i < 8; i++)
        {
            results.Add(new JsonObject { ["title"] = $"t{i}", ["url"] = $"https://example.com/{i}", ["content"] = "c" });
        }

        var provider = new TavilySearchProvider(new HttpClient(new FakeHandler(_ => Responses.Text(new JsonObject { ["results"] = results }.ToJsonString(), "application/json"))));

        Assert.Equal(3, (await provider.SearchAsync("q", 3, Key, Ct)).Count);
    }

    [Theory]
    [InlineData(401, SearchFailureKind.KeyRejected, "拒绝了密钥")]
    [InlineData(403, SearchFailureKind.KeyRejected, "拒绝了密钥")]
    [InlineData(429, SearchFailureKind.RateLimited, "过于频繁")]
    [InlineData(432, SearchFailureKind.QuotaExhausted, "额度已用完")]
    [InlineData(433, SearchFailureKind.QuotaExhausted, "额度已用完")]
    [InlineData(400, SearchFailureKind.BadQuery, "检索词")]
    [InlineData(422, SearchFailureKind.BadQuery, "检索词")]
    [InlineData(500, SearchFailureKind.ServiceError, "HTTP 500")]
    [InlineData(503, SearchFailureKind.ServiceError, "HTTP 503")]
    public async Task Maps_each_status_to_a_reason_with_the_service_detail(int status, SearchFailureKind kind, string words)
    {
        var body = """{"detail":{"error":"Something the service said."}}""";
        var provider = new TavilySearchProvider(new HttpClient(new FakeHandler(_ => Responses.Text(body, "application/json", (HttpStatusCode)status))));

        var failure = await Assert.ThrowsAsync<SearchProviderException>(() => provider.SearchAsync("q", 5, Key, Ct));

        Assert.Equal(kind, failure.Kind);
        Assert.Contains(words, failure.Message, StringComparison.Ordinal);
        Assert.Contains("Something the service said.", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plain_detail_string_is_used_and_cut_short()
    {
        var body = new JsonObject { ["detail"] = new string('x', 1000) }.ToJsonString();
        var provider = new TavilySearchProvider(new HttpClient(new FakeHandler(_ => Responses.Text(body, "application/json", HttpStatusCode.BadRequest))));

        var failure = await Assert.ThrowsAsync<SearchProviderException>(() => provider.SearchAsync("q", 5, Key, Ct));

        Assert.Equal(SearchFailureKind.BadQuery, failure.Kind);
        Assert.InRange(failure.Message.Length, 200, 260);
    }

    [Fact]
    public async Task The_key_never_appears_in_a_failure_even_when_the_service_echoes_it()
    {
        var echoed = new JsonObject { ["detail"] = new JsonObject { ["error"] = $"Invalid API key: {Key}" } }.ToJsonString();
        var provider = new TavilySearchProvider(new HttpClient(new FakeHandler(_ => Responses.Text(echoed, "application/json", HttpStatusCode.Unauthorized))));

        var failure = await Assert.ThrowsAsync<SearchProviderException>(() => provider.SearchAsync("q", 5, Key, Ct));

        Assert.Equal(SearchFailureKind.KeyRejected, failure.Kind);
        Assert.DoesNotContain(Key, failure.Message, StringComparison.Ordinal);
        Assert.Contains("Invalid API key", failure.Message, StringComparison.Ordinal);

        // Echoed in a body that is not JSON, and in a network failure's message.
        var plain = new TavilySearchProvider(new HttpClient(new FakeHandler(_ => Responses.Text($"bad key {Key}", "text/plain", HttpStatusCode.Forbidden))));
        var second = await Assert.ThrowsAsync<SearchProviderException>(() => plain.SearchAsync("q", 5, Key, Ct));
        Assert.DoesNotContain(Key, second.Message, StringComparison.Ordinal);

        var network = new TavilySearchProvider(new HttpClient(new FakeHandler(_ => throw new HttpRequestException($"proxy refused {Key}"))));
        var third = await Assert.ThrowsAsync<SearchProviderException>(() => network.SearchAsync("q", 5, Key, Ct));
        Assert.Equal(SearchFailureKind.Network, third.Kind);
        Assert.DoesNotContain(Key, third.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_answer_is_a_service_error()
    {
        var provider = new TavilySearchProvider(new HttpClient(new FakeHandler(_ => Responses.Text("{not json", "application/json"))));
        Assert.Equal(SearchFailureKind.ServiceError, (await Assert.ThrowsAsync<SearchProviderException>(() => provider.SearchAsync("q", 5, Key, Ct))).Kind);

        var noResults = new TavilySearchProvider(new HttpClient(new FakeHandler(_ => Responses.Text("""{"answer":"x"}""", "application/json"))));
        Assert.Equal(SearchFailureKind.ServiceError, (await Assert.ThrowsAsync<SearchProviderException>(() => noResults.SearchAsync("q", 5, Key, Ct))).Kind);
    }

    [Fact]
    public async Task A_service_that_does_not_answer_in_time_is_a_timeout()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var provider = new TavilySearchProvider(new HttpClient(handler), TimeSpan.FromMilliseconds(100));

        var failure = await Assert.ThrowsAsync<SearchProviderException>(() => provider.SearchAsync("q", 5, Key, Ct));

        Assert.Equal(SearchFailureKind.Timeout, failure.Kind);
    }

    [Fact]
    public async Task Cancelling_is_not_a_failure()
    {
        using var cancel = new CancellationTokenSource();
        var handler = new FakeHandler(async (_, ct) =>
        {
            await cancel.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var provider = new TavilySearchProvider(new HttpClient(handler));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SearchAsync("q", 5, Key, cancel.Token));
    }

    [Fact]
    public async Task A_network_failure_is_reported_as_such()
    {
        var provider = new TavilySearchProvider(new HttpClient(new FakeHandler(_ => throw new HttpRequestException("No such host is known."))));

        var failure = await Assert.ThrowsAsync<SearchProviderException>(() => provider.SearchAsync("q", 5, Key, Ct));

        Assert.Equal(SearchFailureKind.Network, failure.Kind);
        Assert.Contains("api.tavily.com", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_key_nothing_is_sent()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("must not be called"));
        var provider = new TavilySearchProvider(new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<SearchProviderException>(() => provider.SearchAsync("q", 5, " ", Ct));

        Assert.Equal(SearchFailureKind.MissingKey, failure.Kind);
        Assert.Equal("还没有设置搜索服务的密钥，可在设置 › 工具 › 网络搜索中设置。", failure.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void The_production_handler_does_not_follow_redirects()
    {
        using var handler = Assert.IsType<SocketsHttpHandler>(TavilySearchProvider.CreateHandler());
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.True(handler.UseProxy);
    }
}
