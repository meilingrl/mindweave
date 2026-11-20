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
        Assert.Equal(["用户问", "火车的起源。"], events.OfType<ChatStreamEvent.ReasoningDelta>().Select(e => e.Text));
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
    public async Task Reported_usage_is_passed_on_and_requested()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"a\"},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":7,\"total_tokens\":127}}\n\n"
            + "data: [DONE]\n\n";
        var handler = new StubHandler(_ => Sse(sse));

        var events = await Collect(new ChatCompletionsClient(new HttpClient(handler), new ChatClientOptions()));

        Assert.Equal(new ChatStreamEvent.Usage(120, 7), Assert.Single(events.OfType<ChatStreamEvent.Usage>()));
        // Requested (health check 2026-11-12): OpenAI-semantics endpoints send usage only when asked; statistics and
        // token calibration depend on it.
        Assert.True(JsonNode.Parse(handler.Requests.Single().Body)!["stream_options"]!["include_usage"]!.GetValue<bool>());
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
    public async Task A_rate_limit_carries_the_wait_the_service_asked_for_and_reads_as_transient()
    {
        var limited = await Assert.ThrowsAsync<ChatClientException>(() => Collect(Client(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":{\"message\":\"slow down\"}}") };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        })));
        var broke = await Assert.ThrowsAsync<ChatClientException>(() => Collect(Client(_ => new HttpResponseMessage(HttpStatusCode.PaymentRequired) { Content = new StringContent("insufficient balance") })));
        var refused = await Assert.ThrowsAsync<ChatClientException>(() => Collect(Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("bad key") })));
        var failed = await Assert.ThrowsAsync<ChatClientException>(() => Collect(Client(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("") })));

        Assert.Equal((TimeSpan.FromSeconds(7), true), (limited.RetryAfter, limited.IsTransient));
        Assert.Equal((null, false), (broke.RetryAfter, broke.IsTransient));
        Assert.False(refused.IsTransient);
        Assert.True(failed.IsTransient);
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

    [Theory]
    [InlineData("HTTP 400: max_tokens 16384 exceeds the limit for gpt-4-1106-preview", null)]
    [InlineData("HTTP 400: max_tokens is too large: your request has 8000 input tokens (30000 > 32768 - 8000)", null)]
    [InlineData("HTTP 400: max_tokens must be less than or equal to 4096", 4096)]
    public void Only_a_round_output_limit_is_taken_from_the_message(string detail, int? expected) =>
        Assert.Equal(expected, ChatCompletionsClient.StatedLimit(detail, 16_384));

    [Fact]
    public async Task A_service_that_rejects_the_usage_option_is_asked_again_without_it()
    {
        var handler = new StubHandler(request =>
            request.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains("stream_options", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"Unrecognized request argument supplied: stream_options\"}}") }
                : Sse("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n"));

        var events = await Collect(new ChatCompletionsClient(new HttpClient(handler), new ChatClientOptions()));

        Assert.Single(events.OfType<ChatStreamEvent.Completed>());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(JsonNode.Parse(handler.Requests[1].Body)!["stream_options"]);
    }

    /// <summary>Health check 2026-11-12, ai-client-4: comment keep-alives show the service is alive while it thinks silently.</summary>
    [Fact]
    public async Task Keep_alive_comments_keep_a_silent_stream_alive()
    {
        // Silent for 3.2 s in all, longer than the idle limit, with a comment every 200 ms: ten times the margin a loaded
        // CI runner needs (a 250 ms pace against 1 s failed there once).
        var events = Enumerable.Repeat(": keep-alive\n\n", 16)
            .Append("data: {\"choices\":[{\"delta\":{\"content\":\"答\"},\"finish_reason\":\"stop\"}]}\n\n")
            .ToArray();
        var options = new ChatClientOptions { IdleTimeout = TimeSpan.FromSeconds(2) };
        var paced = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PacedStream(events, TimeSpan.FromMilliseconds(200))) });

        var received = await Collect(new ChatCompletionsClient(new HttpClient(paced), options));

        Assert.Equal("答", Assert.Single(received.OfType<ChatStreamEvent.TextDelta>()).Text);
    }

    /// <summary>Health check 2026-11-12, ai-client-5: a 200 answered with plain JSON is read, not taken for a broken stream.</summary>
    [Fact]
    public async Task A_json_error_answered_with_status_200_is_reported_with_its_message()
    {
        var error = await Assert.ThrowsAsync<ChatClientException>(() => Collect(Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"error\":{\"message\":\"insufficient balance\"}}", Encoding.UTF8, "application/json"),
        })));

        Assert.Contains("insufficient balance", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stream_labelled_as_json_is_still_read_as_a_stream()
    {
        var events = await Collect(Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"答\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "application/json"),
        }));

        Assert.Equal("答", Assert.Single(events.OfType<ChatStreamEvent.TextDelta>()).Text);
    }

    [Fact]
    public async Task A_complete_answer_sent_without_streaming_is_used()
    {
        var events = await Collect(Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"完整的回答\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":3}}", Encoding.UTF8, "application/json"),
        }));

        Assert.Equal("完整的回答", Assert.Single(events.OfType<ChatStreamEvent.TextDelta>()).Text);
        Assert.Equal(new ChatStreamEvent.Completed(false), Assert.Single(events.OfType<ChatStreamEvent.Completed>()));
        Assert.Equal(new ChatStreamEvent.Usage(5, 3), Assert.Single(events.OfType<ChatStreamEvent.Usage>()));
    }

    /// <summary>Review 2026-11-14: a whole answer is decoded as the service declares it, a byte-order mark dropped.</summary>
    [Theory]
    [InlineData("utf-8", true)]
    [InlineData("utf-16", false)]
    public async Task A_complete_answer_is_decoded_by_its_declared_charset(string charset, bool withMark)
    {
        var encoding = charset == "utf-8" ? new UTF8Encoding(withMark) : Encoding.Unicode;
        var json = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"完整的回答\"},\"finish_reason\":\"stop\"}]}";
        var bytes = withMark ? [.. encoding.GetPreamble(), .. encoding.GetBytes(json)] : encoding.GetBytes(json);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = charset };

        var events = await Collect(Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

        Assert.Equal("完整的回答", Assert.Single(events.OfType<ChatStreamEvent.TextDelta>()).Text);
    }

    // Health check 2026-11-12, tests-2: branches no test reached.

    [Fact]
    public async Task A_service_that_does_not_start_answering_in_time_times_out()
    {
        var client = new ChatCompletionsClient(new HttpClient(new SlowHandler(TimeSpan.FromSeconds(30))), new ChatClientOptions { FirstByteTimeout = TimeSpan.FromMilliseconds(100) });

        var error = await Assert.ThrowsAsync<ChatClientException>(() => Collect(client));

        Assert.Equal(GenerationErrorKind.Timeout, error.Kind);
    }

    [Fact]
    public async Task Listing_models_times_out_and_reports_invalid_json_and_never_echoes_the_key()
    {
        var slow = new ChatCompletionsClient(new HttpClient(new SlowHandler(TimeSpan.FromSeconds(30))), new ChatClientOptions { ModelListTimeout = TimeSpan.FromMilliseconds(100) });
        Assert.Equal(GenerationErrorKind.Timeout, (await Assert.ThrowsAsync<ChatClientException>(() => slow.ListModelsAsync("https://api.example.com/v1", Key, Ct))).Kind);

        var garbage = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json", Encoding.UTF8, "application/json") });
        Assert.Equal(GenerationErrorKind.InvalidResponse, (await Assert.ThrowsAsync<ChatClientException>(() => garbage.ListModelsAsync("https://api.example.com/v1", Key, Ct))).Kind);

        var refused = Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent($"{{\"error\":{{\"message\":\"bad key {Key}\"}}}}") });
        var error = await Assert.ThrowsAsync<ChatClientException>(() => refused.ListModelsAsync("https://api.example.com/v1", Key, Ct));
        Assert.Equal(GenerationErrorKind.Unauthorized, error.Kind);
        Assert.DoesNotContain(Key, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stream_event_that_is_not_json_is_an_invalid_response()
    {
        var error = await Assert.ThrowsAsync<ChatClientException>(() => Collect(Client(_ => Sse("data: {not json\n\n"))));

        Assert.Equal(GenerationErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public async Task A_413_naming_the_context_is_a_context_limit()
    {
        var error = await Assert.ThrowsAsync<ChatClientException>(() => Collect(Client(_ => new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge)
        {
            Content = new StringContent("{\"error\":{\"message\":\"This model's maximum context length is 8192 tokens\"}}"),
        })));

        Assert.Equal(GenerationErrorKind.ContextTooLong, error.Kind);
    }

    [Fact]
    public async Task Silence_after_the_answer_finished_ends_the_stream_cleanly()
    {
        var options = new ChatClientOptions { IdleTimeout = TimeSpan.FromMilliseconds(200) };
        var stalls = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FinishedThenSilent("data: {\"choices\":[{\"delta\":{\"content\":\"答\"},\"finish_reason\":\"stop\"}]}\n\n")),
        });

        var events = await Collect(new ChatCompletionsClient(new HttpClient(stalls), options));

        Assert.Single(events.OfType<ChatStreamEvent.Completed>());
    }

    /// <summary>Low finding ai-client-6: a connection dropped after the service finished the answer is not a failure.</summary>
    [Fact]
    public async Task A_connection_dropped_after_the_answer_finished_still_completes()
    {
        var dropping = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FinishedThenDropped("data: {\"choices\":[{\"delta\":{\"content\":\"答\"},\"finish_reason\":\"stop\"}]}\n\n")),
        });

        var events = await Collect(new ChatCompletionsClient(new HttpClient(dropping), new ChatClientOptions()));

        Assert.Single(events.OfType<ChatStreamEvent.Completed>());
    }

    /// <summary>Low finding security-3: a key straddling the cut-off point is scrubbed before the text is cut.</summary>
    [Fact]
    public async Task A_key_at_the_cut_off_point_is_still_scrubbed()
    {
        var body = $"{{\"error\":{{\"message\":\"{new string('x', 495)}{Key} rejected\"}}}}";
        var error = await Assert.ThrowsAsync<ChatClientException>(() => Collect(Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(body) })));

        Assert.DoesNotContain(Key[..8], error.Message, StringComparison.Ordinal);
    }

    /// <summary>Low finding security-4: an endless error body is read only so far.</summary>
    [Fact]
    public async Task A_huge_error_body_is_read_only_in_part()
    {
        var error = await Assert.ThrowsAsync<ChatClientException>(() => Collect(Client(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StreamContent(new NeverEndingText()) })));

        Assert.Equal(GenerationErrorKind.ServerError, error.Kind);
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

    private static readonly ChatTool[] Tools = [new("search_notes", "Searches the notes.", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}")];

    [Fact]
    public async Task Tools_are_offered_with_the_service_choosing()
    {
        var handler = new StubHandler(_ => Sse("data: [DONE]\n\n"));
        var client = new ChatCompletionsClient(new HttpClient(handler), new ChatClientOptions());

        await CollectRequest(client, new ChatRequest(Model, [new ChatTurn(ChatRole.User, "hi")]) { Tools = Tools });
        await CollectRequest(client, new ChatRequest(Model, [new ChatTurn(ChatRole.User, "hi")]));

        var body = JsonNode.Parse(handler.Requests[0].Body)!;
        var tool = Assert.Single(body["tools"]!.AsArray())!;
        Assert.Equal("function", tool["type"]!.GetValue<string>());
        Assert.Equal("search_notes", tool["function"]!["name"]!.GetValue<string>());
        Assert.Equal("Searches the notes.", tool["function"]!["description"]!.GetValue<string>());
        Assert.Equal("string", tool["function"]!["parameters"]!["properties"]!["query"]!["type"]!.GetValue<string>());
        Assert.Equal("auto", body["tool_choice"]!.GetValue<string>());
        Assert.Null(body["provider"]);
        Assert.Null(body["tool_stream"]);
        var plain = JsonNode.Parse(handler.Requests[1].Body)!.AsObject();
        Assert.False(plain.ContainsKey("tools"));
        Assert.False(plain.ContainsKey("tool_choice"));
    }

    [Theory]
    [InlineData("https://openrouter.ai/api/v1", true, false)]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", false, true)]
    [InlineData("https://api.z.ai/api/paas/v4", false, true)]
    [InlineData("https://notopenrouter.ai/v1", false, false)]
    public async Task Services_that_need_more_to_use_tools_are_told(string baseUrl, bool requireParameters, bool toolStream)
    {
        var handler = new StubHandler(_ => Sse("data: [DONE]\n\n"));
        var client = new ChatCompletionsClient(new HttpClient(handler), new ChatClientOptions());
        var model = Model with { BaseUrl = baseUrl };

        await CollectRequest(client, new ChatRequest(model, [new ChatTurn(ChatRole.User, "hi")]) { Tools = Tools });
        await CollectRequest(client, new ChatRequest(model, [new ChatTurn(ChatRole.User, "hi")]));

        var body = JsonNode.Parse(handler.Requests[0].Body)!;
        Assert.Equal(requireParameters, body["provider"]?["require_parameters"]?.GetValue<bool>() ?? false);
        Assert.Equal(toolStream, body["tool_stream"]?.GetValue<bool>() ?? false);
        var plain = JsonNode.Parse(handler.Requests[1].Body)!.AsObject();
        Assert.False(plain.ContainsKey("provider"));
        Assert.False(plain.ContainsKey("tool_stream"));
    }

    [Fact]
    public async Task Tool_calls_and_their_results_are_sent_back_with_the_reasoning()
    {
        var handler = new StubHandler(_ => Sse("data: [DONE]\n\n"));
        var client = new ChatCompletionsClient(new HttpClient(handler), new ChatClientOptions());
        ChatTurn[] turns =
        [
            new(ChatRole.User, "找笔记"),
            new(ChatRole.Assistant, string.Empty)
            {
                ToolCalls = [new ChatToolCall("call_a", "search_notes", "{\"query\":\"火车\"}"), new ChatToolCall("call_b", "search_notes", "{bad", ArgumentsValid: false)],
                Reasoning = "先搜索。",
            },
            new(ChatRole.Tool, "两条结果") { ToolCallId = "call_a" },
            new(ChatRole.Tool, "参数不是 JSON") { ToolCallId = "call_b" },
            new(ChatRole.Assistant, "我再查一次") { ToolCalls = [new ChatToolCall("call_c", "search_notes", "{}")] },
            new(ChatRole.Tool, "无") { ToolCallId = "call_c" },
        ];

        await CollectRequest(client, new ChatRequest(Model, turns) { Tools = Tools });

        var messages = JsonNode.Parse(handler.Requests.Single().Body)!["messages"]!.AsArray();
        var first = messages[1]!.AsObject();
        Assert.Equal("assistant", first["role"]!.GetValue<string>());
        Assert.True(first.ContainsKey("content"));
        Assert.Null(first["content"]);
        Assert.Equal("先搜索。", first["reasoning_content"]!.GetValue<string>());
        var calls = first["tool_calls"]!.AsArray();
        Assert.Equal(["call_a", "call_b"], calls.Select(c => c!["id"]!.GetValue<string>()));
        Assert.All(calls, c => Assert.Equal("function", c!["type"]!.GetValue<string>()));
        Assert.Equal("search_notes", calls[0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("{\"query\":\"火车\"}", calls[0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("{}", calls[1]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("tool", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal("call_a", messages[2]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("两条结果", messages[2]!["content"]!.GetValue<string>());
        Assert.Equal("call_b", messages[3]!["tool_call_id"]!.GetValue<string>());
        var second = messages[4]!.AsObject();
        Assert.Equal("我再查一次", second["content"]!.GetValue<string>());
        Assert.False(second.ContainsKey("reasoning_content"));
        Assert.False(messages[0]!.AsObject().ContainsKey("tool_calls"));
    }

    [Fact]
    public async Task Streamed_fragments_of_parallel_calls_are_joined_per_index()
    {
        var sse = Chunk("{\"role\":\"assistant\",\"content\":\"我查一下\"}")
            + Chunk("{\"tool_calls\":[{\"index\":0,\"id\":\"call_x\",\"type\":\"function\",\"function\":{\"name\":\"search_\",\"arguments\":\"\"}}]}")
            + Chunk("{\"tool_calls\":[{\"index\":1,\"id\":\"call_y\",\"type\":\"function\",\"function\":{\"name\":\"open_note\",\"arguments\":\"{\\\"id\\\"\"}}]}")
            + Chunk("{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"notes\",\"arguments\":\"{\\\"query\\\":\"}}]}")
            + Chunk("{\"tool_calls\":[{\"index\":1,\"function\":{\"arguments\":\":7}\"}}]}")
            + Chunk("{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"火车\\\"}\"}}]}")
            + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n"
            + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}\n\n"
            + "data: [DONE]\n\n";

        var events = await Collect(Client(_ => Sse(sse)));

        var calls = Assert.Single(events.OfType<ChatStreamEvent.ToolCalls>());
        Assert.Equal(
            [new ChatToolCall("call_x", "search_notes", "{\"query\":\"火车\"}"), new ChatToolCall("call_y", "open_note", "{\"id\":7}")],
            calls.Calls);
        Assert.Null(calls.Reasoning);
        Assert.Equal("我查一下", Assert.Single(events.OfType<ChatStreamEvent.TextDelta>()).Text);
        var at = events.IndexOf(calls);
        Assert.Equal(new ChatStreamEvent.Completed(false), events[at + 1]);
        Assert.IsType<ChatStreamEvent.Usage>(events[at + 2]);
    }

    [Fact]
    public async Task Calls_without_ids_or_with_repeated_ids_get_unique_ones()
    {
        var sse = Chunk("{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"a\",\"arguments\":\"{}\"}},{\"index\":1,\"id\":\"\",\"function\":{\"name\":\"b\"}}]}")
            + Chunk("{\"tool_calls\":[{\"index\":2,\"id\":\"call_1\",\"function\":{\"name\":\"c\"}},{\"index\":3,\"id\":\"dup\",\"function\":{\"name\":\"d\"}},{\"index\":4,\"id\":\"dup\",\"function\":{\"name\":\"e\"}}]}")
            + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n";

        var calls = Assert.Single((await Collect(Client(_ => Sse(sse)))).OfType<ChatStreamEvent.ToolCalls>()).Calls;

        Assert.Equal(["a", "b", "c", "d", "e"], calls.Select(c => c.Name));
        Assert.Equal(5, calls.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(["call_1", "call_2"], calls.Take(2).Select(c => c.Id));
        Assert.All(calls, c => Assert.False(string.IsNullOrEmpty(c.Id)));
    }

    [Fact]
    public async Task Fragments_without_an_index_continue_the_last_call_unless_they_bring_a_new_id()
    {
        // A whole name sent again with each fragment is not doubled.
        var sse = Chunk("{\"tool_calls\":[{\"id\":\"c1\",\"function\":{\"name\":\"search_notes\",\"arguments\":\"{\\\"q\\\":\"}}]}")
            + Chunk("{\"tool_calls\":[{\"id\":\"c1\",\"function\":{\"name\":\"search_notes\",\"arguments\":\"1}\"}}]}")
            + Chunk("{\"tool_calls\":[{\"id\":\"c2\",\"function\":{\"name\":\"open_note\",\"arguments\":\"{}\"}}]}")
            + "data: [DONE]\n\n";

        var events = await Collect(Client(_ => Sse(sse)));

        Assert.Equal(
            [new ChatToolCall("c1", "search_notes", "{\"q\":1}"), new ChatToolCall("c2", "open_note", "{}")],
            Assert.Single(events.OfType<ChatStreamEvent.ToolCalls>()).Calls);
        Assert.Equal(new ChatStreamEvent.Completed(false), events[^1]);
    }

    [Theory]
    [InlineData("", "{}", true)]
    [InlineData("  ", "{}", true)]
    [InlineData("{\\\"q\\\":", "{\"q\":", false)]
    [InlineData("[1,2]", "[1,2]", false)]
    [InlineData("\\\"text\\\"", "\"text\"", false)]
    public async Task Arguments_must_be_a_json_object(string sent, string arguments, bool valid)
    {
        var sse = Chunk($"{{\"tool_calls\":[{{\"index\":0,\"id\":\"c\",\"function\":{{\"name\":\"f\",\"arguments\":\"{sent}\"}}}}]}}")
            + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n";

        var call = Assert.Single(Assert.Single((await Collect(Client(_ => Sse(sse)))).OfType<ChatStreamEvent.ToolCalls>()).Calls);

        Assert.Equal(new ChatToolCall("c", "f", arguments, valid), call);
    }

    [Fact]
    public async Task Calls_cut_off_at_the_output_limit_are_not_handed_on()
    {
        var sse = Chunk("{\"tool_calls\":[{\"index\":0,\"id\":\"c\",\"function\":{\"name\":\"f\",\"arguments\":\"{\\\"q\\\":\\\"火\"}}]}")
            + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n";

        var events = await Collect(Client(_ => Sse(sse)));

        Assert.DoesNotContain(events, e => e is ChatStreamEvent.ToolCalls);
        Assert.Equal(new ChatStreamEvent.Completed(true), Assert.Single(events.OfType<ChatStreamEvent.Completed>()));
    }

    [Fact]
    public async Task Reasoning_text_goes_with_the_calls_while_deltas_still_carry_only_its_length()
    {
        var sse = Chunk("{\"reasoning_content\":\"要查\"}")
            + Chunk("{\"reasoning_content\":\"笔记。\"}")
            + Chunk("{\"tool_calls\":[{\"index\":0,\"id\":\"c\",\"function\":{\"name\":\"f\",\"arguments\":\"{}\"}}]}")
            + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n";

        var events = await Collect(Client(_ => Sse(sse)));

        Assert.Equal([2, 3], events.OfType<ChatStreamEvent.ReasoningDelta>().Select(e => e.Characters));
        Assert.Equal("要查笔记。", Assert.Single(events.OfType<ChatStreamEvent.ToolCalls>()).Reasoning);
    }

    [Fact]
    public async Task A_whole_answer_with_tool_calls_is_read_like_a_stream()
    {
        const string body = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":null,\"reasoning_content\":\"想想\",\"tool_calls\":["
            + "{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"f\",\"arguments\":\"{\\\"a\\\":1}\"}},"
            + "{\"type\":\"function\",\"function\":{\"name\":\"g\",\"arguments\":{\"b\":2}}}]},\"finish_reason\":\"tool_calls\"}],"
            + "\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":4}}";

        var events = await Collect(Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

        var calls = Assert.Single(events.OfType<ChatStreamEvent.ToolCalls>());
        Assert.Equal([new ChatToolCall("c1", "f", "{\"a\":1}"), new ChatToolCall("call_2", "g", "{\"b\":2}")], calls.Calls);
        Assert.Equal("想想", calls.Reasoning);
        Assert.Equal(new ChatStreamEvent.Completed(false), events[events.IndexOf(calls) + 1]);
        Assert.Equal(new ChatStreamEvent.ReasoningDelta("想想"), Assert.Single(events.OfType<ChatStreamEvent.ReasoningDelta>()));
        Assert.Empty(events.OfType<ChatStreamEvent.TextDelta>());
    }

    [Theory]
    [InlineData("{\"tool_calls\":{\"index\":0}}")]
    [InlineData("{\"tool_calls\":[\"x\"]}")]
    [InlineData("{\"tool_calls\":[{\"index\":0,\"function\":\"f\"}]}")]
    [InlineData("{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":[1]}}]}")]
    public async Task Tool_calls_of_unexpected_shape_are_client_failures(string delta)
    {
        var error = await Assert.ThrowsAsync<ChatClientException>(() => Collect(Client(_ => Sse(Chunk(delta)))));

        Assert.Equal(GenerationErrorKind.InvalidResponse, error.Kind);
    }

    private static string Chunk(string delta) => $"data: {{\"choices\":[{{\"delta\":{delta}}}]}}\n\n";

    private static async Task<List<ChatStreamEvent>> CollectRequest(ChatCompletionsClient client, ChatRequest request)
    {
        var list = new List<ChatStreamEvent>();
        await foreach (var e in client.StreamAsync(request, Key, Ct))
        {
            list.Add(e);
        }

        return list;
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
    /// <summary>Answers only after <paramref name="delay"/> (or never, when cancelled first).</summary>
    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    /// <summary>Sends <paramref name="text"/>, then the connection breaks.</summary>
    private sealed class FinishedThenDropped(string text) : Stream
    {
        private ReadOnlyMemory<byte> _left = Encoding.UTF8.GetBytes(text);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_left.Length == 0)
            {
                throw new IOException("connection reset");
            }

            var n = Math.Min(buffer.Length, _left.Length);
            _left[..n].CopyTo(buffer);
            _left = _left[n..];
            return ValueTask.FromResult(n);
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

    /// <summary>Sends <paramref name="text"/>, then keeps the connection open without sending anything more.</summary>
    private sealed class FinishedThenSilent(string text) : Stream
    {
        private ReadOnlyMemory<byte> _left = Encoding.UTF8.GetBytes(text);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_left.Length > 0)
            {
                var n = Math.Min(buffer.Length, _left.Length);
                _left[..n].CopyTo(buffer);
                _left = _left[n..];
                return n;
            }

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

    /// <summary>An endless body of 'x'.</summary>
    private sealed class NeverEndingText : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'x', offset, count);
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

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
