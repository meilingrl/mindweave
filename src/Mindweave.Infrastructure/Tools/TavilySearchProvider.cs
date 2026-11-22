using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mindweave.Application.Diagnostics;
using Mindweave.Application.Languages;
using Mindweave.Application.Tools;

namespace Mindweave.Infrastructure.Tools;

/// <summary>
/// The Tavily search API (Q12, decision W7): one <c>basic</c> search, results only (no generated answer, no page content,
/// no images). The key goes only into the request's Authorization header; the client must not follow redirects, so it is
/// never sent to another host, and every failure's text is scrubbed of it.
/// </summary>
public sealed class TavilySearchProvider : ISearchProvider
{
    internal static readonly Uri Endpoint = new("https://api.tavily.com/search");

    internal const int MaxSnippet = 600;

    private const int MaxDetail = 200;

    private const int MaxBodyBytes = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public TavilySearchProvider(HttpClient http, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    public string Id => "tavily";

    public string DisplayName => "Tavily";

    public string Host => Endpoint.Host;

    /// <summary>No redirects (the key stays with Tavily); the system proxy is used like model requests (decision W8).</summary>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
    };

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, string apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new SearchProviderException(SearchFailureKind.MissingKey, new BilingualText(
                "The search service's key is not set yet. Set it in Settings › Tools › Web search.",
                "还没有设置搜索服务的密钥，可在设置 › 工具 › 网络搜索中设置。"));
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            throw new SearchProviderException(SearchFailureKind.BadQuery, new BilingualText("The search terms are empty.", "检索词是空的。"));
        }

        try
        {
            return await SearchCoreAsync(query, Math.Clamp(maxResults, 1, 20), apiKey, cancellationToken).ConfigureAwait(false);
        }
        catch (SearchProviderException ex)
        {
            throw new SearchProviderException(
                ex.Kind, new BilingualText(Redaction.Scrub(ex.Text.English, apiKey), Redaction.Scrub(ex.Text.Chinese, apiKey)), ex.InnerException);
        }
    }

    private async Task<IReadOnlyList<SearchHit>> SearchCoreAsync(string query, int maxResults, string apiKey, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var body = new JsonObject
        {
            ["query"] = query,
            ["search_depth"] = "basic",
            ["max_results"] = maxResults,
            ["include_answer"] = false,
            ["include_raw_content"] = false,
            ["include_images"] = false,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var text = await ReadAtMostAsync(response.Content, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw Failure((int)response.StatusCode, Detail(text, apiKey));
            }

            return Parse(text, maxResults);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SearchProviderException(SearchFailureKind.Timeout, new BilingualText(
                string.Create(CultureInfo.InvariantCulture, $"The search service did not answer within {_timeout.TotalSeconds:0} seconds. Try again later."),
                string.Create(CultureInfo.InvariantCulture, $"搜索服务 {_timeout.TotalSeconds:0} 秒内没有回应，请稍后再试。")));
        }
        catch (HttpRequestException ex)
        {
            var reason = Short(Redaction.Scrub(ex.Message, apiKey));
            throw new SearchProviderException(
                SearchFailureKind.Network,
                new BilingualText($"Could not connect to the search service ({Endpoint.Host}): {reason}", $"连不上搜索服务（{Endpoint.Host}）：{reason}"),
                ex);
        }
        catch (JsonException ex)
        {
            throw new SearchProviderException(SearchFailureKind.ServiceError, Unreadable, ex);
        }
    }

    private static BilingualText Unreadable => new("The search service's answer could not be read.", "搜索服务的回答无法读取。");

    private static SearchProviderException Failure(int status, string? detail)
    {
        var (kind, message) = status switch
        {
            401 or 403 => (SearchFailureKind.KeyRejected, new BilingualText(
                "The search service refused the key. Check it in Settings › Tools › Web search.", "搜索服务拒绝了密钥，请在设置 › 工具 › 网络搜索中检查。")),
            429 => (SearchFailureKind.RateLimited, new BilingualText("Too many searches. Try again later.", "搜索过于频繁，请稍后再试。")),
            432 or 433 => (SearchFailureKind.QuotaExhausted, new BilingualText("The search service's credits are used up.", "搜索服务的额度已用完。")),
            400 or 422 => (SearchFailureKind.BadQuery, new BilingualText("The search service did not accept these search terms.", "搜索服务没有接受这个检索词。")),
            >= 500 => (SearchFailureKind.ServiceError, new BilingualText(
                string.Create(CultureInfo.InvariantCulture, $"The search service failed (HTTP {status}). Try again later."),
                string.Create(CultureInfo.InvariantCulture, $"搜索服务出错了（HTTP {status}），请稍后再试。"))),
            _ => (SearchFailureKind.ServiceError, new BilingualText(
                string.Create(CultureInfo.InvariantCulture, $"The search service returned HTTP {status}."),
                string.Create(CultureInfo.InvariantCulture, $"搜索服务返回了 HTTP {status}。"))),
        };
        return new SearchProviderException(kind, detail is { Length: > 0 }
            ? new BilingualText($"{message.English} (The service says: {detail})", $"{message.Chinese}（服务说明：{detail}）")
            : message);
    }

    /// <summary>The service's own short explanation (<c>detail.error</c>, <c>detail</c> or <c>error</c>), scrubbed, then cut.</summary>
    private static string? Detail(string body, string apiKey)
    {
        string? detail = null;
        try
        {
            if (JsonNode.Parse(body) is JsonObject root)
            {
                detail = root["detail"] switch
                {
                    JsonObject inner => TextOf(inner["error"]) ?? TextOf(inner["message"]),
                    var value => TextOf(value),
                } ?? TextOf(root["error"]) ?? TextOf(root["message"]);
            }
        }
        catch (JsonException)
        {
            detail = body.TrimStart().StartsWith('<') ? null : body;
        }

        detail = detail is null ? null : Collapse(Redaction.Scrub(detail, apiKey));
        return detail is { Length: > 0 } ? Short(detail) : null;
    }

    private static string? TextOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static string Short(string text) => text.Length > MaxDetail ? text[..MaxDetail] + "…" : text;

    private static List<SearchHit> Parse(string body, int maxResults)
    {
        if (JsonNode.Parse(body) is not JsonObject root || root["results"] is not JsonArray results)
        {
            throw new SearchProviderException(SearchFailureKind.ServiceError, Unreadable);
        }

        var hits = new List<SearchHit>();
        foreach (var item in results.OfType<JsonObject>())
        {
            if (TextOf(item["url"]) is not { } address
                || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var url)
                || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            var title = TextOf(item["title"]) is { } t ? Collapse(t) : "";
            var snippet = TextOf(item["content"]) is { } c ? Collapse(c) : "";
            if (snippet.Length > MaxSnippet)
            {
                snippet = snippet[..MaxSnippet] + "…";
            }

            hits.Add(new SearchHit(title.Length > 0 ? title : url.Host, url, snippet));
            if (hits.Count >= maxResults)
            {
                break;
            }
        }

        return hits;
    }

    private static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static async Task<string> ReadAtMostAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var body = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while (body.Length < MaxBodyBytes && (read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                body.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)Math.Min(body.Length, MaxBodyBytes));
        }
    }
}
