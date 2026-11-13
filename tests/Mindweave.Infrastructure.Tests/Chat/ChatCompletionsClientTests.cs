using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Mindweave.Application.Ai;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Infrastructure.Ai;

namespace Mindweave.Infrastructure.Tests.Chat;

public class ChatCompletionsClientTests
{
    private static readonly ModelSnapshot Model = new("cfg", "https://api.example.com/v1/", ChatProtocol.ChatCompletions, "m1", "ref", 32, ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions));

    /// <summary>Not credential-shaped, so only masking of the known key can hide it.</summary>
    private const string Key = "gw-key-0123456789";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Streams_deltas_handles_multiline_events_crlf_comments_and_done()
    {
        var sse = ": keep-alive\r\n\r\n"
            + "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\r\n\r\n"
            + "data: {\"choices\":[{\"delta\":{\"content\":\"蒸汽\"}}]}\n\n"
            + "data: {\"choices\":\ndata: [{\"delta\":{\"content\":\"机\"},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: [DONE]\n\n";
        var handler = new StubHandler(_ => Sse(sse));

        var events = await Collect(new ChatCompletionsClient(new HttpClient(handler), new ChatClientOptions()));

        Assert.Equal(["蒸汽", "机"], events.OfType<ChatStreamEvent.TextDelta>().Select(e => e.Text));
        Assert.Equal(new ChatStreamEvent.Completed(false), Assert.Single(events.OfType<ChatStreamEvent.Completed>()));
        var request = handler.Requests.Single();
        Assert.Equal("https://api.example.com/v1/chat/completions", request.Uri);
        Assert.Equal($"Bearer {Key}", request.Authorization);
        var body = JsonNode.Parse(request.Body)!;
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal(32, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("user", body["messages"]![0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reasoning_is_passed_on_as_its_length_only_and_never_as_text()
    {
        // Kimi and GLM (2026-11-05): many reasoning_content chunks, then content; some chunks carry an empty or null field.
        var sse = "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"reasoning_content\":\"用户问\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"火车的起源。\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"reasoning_content\":null,\"content\":\"蒸汽\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"content\":\"机\"},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: [DONE]\n\n";

        var events = await Collect(Client(_ => Sse(sse)));

        Assert.Equal([3, 6], events.OfType<ChatStreamEvent.ReasoningDelta>().Select(e => e.Characters));
        Assert.Equal(["蒸汽", "机"], events.OfType<ChatStreamEvent.TextDelta>().Select(e => e.Text));
        Assert.IsType<ChatStreamEvent.ReasoningDelta>(events[1]);
        Assert.Equal(new ChatStreamEvent.Completed(false), Assert.Single(events.OfType<ChatStreamEvent.Completed>()));
    }

    [Fact]
    public async Task Reasoning_keeps_a_long_thinking_stream_alive()
    {
        // Eight reasoning events 250 ms apart: twice the idle limit in total, never silent for that long.
        var events = Enumerable.Repeat("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"想\"}}]}\n\n", 8)
            .Append("data: {\"choices\":[{\"delta\":{\"content\":\"答\"},\"finish_reason\":\"stop\"}]}\n\n")
            .ToArray();
        var options = new ChatClientOptions { IdleTimeout = TimeSpan.FromSeconds(1) };
        var paced = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PacedStream(events, TimeSpan.FromMilliseconds(250))) });

        var received = await Collect(new ChatCompletionsClient(new HttpClient(paced), options));

        Assert.Equal(8, received.OfType<ChatStreamEvent.ReasoningDelta>().Count());
        Assert.Equal("答", Assert.Single(received.OfType<ChatStreamEvent.TextDelta>()).Text);
        Assert.Equal(new ChatStreamEvent.Completed(false), received[^1]);
    }

    [Fact]
    public async Task Reported_usage_is_passed_on_and_never_requested()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"a\"},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":7,\"total_tokens\":127}}\n\n"
            + "data: [DONE]\n\n";
        var handler = new StubHandler(_ => Sse(sse));

        var events = await Collect(new ChatCompletionsClient(new HttpClient(handler), new ChatClientOptions()));

        Assert.Equal(new ChatStreamEvent.Usage(120, 7), Assert.Single(events.OfType<ChatStreamEvent.Usage>()));
        Assert.Null(JsonNode.Parse(handler.Requests.Single().Body)!["stream_options"]);
        var plain = await Collect(Client(_ => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"a\"},\"finish_reason\":\"stop\"}]}\n\n")));
        Assert.Empty(plain.OfType<ChatStreamEvent.Usage>());
    }

    [Fact]
    public async Task Usage_inside_the_final_choice_is_passed_on_too()
    {
        // Moonshot (Kimi), 2026-11-05: the counts ride on the last choice, not on the chunk.
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"好\"},\"finish_reason\":\"stop\",\"usage\":{\"prompt_tokens\":89,\"completion_tokens\":61}}]}\n\n"
            + "data: [DONE]\n\n";

        var events = await Collect(Client(_ => Sse(sse)));

        Assert.Equal(new ChatStreamEvent.Usage(89, 61), Assert.Single(events.OfType<ChatStreamEvent.Usage>()));
    }

    [Fact]
    public async Task Done_without_finish_reason_still_completes_and_length_marks_truncation()
    {
        var plain = await Collect(Client(_ => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\ndata: [DONE]\n\n")));
        var cut = await Collect(Client(_ => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"a\"},\"finish_reason\":\"length\"}]}\n\n")));

        Assert.Equal(new ChatStreamEvent.Completed(false), plain[^1]);
        Assert.Equal(new ChatStreamEvent.Completed(true), cut[^1]);
    }

    [Fact]
    public async Task A_stream_that_ends_without_finishing_yields_no_completion()
    {
        var events = await Collect(Client(_ => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"半\"}}]}\n\n")));

        Assert.DoesNotContain(events, e => e is ChatStreamEvent.Completed);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"bad key\"}}", GenerationErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"slow down\"}}", GenerationErrorKind.QuotaOrRateLimited)]
    [InlineData(HttpStatusCode.PaymentRequired, "insufficient balance", GenerationErrorKind.QuotaOrRateLimited)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"This model's maximum context length is 8192 tokens\"}}", GenerationErrorKind.ContextTooLong)]
    [InlineData(HttpStatusCode.NotFound, "model not found", GenerationErrorKind.InvalidResponse)]
    [InlineData(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"internal error\"}}", GenerationErrorKind.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, "<html>bad gateway</html>", GenerationErrorKind.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"overloaded, maximum load reached\"}}", GenerationErrorKind.ServerError)]
    [InlineData(HttpStatusCode.GatewayTimeout, "", GenerationErrorKind.ServerError)]
    [InlineData(HttpStatusCode.Found, "", GenerationErrorKind.InvalidResponse)]
    public async Task Http_errors_are_classified(HttpStatusCode status, string body, GenerationErrorKind kind)
    {
        var error = await Assert.ThrowsAsync<ChatClientException>(() => Collect(Client(_ => new HttpResponseMessage(status) { Content = new StringContent(body) })));

        Assert.Equal(kind, error.Kind);
        Assert.StartsWith($"HTTP {(int)status}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_response_status_and_provider_request_id_come_first()
    {
        var events = await Collect(Client(_ =>
        {
            var response = Sse("data: {\"choices\":[{\"delta\":{\"content\":\"a\"},\"finish_reason\":\"stop\"}]}\n\n");
            response.Headers.Add("x-request-id", "req_123");
            return response;
        }));

        Assert.Equal(new ChatStreamEvent.ResponseStarted(200, "req_123"), events[0]);
    }

    [Fact]
    public async Task Errors_carry_the_status_and_never_echo_the_key()
    {
        var error = await Assert.ThrowsAsync<ChatClientException>(() => Collect(Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":{\"message\":\"Incorrect API key provided: gw-key-0123456789\"}}"),
        })));

        Assert.Equal(401, error.HttpStatus);
        Assert.DoesNotContain(Key, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_error_event_inside_the_stream_fails_the_answer()
    {
        var error = await Assert.ThrowsAsync<ChatClientException>(() => Collect(Client(_ => Sse("data: {\"error\":{\"message\":\"upstream overloaded\"}}\n\n"))));

        Assert.Equal(GenerationErrorKind.ServerError, error.Kind);
        Assert.Contains("overloaded", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Health check 2026-11-12, item 8: internal requests ask for 16,384 output tokens. A service whose limit is lower
    /// rejects the request with a 400 naming the parameter; it is sent once more within the limit it states.
    /// </summary>
    [Fact]
    public async Task An_output_limit_the_service_rejects_is_retried_once_within_the_limit_it_states()
    {
        var handler = new StubHandler(request =>
            (JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!["max_tokens"]?.GetValue<int>() ?? 0) > 8192
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"Invalid max_tokens value, the valid range of max_tokens is [1, 8192]\"}}") }
                : Sse("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n"));

        var events = await CollectUntil(new ChatCompletionsClient(new HttpClient(handler), new ChatClientOptions()), Ct, Model with { MaxOutputTokens = 16_384 });

        Assert.Single(events.OfType<ChatStreamEvent.Completed>());
        Assert.Equal([16_384, 8_192], handler.Requests.Select(r => JsonNode.Parse(r.Body)!["max_tokens"]!.GetValue<int>()));
    }

    [Fact]
    public async Task An_output_parameter_the_service_does_not_take_is_left_out_on_the_second_try()
    {
        var handler = new StubHandler(request =>
            request.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains("max_tokens", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"Unsupported parameter: 'max_tokens' is not supported with this model.\"}}") }
                : Sse("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n"));

        var events = await CollectUntil(new ChatCompletionsClient(new HttpClient(handler), new ChatClientOptions()), Ct, Model with { MaxOutputTokens = 16_384 });

        Assert.Single(events.OfType<ChatStreamEvent.Completed>());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(JsonNode.Parse(handler.Requests[1].Body)!["max_tokens"]);
    }

    [Fact]
    public async Task Other_bad_requests_are_not_retried()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"model not found\"}}") });

        await Assert.ThrowsAsync<ChatClientException>(() => CollectUntil(new ChatCompletionsClient(new HttpClient(handler), new ChatClientOptions()), Ct, Model with { MaxOutputTokens = 16_384 }));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_content_filter_finish_is_a_truncated_and_filtered_completion()
    {
        var events = await Collect(Client(_ => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"a\"},\"finish_reason\":\"content_filter\"}]}\n\n")));

        Assert.Equal(new ChatStreamEvent.Completed(Truncated: true, Filtered: true), Assert.Single(events.OfType<ChatStreamEvent.Completed>()));
    }

    /// <summary>Health check 2026-11-12, item 2: relays answer errors in shapes other than OpenAI's; none may escape as anything but a client failure.</summary>
    [Theory]
    [InlineData("{\"error\":\"invalid api key\"}", "invalid api key")]
    [InlineData("\"Unauthorized\"", "Unauthorized")]
    [InlineData("[1,2]", "[1,2]")]
    [InlineData("{\"error\":{\"message\":42}}", "42")]
    public async Task Error_bodies_of_any_json_shape_are_client_failures(string body, string expected)
    {
        var error = await Assert.ThrowsAsync<ChatClientException>(() => Collect(Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(body) })));

        Assert.Equal(GenerationErrorKind.Unauthorized, error.Kind);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("data: {\"error\":\"upstream overloaded\"}\n\n", GenerationErrorKind.ServerError)]
    [InlineData("data: {\"choices\":{\"delta\":{\"content\":\"a\"}}}\n\n", GenerationErrorKind.InvalidResponse)]
    [InlineData("data: [\"not\",\"an\",\"object\"]\n\n", GenerationErrorKind.InvalidResponse)]
    [InlineData("data: \"text\"\n\n", GenerationErrorKind.InvalidResponse)]
    [InlineData("data: {\"choices\":[\"x\"]}\n\n", GenerationErrorKind.InvalidResponse)]
    public async Task Stream_events_of_unexpected_shape_are_client_failures(string sse, GenerationErrorKind kind)
    {
        var error = await Assert.ThrowsAsync<ChatClientException>(() => Collect(Client(_ => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\n" + sse))));

        Assert.Equal(kind, error.Kind);
    }

    [Fact]
    public async Task Choices_with_a_null_delta_or_content_are_not_failures()
    {
        var events = await Collect(Client(_ => Sse("data: {\"choices\":[{\"delta\":null}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":null},\"finish_reason\":\"stop\"}]}\n\n")));

        Assert.Single(events.OfType<ChatStreamEvent.Completed>());
    }

    [Fact]
    public async Task Anything_unexpected_inside_the_client_leaves_it_as_a_client_failure_without_the_key()
    {
        var error = await Assert.ThrowsAsync<ChatClientException>(() => Collect(Client(_ => throw new InvalidOperationException($"bad state near {Key}"))));

        Assert.Equal(GenerationErrorKind.InvalidResponse, error.Kind);
        Assert.DoesNotContain(Key, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_models_tolerates_unexpected_shapes()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":[\"x\",{\"id\":7},{\"id\":\"ok\"}]}", Encoding.UTF8, "application/json"),
        });
        Assert.Equal(["ok"], await client.ListModelsAsync("https://api.example.com/v1", "k", Ct));

        var odd = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{}}", Encoding.UTF8, "application/json") });
        Assert.Empty(await odd.ListModelsAsync("https://api.example.com/v1", "k", Ct));
    }

    [Fact]
    public async Task A_stalled_stream_times_out_but_user_cancellation_is_a_cancellation()
    {
        var options = new ChatClientOptions { IdleTimeout = TimeSpan.FromMilliseconds(200) };
        var stalled = new ChatCompletionsClient(new HttpClient(new StubHandler(_ => Stalled())), options);
        var timeout = await Assert.ThrowsAsync<ChatClientException>(() => Collect(stalled));
        Assert.Equal(GenerationErrorKind.Timeout, timeout.Kind);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var patient = new ChatCompletionsClient(new HttpClient(new StubHandler(_ => Stalled())), new ChatClientOptions());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectUntil(patient, cts.Token));
    }

    [Fact]
    public async Task Network_failures_are_classified()
    {
        var error = await Assert.ThrowsAsync<ChatClientException>(() => Collect(Client(_ => throw new HttpRequestException("connection refused"))));

        Assert.Equal(GenerationErrorKind.Network, error.Kind);
    }

    [Fact]
    public async Task Lists_model_ids_sorted_and_deduplicated()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":[{\"id\":\"b\"},{\"id\":\"A\"},{\"id\":\"b\"},{\"object\":\"x\"}]}", Encoding.UTF8, "application/json"),
        });

        Assert.Equal(["A", "b"], await client.ListModelsAsync("https://api.example.com/v1", "k", Ct));
    }

    [Fact]
    public void The_production_handler_does_not_follow_redirects()
    {
        using var handler = (SocketsHttpHandler)ChatCompletionsClient.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    private static ChatCompletionsClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)), new ChatClientOptions());

    private static Task<List<ChatStreamEvent>> Collect(ChatCompletionsClient client) => CollectUntil(client, Ct);

    private static async Task<List<ChatStreamEvent>> CollectUntil(ChatCompletionsClient client, CancellationToken cancellationToken, ModelSnapshot? model = null)
    {
        var list = new List<ChatStreamEvent>();
        var request = new ChatRequest(model ?? Model, [new ChatTurn(ChatRole.User, "hi")]);
        await foreach (var e in client.StreamAsync(request, Key, cancellationToken))
        {
            list.Add(e);
        }

        return list;
    }

    private static HttpResponseMessage Sse(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/event-stream") };

    private static HttpResponseMessage Stalled() =>
        new(HttpStatusCode.OK) { Content = new StreamContent(new NeverEndingStream()) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Uri, string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            return respond(request);
        }
    }

    /// <summary>A response body that sends one event at a time with a pause before each, like a model thinking slowly.</summary>
    private sealed class PacedStream(IReadOnlyList<string> events, TimeSpan pause) : Stream
    {
        private int _next;
        private ReadOnlyMemory<byte> _pending;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pending.IsEmpty)
            {
                if (_next == events.Count)
                {
                    return 0;
                }

                await Task.Delay(pause, cancellationToken);
                _pending = Encoding.UTF8.GetBytes(events[_next++]);
            }

            var count = Math.Min(buffer.Length, _pending.Length);
            _pending[..count].CopyTo(buffer);
            _pending = _pending[count..];
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A response body that never produces data, like a stalled server.</summary>
    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
