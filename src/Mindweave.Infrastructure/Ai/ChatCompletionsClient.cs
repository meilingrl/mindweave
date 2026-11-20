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

        // A 200 that is not a stream (health check 2026-11-12, ai-client-5): a relay's error, or an endpoint that ignored
        // stream=true and sent the whole answer. Read it instead of reporting a stream that ended without finishing.
        Stream source;
        if (response.Content.Headers.ContentType?.MediaType is { } media && media.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            var body = await ReadWholeAsync(response, cancellationToken).ConfigureAwait(false);

            // Some relays stream events but label them as JSON (review 2026-11-13): those are read as a stream.
            if (!LooksLikeEvents(body))
            {
                foreach (var item in ParseWhole(body))
                {
                    yield return item;
                }

                yield break;
            }

            source = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        }
        else
        {
            source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }

        using var reader = new SseReader(source);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Any line, a keep-alive comment included, shows the service is alive (health check 2026-11-12, ai-client-4).
        reader.LineRead = () => idle.CancelAfter(options.IdleTimeout);
        var completed = false;

        // Tool calls arrive as fragments across many chunks (phase 2); they and the reasoning text are kept until the reply
        // finishes and then handed on whole, once.
        var calls = new ToolCallAccumulator();
        var reasoning = new System.Text.StringBuilder();
        while (true)
        {
            idle.CancelAfter(options.IdleTimeout);
            var data = await ReadAsync(reader, completed, idle.Token, cancellationToken).ConfigureAwait(false);
            if (data is null || data == "[DONE]")
            {
                if (!completed && calls.Any)
                {
                    // Calls the service made without saying why it stopped: handed on, as at a clean finish. A stream that
                    // ended without [DONE] still yields no completion, so callers keep treating it as unfinished.
                    yield return calls.Build(reasoning);
                }

                if (data == "[DONE]" && !completed)
                {
                    // Some compatible services end with [DONE] and no finish_reason; that is still a clean finish.
                    yield return new ChatStreamEvent.Completed(Truncated: false);
                }

                yield break;
            }

            var chunk = Parse(data);
            if (chunk.Reasoning is { Length: > 0 } thought)
            {
                if (!completed)
                {
                    reasoning.Append(thought);
                }

                yield return new ChatStreamEvent.ReasoningDelta(thought);
            }

            if (chunk.Text is { Length: > 0 } text)
            {
                yield return new ChatStreamEvent.TextDelta(text);
            }

            if (chunk.ToolCalls is { } fragments && !completed)
            {
                calls.Add(fragments);
            }

            if (chunk.FinishReason is { } reason && !completed)
            {
                completed = true;
                foreach (var item in Finish(reason, calls, reasoning))
                {
                    yield return item;
                }
            }

            if (chunk.Usage is { } usage)
            {
                yield return usage;
            }
        }
    }

    /// <summary>
    /// The events of a finish: the tool calls made, if any, then the completion. A filtered answer is cut off too: internal
    /// requests reject it like one cut at the limit. Calls cut off at the limit (or by the filter) are not handed on: their
    /// arguments are not whole.
    /// </summary>
    private static IEnumerable<ChatStreamEvent> Finish(string reason, ToolCallAccumulator calls, System.Text.StringBuilder reasoning)
    {
        var cut = reason is "length" or "content_filter";
        if (calls.Any && !cut)
        {
            yield return calls.Build(reasoning);
        }

        yield return new ChatStreamEvent.Completed(Truncated: cut, Filtered: reason == "content_filter");
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
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            await ThrowIfFailedAsync(response, timeout.Token).ConfigureAwait(false);
            // Read within a limit like every other body (low finding security-4).
            var (listing, cut) = await ReadAtMostAsync(response.Content, MaxErrorBodyBytes * 16, timeout.Token).ConfigureAwait(false);
            var json = cut ? throw new ChatClientException(GenerationErrorKind.InvalidResponse, "The model list is larger than any list could be.") : JsonNode.Parse(listing);
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
        var usage = true;
        var limitLowered = false;
        while (true)
        {
            try
            {
                return await SendOnceAsync(request, apiKey, max, usage, cancellationToken).ConfigureAwait(false);
            }
            catch (ChatClientException ex) when (ex.HttpStatus == 400 && usage && ex.Message.Contains("stream_options", StringComparison.OrdinalIgnoreCase))
            {
                // Usage is asked for (health check 2026-11-12): statistics and token calibration depend on it. A service
                // that does not know the option refuses the request before answering; it is sent again without it.
                usage = false;
            }
            catch (ChatClientException ex) when (ex.HttpStatus == 400 && max is not null && !limitLowered && NamesOutputLimit(ex.Message))
            {
                // Health check 2026-11-12, item 8: internal requests ask for a large output allowance (D-015). A service
                // whose limit is lower, or that does not take the parameter, refuses the request before answering
                // anything; it is sent once more within the limit the service states, or without the parameter.
                limitLowered = true;
                max = StatedLimit(ex.Message, max.Value);
            }
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

    private async Task<HttpResponseMessage> SendOnceAsync(ChatRequest request, string apiKey, int? maxOutputTokens, bool includeUsage, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model.Model,
            ["stream"] = true,
            ["messages"] = new JsonArray(request.Turns.Select(MessageOf).ToArray()),
        };
        if (request.HasTools)
        {
            AddTools(body, request);
        }

        if (maxOutputTokens is { } max)
        {
            body["max_tokens"] = max;
        }

        if (includeUsage)
        {
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
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

    private static JsonNode MessageOf(ChatTurn turn)
    {
        var message = new JsonObject
        {
            ["role"] = turn.Role switch
            {
                ChatRole.System => "system",
                ChatRole.User => "user",
                ChatRole.Tool => "tool",
                _ => "assistant",
            },
        };
        if (turn.Role == ChatRole.Tool)
        {
            message["tool_call_id"] = turn.ToolCallId ?? string.Empty;
            message["content"] = turn.Content;
            return message;
        }

        if (turn.Role == ChatRole.Assistant && turn.ToolCalls is { Count: > 0 } calls)
        {
            // An assistant turn that only called tools has no text; services expect null rather than "" there.
            message["content"] = turn.Content.Length > 0 ? turn.Content : null;
            message["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = c.Name,

                    // Services reject history whose arguments are not JSON; the run already handed the error back.
                    ["arguments"] = c.ArgumentsValid ? c.Arguments : "{}",
                },
            }).ToArray());
        }
        else
        {
            message["content"] = ContentOf(turn);
        }

        // DeepSeek and Kimi refuse a tool loop whose earlier replies come back without their reasoning; OpenAI never
        // returns any, so it never gets this field.
        if (turn.Role == ChatRole.Assistant && turn.Reasoning is { Length: > 0 } reasoning)
        {
            message["reasoning_content"] = reasoning;
        }

        return message;
    }

    /// <summary>
    /// The tools offered, always with the service choosing (several services reject any other <c>tool_choice</c>), and what
    /// some services need besides: OpenRouter silently drops the tools when it routes to a provider without them unless
    /// told to require them; GLM streams tool calls only when asked to (model-capabilities §3.3).
    /// </summary>
    private static void AddTools(JsonObject body, ChatRequest request)
    {
        body["tools"] = new JsonArray(request.Tools!.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = SchemaOf(t),
            },
        }).ToArray());
        body["tool_choice"] = "auto";
        var host = Uri.TryCreate(request.Model.BaseUrl, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
        if (IsHost(host, "openrouter.ai"))
        {
            body["provider"] = new JsonObject { ["require_parameters"] = true };
        }

        if (IsHost(host, "bigmodel.cn") || IsHost(host, "z.ai"))
        {
            body["tool_stream"] = true;
        }

        static bool IsHost(string host, string domain) =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonNode SchemaOf(ChatTool tool)
    {
        if (string.IsNullOrWhiteSpace(tool.ParametersSchema))
        {
            return new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        }

        try
        {
            return JsonNode.Parse(tool.ParametersSchema) as JsonObject
                ?? throw new ChatClientException(GenerationErrorKind.InvalidResponse, $"The input schema of tool {tool.Name} is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new ChatClientException(GenerationErrorKind.InvalidResponse, $"The input schema of tool {tool.Name} is not valid JSON.", ex);
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
            // A dropped connection after the service said the answer is finished loses nothing (low finding ai-client-6).
            return afterCompletion ? null : throw new ChatClientException(GenerationErrorKind.Network, ex.Message, ex);
        }
        catch (HttpRequestException ex)
        {
            return afterCompletion ? null : throw new ChatClientException(GenerationErrorKind.Network, ex.Message, ex);
        }
    }

    /// <summary>The whole body of a JSON answer, within the idle limit: a body that stalls after the headers is a timeout.</summary>
    private async Task<string> ReadWholeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(options.IdleTimeout);
        try
        {
            var (body, cut) = await ReadAtMostAsync(response.Content, MaxWholeBodyBytes, limit.Token).ConfigureAwait(false);
            return cut ? throw new ChatClientException(GenerationErrorKind.InvalidResponse, "The response is larger than any answer could be.") : body;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatClientException(GenerationErrorKind.Timeout, "The response stalled.");
        }
    }

    private static bool LooksLikeEvents(string body)
    {
        var start = body.TrimStart();
        return start.StartsWith("data:", StringComparison.Ordinal) || start.StartsWith(':') || start.StartsWith("event:", StringComparison.Ordinal);
    }

    /// <summary>A whole JSON answer: an error, or a non-streamed completion turned into the events a stream would have sent.</summary>
    private static List<ChatStreamEvent> ParseWhole(string body)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new ChatClientException(GenerationErrorKind.InvalidResponse, $"The service answered with invalid JSON instead of a stream: {Limit(body)}", ex);
        }

        if (node is not JsonObject obj)
        {
            throw new ChatClientException(GenerationErrorKind.InvalidResponse, $"The service answered with JSON instead of a stream: {Limit(body)}");
        }

        if (obj["error"] is { } error)
        {
            throw new ChatClientException(GenerationErrorKind.ServerError, Limit(ErrorMessage(error)));
        }

        if ((obj["choices"] as JsonArray)?.FirstOrDefault() is not JsonObject choice || choice["message"] is not JsonObject message)
        {
            throw new ChatClientException(GenerationErrorKind.InvalidResponse, $"The service answered with JSON that is neither a stream nor an answer: {Limit(body)}");
        }

        var events = new List<ChatStreamEvent>();
        var reasoning = new System.Text.StringBuilder();
        if (message["reasoning_content"] is JsonValue thought && thought.TryGetValue<string>(out var t) && t.Length > 0)
        {
            reasoning.Append(t);
            events.Add(new ChatStreamEvent.ReasoningDelta(t));
        }

        if (message["content"] is JsonValue content && content.TryGetValue<string>(out var text) && text.Length > 0)
        {
            events.Add(new ChatStreamEvent.TextDelta(text));
        }

        var calls = new ToolCallAccumulator();
        if (ToolCallFragments(message["tool_calls"]) is { } fragments)
        {
            // A whole answer lists each call once; its position stands in for a missing index.
            calls.Add(fragments.Select((c, i) => c.Index is null ? c with { Index = i } : c).ToList());
        }

        var reason = choice["finish_reason"] is JsonValue r && r.TryGetValue<string>(out var f) ? f : "stop";
        events.AddRange(Finish(reason, calls, reasoning));
        if (obj["usage"] is JsonObject counts)
        {
            events.Add(new ChatStreamEvent.Usage(Count(counts["prompt_tokens"]), Count(counts["completion_tokens"])));
        }

        return events;

        static int? Count(JsonNode? value) => value is JsonValue number && number.TryGetValue<int>(out var n) && n >= 0 ? n : null;
    }

    private static Chunk Parse(string data)
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

        // Reasoning models (Kimi, GLM; 2026-11-05) stream reasoning_content long before any content. Only its length is passed
        // on as it arrives; the text is kept for the tool calls of the reply, which some services need it back with.
        var reasoning = delta?["reasoning_content"] is JsonValue thought && thought.TryGetValue<string>(out var t) ? t : null;
        var tools = ToolCallFragments(delta?["tool_calls"]);
        var finish = choice?["finish_reason"] is JsonValue reason && reason.TryGetValue<string>(out var r) ? r : null;

        // Usage comes on the last chunk (or a final chunk without choices); it is requested with stream_options.
        // Moonshot (Kimi) puts it inside the final choice instead (2026-11-05, real service).
        ChatStreamEvent.Usage? usage = (obj["usage"] as JsonObject ?? choice?["usage"] as JsonObject) is { } counts
            ? new ChatStreamEvent.Usage(Count(counts["prompt_tokens"]), Count(counts["completion_tokens"]))
            : null;
        return new Chunk(text, reasoning, finish, usage, tools);

        static int? Count(JsonNode? value) => value is JsonValue number && number.TryGetValue<int>(out var n) && n >= 0 ? n : null;
    }

    /// <summary>
    /// The tool call fragments of a delta, or the calls of a whole message: <c>{index, id, type, function: {name, arguments}}</c>.
    /// Null when there are none; a list of any other shape is a failed response.
    /// </summary>
    private static List<ToolCallFragment>? ToolCallFragments(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is not JsonArray list)
        {
            throw new ChatClientException(GenerationErrorKind.InvalidResponse, "The service sent tool calls of an unexpected shape.");
        }

        var fragments = new List<ToolCallFragment>(list.Count);
        foreach (var item in list)
        {
            if (item is not JsonObject call || call["function"] is not (null or JsonObject))
            {
                throw new ChatClientException(GenerationErrorKind.InvalidResponse, "The service sent a tool call of an unexpected shape.");
            }

            var function = call["function"] as JsonObject;
            int? index = call["index"] is JsonValue i && i.TryGetValue<int>(out var n) && n >= 0 ? n : null;
            var arguments = function?["arguments"] switch
            {
                null => null,
                JsonValue v when v.TryGetValue<string>(out var a) => a,

                // Some relays send the arguments as an object rather than as JSON text.
                JsonObject o => o.ToJsonString(),
                _ => throw new ChatClientException(GenerationErrorKind.InvalidResponse, "The service sent tool call arguments of an unexpected shape."),
            };
            fragments.Add(new ToolCallFragment(index, Text(call["id"]), Text(function?["name"]), arguments));
        }

        return fragments;

        static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    /// <summary>What one stream event carries.</summary>
    private readonly record struct Chunk(string? Text, string? Reasoning, string? FinishReason, ChatStreamEvent.Usage? Usage, IReadOnlyList<ToolCallFragment>? ToolCalls);

    /// <summary>One piece of a tool call; <paramref name="Index"/> is null when the service gave none.</summary>
    private sealed record ToolCallFragment(int? Index, string? Id, string? Name, string? Arguments);

    /// <summary>
    /// Joins tool call fragments per index into whole calls. The first non-empty ID wins (some services repeat it in every
    /// fragment), names and arguments are joined in order. A fragment without an index continues the last call unless it
    /// brings an ID of its own, which starts a new one.
    /// </summary>
    private sealed class ToolCallAccumulator
    {
        private readonly SortedDictionary<int, (string? Id, System.Text.StringBuilder Name, System.Text.StringBuilder Arguments)> _calls = [];
        private int? _last;

        public bool Any => _calls.Count > 0;

        public void Add(IReadOnlyList<ToolCallFragment> fragments)
        {
            foreach (var fragment in fragments)
            {
                Add(fragment);
            }
        }

        public ChatStreamEvent.ToolCalls Build(System.Text.StringBuilder reasoning)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var calls = new List<ChatToolCall>(_calls.Count);
            foreach (var (index, call) in _calls)
            {
                // IDs the service did not give are generated; every ID is unique within the reply, as results name them.
                var id = call.Id is { Length: > 0 } given ? given : $"call_{index + 1}";
                var unique = id;
                for (var n = 2; !used.Add(unique); n++)
                {
                    unique = $"{id}_{n}";
                }

                var arguments = call.Arguments.ToString();
                calls.Add(string.IsNullOrWhiteSpace(arguments)
                    ? new ChatToolCall(unique, call.Name.ToString(), "{}")
                    : new ChatToolCall(unique, call.Name.ToString(), arguments, IsObject(arguments)));
            }

            return new ChatStreamEvent.ToolCalls(calls, reasoning.Length > 0 ? reasoning.ToString() : null);
        }

        private void Add(ToolCallFragment fragment)
        {
            int index;
            if (fragment.Index is { } given)
            {
                index = given;
            }
            else if (_last is { } last && !(fragment.Id is { Length: > 0 } id && _calls[last].Id is { Length: > 0 } have && have != id))
            {
                index = last;
            }
            else
            {
                index = _calls.Count == 0 ? 0 : _calls.Keys.Max() + 1;
            }

            if (!_calls.TryGetValue(index, out var call))
            {
                call = (null, new System.Text.StringBuilder(), new System.Text.StringBuilder());
            }

            if (call.Id is not { Length: > 0 } && fragment.Id is { Length: > 0 })
            {
                call.Id = fragment.Id;
            }

            // Some services send the whole name in every fragment instead of pieces of it.
            if (fragment.Name is { Length: > 0 } name && !(name.Length > 1 && call.Name.Equals(name.AsSpan())))
            {
                call.Name.Append(name);
            }

            call.Arguments.Append(fragment.Arguments);
            _calls[index] = call;
            _last = index;
        }

        private static bool IsObject(string arguments)
        {
            try
            {
                return JsonNode.Parse(arguments) is JsonObject;
            }
            catch (JsonException)
            {
                return false;
            }
        }
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

        var (text, _) = await ReadAtMostAsync(response.Content, MaxErrorBodyBytes, cancellationToken).ConfigureAwait(false);
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
            400 or 413 when LooksLikeContextLimit(detail.Length > MaxErrorDetail ? detail[..MaxErrorDetail] : detail) => GenerationErrorKind.ContextTooLong,
            >= 500 => GenerationErrorKind.ServerError,
            _ => GenerationErrorKind.InvalidResponse,
        };
        var wait = response.Headers.RetryAfter is { } retry
            ? retry.Delta ?? (retry.Date is { } date ? date - DateTimeOffset.UtcNow : null)
            : null;
        throw new ChatClientException(kind, detail, httpStatus: status) { RetryAfter = wait is { } w && w > TimeSpan.Zero ? w : null };
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
        new(GenerationErrorKind.InvalidResponse, ScrubbedAndShort($"Unexpected failure reading the response: {ex.Message}", apiKey), ex);

    /// <summary>Every failure leaving this client is scrubbed of the key it was called with (services echo keys).</summary>
    private static ChatClientException Redacted(ChatClientException ex, string apiKey) =>
        new(ex.Kind, ScrubbedAndShort(ex.Message, apiKey), ex.InnerException, ex.HttpStatus) { RetryAfter = ex.RetryAfter };

    private static string? ProviderRequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-request-id", out var values) || response.Headers.TryGetValues("request-id", out values)
            ? values.FirstOrDefault() is { } id ? (id.Length > 128 ? id[..128] : id) : null
            : null;

    private static bool LooksLikeContextLimit(string detail) =>
        detail.Contains("context", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("too long", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("maximum", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The text as it goes into a failure. Not cut here: a key straddling the cut would survive in part, since the scrub
    /// matches whole keys (low finding security-3); failures are cut after scrubbing, as they leave this client.
    /// </summary>
    private static string Limit(string text) => text.Trim();

    /// <summary>The failure's text, scrubbed of the key first and only then cut to a readable length.</summary>
    private static string ScrubbedAndShort(string text, string apiKey)
    {
        var scrubbed = Redaction.Scrub(text, apiKey);
        return scrubbed.Length > MaxErrorDetail + 40 ? scrubbed[..(MaxErrorDetail + 40)] + "…" : scrubbed;
    }

    /// <summary>Most of an error body worth reading; a larger one is cut (low finding security-4: bodies were read in full).</summary>
    private const int MaxErrorBodyBytes = 64 * 1024;

    /// <summary>The largest whole JSON answer read (a non-streamed completion of a very long answer stays well below).</summary>
    private const int MaxWholeBodyBytes = 16 * 1024 * 1024;

    /// <summary>Reads at most <paramref name="maxBytes"/> of the body as UTF-8; <paramref name="cut"/> says whether more was there.</summary>
    /// <summary>
    /// A turn's content: its text, or, with images (a model declaring image input, architecture §15.4), the text part followed
    /// by one <c>image_url</c> part per image as a base64 data URL (public URLs are refused by some services).
    /// </summary>
    private static JsonNode ContentOf(ChatTurn turn)
    {
        if (!turn.HasImages)
        {
            return JsonValue.Create(turn.Content);
        }

        var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = turn.Content } };
        foreach (var image in turn.Images!)
        {
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data)}" },
            });
        }

        return parts;
    }

    private static async Task<(string Text, bool Cut)> ReadAtMostAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[Math.Min(maxBytes + 1, 81920)];
        using var collected = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            collected.Write(buffer, 0, read);
            if (collected.Length > maxBytes)
            {
                return (Decode(content, collected.GetBuffer(), maxBytes), true);
            }
        }

        return (Decode(content, collected.GetBuffer(), (int)collected.Length), false);
    }

    /// <summary>As ReadAsStringAsync does (review 2026-11-14): the declared charset, else UTF-8, and a byte-order mark dropped.</summary>
    private static string Decode(HttpContent content, byte[] bytes, int length)
    {
        System.Text.Encoding encoding;
        try
        {
            encoding = content.Headers.ContentType?.CharSet is { Length: > 0 } charset ? System.Text.Encoding.GetEncoding(charset.Trim('"')) : System.Text.Encoding.UTF8;
        }
        catch (ArgumentException)
        {
            encoding = System.Text.Encoding.UTF8;
        }

        using var reader = new StreamReader(new MemoryStream(bytes, 0, length, writable: false), encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
