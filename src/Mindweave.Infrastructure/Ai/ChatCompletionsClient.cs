using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mindweave.Application.Ai;
using Mindweave.Application.Diagnostics;
using Mindweave.Core.Conversations;

namespace Mindweave.Infrastructure.Ai;

public sealed class ChatClientOptions
{
    /// <summary>Until response headers arrive. Reasoning models can take a while before the first byte.</summary>
    public TimeSpan FirstByteTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Longest silence allowed between two stream events.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public TimeSpan ModelListTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// OpenAI-compatible Chat Completions over HTTP + SSE. The HttpClient must not follow redirects, so the
/// API key is never sent to a host the user did not configure.
/// </summary>
public sealed class ChatCompletionsClient(HttpClient http, ChatClientOptions options) : IChatClient
{
    private const int MaxErrorDetail = 500;

    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
    };

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var events = StreamCoreAsync(request, apiKey, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            ChatStreamEvent item;
            try
            {
                if (!await events.MoveNextAsync().ConfigureAwait(false))
                {
                    yield break;
                }

                item = events.Current;
            }
            catch (ChatClientException ex)
            {
                throw Redacted(ex, apiKey);
            }

            yield return item;
        }
    }

    private async IAsyncEnumerable<ChatStreamEvent> StreamCoreAsync(ChatRequest request, string apiKey, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request, apiKey, cancellationToken).ConfigureAwait(false);
        yield return new ChatStreamEvent.ResponseStarted((int)response.StatusCode, ProviderRequestId(response));
        using var reader = new SseReader(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completed = false;
        while (true)
        {
            idle.CancelAfter(options.IdleTimeout);
            var data = await ReadAsync(reader, completed, idle.Token, cancellationToken).ConfigureAwait(false);
            if (data is null || data == "[DONE]")
            {
                if (data == "[DONE]" && !completed)
                {
                    // Some compatible services end with [DONE] and no finish_reason; that is still a clean finish.
                    yield return new ChatStreamEvent.Completed(Truncated: false);
                }

                yield break;
            }

            var chunk = Parse(data);
            if (chunk.Reasoning > 0)
            {
                yield return new ChatStreamEvent.ReasoningDelta(chunk.Reasoning);
            }

            if (chunk.Text is { Length: > 0 } text)
            {
                yield return new ChatStreamEvent.TextDelta(text);
            }

            if (chunk.FinishReason is { } reason && !completed)
            {
                completed = true;
                yield return new ChatStreamEvent.Completed(Truncated: reason == "length");
            }

            if (chunk.Usage is { } usage)
            {
                yield return usage;
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            return await ListModelsCoreAsync(baseUrl, apiKey, cancellationToken).ConfigureAwait(false);
        }
        catch (ChatClientException ex)
        {
            throw Redacted(ex, apiKey);
        }
    }

    private async Task<IReadOnlyList<string>> ListModelsCoreAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ModelListTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{baseUrl.TrimEnd('/')}/models"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            await ThrowIfFailedAsync(response, timeout.Token).ConfigureAwait(false);
            var json = await response.Content.ReadFromJsonAsync<JsonNode>(timeout.Token).ConfigureAwait(false);
            return json?["data"]?.AsArray()
                .Select(m => m?["id"]?.GetValue<string>())
                .OfType<string>()
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatClientException(GenerationErrorKind.Timeout, "Listing models timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new ChatClientException(GenerationErrorKind.Network, ex.Message, ex);
        }
        catch (JsonException ex)
        {
            throw new ChatClientException(GenerationErrorKind.InvalidResponse, "The model list is not valid JSON.", ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(ChatRequest request, string apiKey, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model.Model,
            ["stream"] = true,
            ["messages"] = new JsonArray(request.Turns.Select(t => (JsonNode)new JsonObject
            {
                ["role"] = t.Role switch
                {
                    ChatRole.System => "system",
                    ChatRole.User => "user",
                    _ => "assistant",
                },
                ["content"] = t.Content,
            }).ToArray()),
        };
        if (request.Model.MaxOutputTokens is { } max)
        {
            body["max_tokens"] = max;
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri($"{request.Model.BaseUrl.TrimEnd('/')}/chat/completions"))
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var firstByte = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        firstByte.CancelAfter(options.FirstByteTimeout);
        HttpResponseMessage? response = null;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, firstByte.Token).ConfigureAwait(false);
            await ThrowIfFailedAsync(response, firstByte.Token).ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw new ChatClientException(GenerationErrorKind.Timeout, "The service did not start answering in time.");
        }
        catch (HttpRequestException ex)
        {
            response?.Dispose();
            throw new ChatClientException(GenerationErrorKind.Network, ex.Message, ex);
        }
        catch
        {
            response?.Dispose();
            throw;
        }
    }

    private static async Task<string?> ReadAsync(SseReader reader, bool afterCompletion, CancellationToken idle, CancellationToken user)
    {
        try
        {
            return await reader.ReadEventAsync(idle).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!user.IsCancellationRequested)
        {
            // Silence after the answer already finished is not an error; anything else is a stall.
            return afterCompletion ? null : throw new ChatClientException(GenerationErrorKind.Timeout, "The stream stalled.");
        }
        catch (IOException ex)
        {
            throw new ChatClientException(GenerationErrorKind.Network, ex.Message, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ChatClientException(GenerationErrorKind.Network, ex.Message, ex);
        }
    }

    private static (string? Text, int Reasoning, string? FinishReason, ChatStreamEvent.Usage? Usage) Parse(string data)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(data);
        }
        catch (JsonException ex)
        {
            throw new ChatClientException(GenerationErrorKind.InvalidResponse, "A stream event is not valid JSON.", ex);
        }

        if (node?["error"] is { } error)
        {
            throw new ChatClientException(GenerationErrorKind.ServerError, Limit(error["message"]?.ToString() ?? error.ToJsonString()));
        }

        var choice = node?["choices"]?.AsArray().FirstOrDefault();
        var text = choice?["delta"]?["content"] is JsonValue content && content.TryGetValue<string>(out var s) ? s : null;

        // Reasoning models (Kimi, GLM; 2026-11-05) stream reasoning_content long before any content. Only its length is kept.
        var reasoning = choice?["delta"]?["reasoning_content"] is JsonValue thought && thought.TryGetValue<string>(out var t) ? t.Length : 0;
        var finish = choice?["finish_reason"] is JsonValue reason && reason.TryGetValue<string>(out var r) ? r : null;

        // Services that report usage put it on the last chunk (or a final chunk without choices); it is never requested.
        // Moonshot (Kimi) puts it inside the final choice instead (2026-11-05, real service).
        ChatStreamEvent.Usage? usage = (node?["usage"] as JsonObject ?? choice?["usage"] as JsonObject) is { } counts
            ? new ChatStreamEvent.Usage(Count(counts["prompt_tokens"]), Count(counts["completion_tokens"]))
            : null;
        return (text, reasoning, finish, usage);

        static int? Count(JsonNode? value) => value is JsonValue number && number.TryGetValue<int>(out var n) && n >= 0 ? n : null;
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        if (status is >= 200 and < 300)
        {
            return;
        }

        if (status is >= 300 and < 400)
        {
            throw new ChatClientException(GenerationErrorKind.InvalidResponse, $"HTTP {status}: redirects are not followed; check the service address.", httpStatus: status);
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string detail;
        try
        {
            detail = JsonNode.Parse(text)?["error"]?["message"]?.ToString() ?? text;
        }
        catch (JsonException)
        {
            detail = text;
        }

        detail = $"HTTP {status}: {Limit(detail)}";
        var kind = status switch
        {
            401 or 403 => GenerationErrorKind.Unauthorized,
            402 or 429 => GenerationErrorKind.QuotaOrRateLimited,
            400 or 413 when LooksLikeContextLimit(detail) => GenerationErrorKind.ContextTooLong,
            >= 500 => GenerationErrorKind.ServerError,
            _ => GenerationErrorKind.InvalidResponse,
        };
        throw new ChatClientException(kind, detail, httpStatus: status);
    }

    /// <summary>Every failure leaving this client is scrubbed of the key it was called with (services echo keys).</summary>
    private static ChatClientException Redacted(ChatClientException ex, string apiKey) =>
        new(ex.Kind, Redaction.Scrub(ex.Message, apiKey), ex.InnerException, ex.HttpStatus);

    private static string? ProviderRequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-request-id", out var values) || response.Headers.TryGetValues("request-id", out values)
            ? values.FirstOrDefault()
            : null;

    private static bool LooksLikeContextLimit(string detail) =>
        detail.Contains("context", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("too long", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("maximum", StringComparison.OrdinalIgnoreCase);

    private static string Limit(string text)
    {
        text = text.Trim();
        return text.Length > MaxErrorDetail ? text[..MaxErrorDetail] + "…" : text;
    }
}
