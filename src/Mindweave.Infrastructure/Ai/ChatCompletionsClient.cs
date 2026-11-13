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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Health check 2026-11-12: anything else (a response shape nobody foresaw, a defect here) would escape the
                // callers' failure handling and leave the answer streaming; it leaves this client as a failed response.
                throw Unexpected(ex, apiKey);
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
                // A filtered answer is cut off too: internal requests reject it like one cut at the limit.
                yield return new ChatStreamEvent.Completed(Truncated: reason is "length" or "content_filter", Filtered: reason == "content_filter");
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Unexpected(ex, apiKey);
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
            return ((json as JsonObject)?["data"] as JsonArray)?
                .Select(m => (m as JsonObject)?["id"] is JsonValue id && id.TryGetValue<string>(out var s) ? s : null)
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
        var max = request.Model.MaxOutputTokens;
        try
        {
            return await SendOnceAsync(request, apiKey, max, cancellationToken).ConfigureAwait(false);
        }
        catch (ChatClientException ex) when (max is not null && ex.HttpStatus == 400 && NamesOutputLimit(ex.Message))
        {
            // Health check 2026-11-12, item 8: internal requests ask for a large output allowance (D-015). A service whose
            // limit is lower, or that does not take the parameter, refuses the request before answering anything; it
            // is sent once more within the limit the service states, or without the parameter.
            return await SendOnceAsync(request, apiKey, StatedLimit(ex.Message, max.Value), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool NamesOutputLimit(string detail) =>
        detail.Contains("max_tokens", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("max_output_tokens", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The largest plausible limit named in the service's message below what was asked for, or null (leave the parameter
    /// out). Output limits are multiples of 1,024; other numbers in such messages (model versions such as 1106, token
    /// counts of the input) are not taken for one (review 2026-11-13).
    /// </summary>
    internal static int? StatedLimit(string detail, int asked)
    {
        // The status prefix ("HTTP 400: ") is not a limit.
        var message = detail.IndexOf(": ", StringComparison.Ordinal) is var colon and >= 0 ? detail[(colon + 2)..] : detail;
        var limits = System.Text.RegularExpressions.Regex.Matches(message, "[0-9]{3,7}")
            .Select(m => int.TryParse(m.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0)
            .Where(n => n >= MinStatedLimit && n < asked && n % 1024 == 0)
            .ToList();
        return limits.Count > 0 ? limits.Max() : null;
    }

    /// <summary>Numbers below this in an error message are not taken for an output limit.</summary>
    private const int MinStatedLimit = 1024;

    private async Task<HttpResponseMessage> SendOnceAsync(ChatRequest request, string apiKey, int? maxOutputTokens, CancellationToken cancellationToken)
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
        if (maxOutputTokens is { } max)
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

        // Every shape is checked rather than assumed (health check 2026-11-12): relays answer in their own shapes, and the
        // indexers throw on a node of the wrong kind.
        if (node is not JsonObject obj)
        {
            throw new ChatClientException(GenerationErrorKind.InvalidResponse, "A stream event is not a JSON object.");
        }

        if (obj["error"] is { } error)
        {
            throw new ChatClientException(GenerationErrorKind.ServerError, Limit(ErrorMessage(error)));
        }

        JsonObject? choice = null;
        if (obj["choices"] is { } choices)
        {
            choice = choices is JsonArray list && (list.Count == 0 || list[0] is null or JsonObject)
                ? list.FirstOrDefault() as JsonObject
                : throw new ChatClientException(GenerationErrorKind.InvalidResponse, "A stream event has choices of an unexpected shape.");
        }

        var delta = choice?["delta"] as JsonObject;
        var text = delta?["content"] is JsonValue content && content.TryGetValue<string>(out var s) ? s : null;

        // Reasoning models (Kimi, GLM; 2026-11-05) stream reasoning_content long before any content. Only its length is kept.
        var reasoning = delta?["reasoning_content"] is JsonValue thought && thought.TryGetValue<string>(out var t) ? t.Length : 0;
        var finish = choice?["finish_reason"] is JsonValue reason && reason.TryGetValue<string>(out var r) ? r : null;

        // Services that report usage put it on the last chunk (or a final chunk without choices); it is never requested.
        // Moonshot (Kimi) puts it inside the final choice instead (2026-11-05, real service).
        ChatStreamEvent.Usage? usage = (obj["usage"] as JsonObject ?? choice?["usage"] as JsonObject) is { } counts
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
            detail = JsonNode.Parse(text) is JsonObject body && body["error"] is { } error ? ErrorMessage(error) : text;
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

    /// <summary>The message of an error node: OpenAI's <c>{"message": …}</c> object, or whatever else a relay sent, as text.</summary>
    private static string ErrorMessage(JsonNode error) =>
        error switch
        {
            JsonObject o when o["message"] is { } message => message is JsonValue v && v.TryGetValue<string>(out var s) ? s : message.ToJsonString(),
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => error.ToJsonString(),
        };

    private static ChatClientException Unexpected(Exception ex, string apiKey) =>
        new(GenerationErrorKind.InvalidResponse, Redaction.Scrub($"Unexpected failure reading the response: {ex.Message}", apiKey), ex);

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
