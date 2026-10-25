using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;

namespace Mindweave.Infrastructure.AI;

public sealed class ChatTransportException(string errorCode) : Exception($"聊天服务请求未完成（{errorCode}）。")
{
    public string ErrorCode { get; } = errorCode;
}

public sealed class ChatCompletionsTransport : IChatTransport
{
    private readonly HttpClient _client;
    private readonly TimeSpan _requestTimeout;

    public ChatCompletionsTransport(HttpClient client, TimeSpan? requestTimeout = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _requestTimeout = requestTimeout ?? TimeSpan.FromMinutes(2);
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout.TotalMilliseconds > uint.MaxValue - 1) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
    }

    // Composition must use this factory: redirects must be disabled before sending a Bearer.
    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string secret,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var endpoint = Endpoint(request.Configuration, "chat/completions");
        ValidateSecret(secret);
        if (request.Messages.Count == 0 || request.Messages.Any(x => x.Role is not ("user" or "assistant" or "system") || x.Content is null))
            throw new ArgumentException("聊天消息无效。", nameof(request));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        using var message = Authorized(HttpMethod.Post, endpoint, secret);
        message.Content = JsonContent.Create(new Dictionary<string, object>
        {
            ["model"] = request.Configuration.ModelId,
            ["stream"] = true,
            ["messages"] = request.Messages.Select(x => new { role = x.Role, content = x.Content }).ToArray(),
            [request.Configuration.TokenLimitParameter] = request.Configuration.MaxOutputTokens
        });
        using var response = await Guard(() => _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token), cancellationToken);
        CheckStatus(response);
        using var stream = await Guard(() => response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken);
        await using var events = SseReader.ReadAsync(stream, timeout.Token).GetAsyncEnumerator(timeout.Token);
        var hasBody = false;
        var stopped = false;
        while (await Guard(async () => await events.MoveNextAsync(), cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (events.Current == "[DONE]") break;
            var item = ParseEvent(events.Current);
            if (item is null) continue;
            if (stopped && (item.Text is not null || item.FinishReason is not null)) throw new ChatTransportException("InvalidResponse");
            hasBody |= !string.IsNullOrWhiteSpace(item.Text);
            yield return item;
            if (item.FinishReason is { } finish)
            {
                if (finish != "stop") throw new ChatTransportException(finish == "length" ? "OutputTruncated" : "UnsupportedFinish");
                stopped = true;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!hasBody) throw new ChatTransportException("EmptyResponse");
        if (!stopped) throw new ChatTransportException("IncompleteResponse");
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(AiConfiguration configuration, string secret, CancellationToken cancellationToken)
    {
        var endpoint = Endpoint(configuration, "models");
        ValidateSecret(secret);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        using var message = Authorized(HttpMethod.Get, endpoint, secret);
        using var response = await Guard(() => _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token), cancellationToken);
        CheckStatus(response);
        using var stream = await Guard(() => response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await Guard(async () => await stream.ReadAsync(buffer, timeout.Token), cancellationToken)) > 0)
        {
            if (bytes.Length + count > 1024 * 1024) throw new ChatTransportException("ResponseTooLarge");
            bytes.Write(buffer, 0, count);
        }
        return ParseModels(bytes.ToArray());
    }

    private static IReadOnlyList<string> ParseModels(byte[] bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new ChatTransportException("InvalidResponse");
            var result = new List<string>();
            foreach (var model in data.EnumerateArray())
            {
                var id = model.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id)) throw new ChatTransportException("InvalidResponse");
                if (!result.Contains(id, StringComparer.Ordinal)) result.Add(id);
            }
            return result;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { throw new ChatTransportException("InvalidResponse"); }
    }

    private static ChatStreamEvent? ParseEvent(string value)
    {
        try
        {
            using var json = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 });
            var root = json.RootElement;
            if (root.TryGetProperty("error", out _)) throw new ChatTransportException("ServiceError");
            var model = root.TryGetProperty("model", out var modelValue) ? modelValue.GetString() : null;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) throw new ChatTransportException("InvalidResponse");
            if (choices.GetArrayLength() == 0) return model is null ? null : new(ActualModelId: model);
            if (choices.GetArrayLength() != 1) throw new ChatTransportException("InvalidResponse");
            var choice = choices[0];
            if (choice.TryGetProperty("index", out var index) && index.GetInt32() != 0) throw new ChatTransportException("InvalidResponse");
            var text = choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) ? content.GetString() : null;
            var finish = choice.TryGetProperty("finish_reason", out var finishValue) ? finishValue.GetString() : null;
            return new(text, finish, model);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { throw new ChatTransportException("InvalidResponse"); }
    }

    private static Uri Endpoint(AiConfiguration config, string path)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.ModelId) || config.MaxOutputTokens < 1 || config.ContextTokenBudget <= config.MaxOutputTokens ||
            config.TokenLimitParameter is not ("max_tokens" or "max_completion_tokens") ||
            !Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("AI 服务配置无效。", nameof(config));
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/" + path);
    }
    private static void ValidateSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Any(c => c < '!' || c > '~')) throw new ArgumentException("服务密钥无效。", nameof(secret));
    }
    private static HttpRequestMessage Authorized(HttpMethod method, Uri endpoint, string secret)
    {
        var message = new HttpRequestMessage(method, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return message;
    }
    private static void CheckStatus(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 300 and < 400) throw new ChatTransportException("RedirectRejected");
        if (!response.IsSuccessStatusCode) throw new ChatTransportException("ServiceError");
    }
    private static async Task<T> Guard<T>(Func<Task<T>> operation, CancellationToken caller)
    {
        try { return await operation(); }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw new OperationCanceledException(caller); }
        catch (OperationCanceledException) { throw new ChatTransportException("Timeout"); }
        catch (Exception e) when (e is HttpRequestException or IOException or System.Text.DecoderFallbackException)
        { throw new ChatTransportException("TransportError"); }
    }
}
