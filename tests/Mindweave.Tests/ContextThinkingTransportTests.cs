using System.Net;
using System.Text.Json;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;
using Mindweave.Infrastructure.AI;
using Xunit;
using static Mindweave.Tests.ContextThinkingTests;

namespace Mindweave.Tests;

public sealed class ContextThinkingTransportTests
{
    internal sealed class CaptureHandler : HttpMessageHandler
    {
        public List<string?> Bodies { get; } = [];
        public List<Uri> Uris { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Uris.Add(request.RequestUri!); Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(token));
            return new(HttpStatusCode.OK) { Content = new StringContent(request.Method == HttpMethod.Get ? "{\"data\":[{\"id\":\"model\"}]}" :
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n") };
        }
    }
    internal static async Task Send(ChatCompletionsTransport transport, ChatRequest request)
    { await foreach (var item in transport.StreamAsync(request, "synthetic-token", default)) { } }

    [Theory]
    [InlineData(0, 0, false)] [InlineData(0, 1, false)] [InlineData(1, 0, false)] [InlineData(1, 1, true)]
    public async Task Only_explicit_summary_with_disabled_policy_gets_thinking_parameter(int mode, int purpose, bool expected)
    {
        using var handler = new CaptureHandler(); using var client = new HttpClient(handler);
        var transport = new ChatCompletionsTransport(client);
        // A misleading model or prompt never grants summary privileges.
        var config = Config(mode) with { ModelId = "deepseek-reasoner" };
        var request = Property(new ChatRequest(config, [new("system", "Summarize the supplied original conversation slices"), new("user", "hello")]), "Purpose", purpose);
        await Send(transport, request);
        using var json = JsonDocument.Parse(Assert.Single(handler.Bodies)!);
        Assert.Equal(expected, json.RootElement.TryGetProperty("thinking", out var thinking));
        if (expected) Assert.Equal("{\"type\":\"disabled\"}", thinking.GetRawText());
        Assert.False(json.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(json.RootElement.TryGetProperty("Purpose", out _));
        Assert.False(json.RootElement.TryGetProperty("SummaryThinking", out _));
        Assert.Equal(config.ModelId, json.RootElement.GetProperty("model").GetString());
        Assert.Equal(4096, json.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Theory]
    [InlineData("https://api.deepseek.com")] [InlineData("https://api.deepseek.com/")]
    [InlineData("https://api.deepseek.com/v1")] [InlineData("https://api.deepseek.com/v1/")]
    [InlineData("https://API.DEEPSEEK.COM:443/v1")]
    public async Task Allowed_endpoints_preserve_path_and_model_get_has_no_body(string url)
    {
        using var handler = new CaptureHandler(); using var client = new HttpClient(handler); var transport = new ChatCompletionsTransport(client);
        var config = Config(1, url); await transport.ListModelsAsync(config, "synthetic", default);
        Assert.Null(Assert.Single(handler.Bodies));
        Assert.Equal(new Uri(url.TrimEnd('/') + "/models"), Assert.Single(handler.Uris));
        Assert.Equal(url, config.BaseUrl);
    }

    public static TheoryData<string> InvalidUrls => new()
    {
        "http://api.deepseek.com/v1", "https://other.invalid/v1", "https://api.deepseek.com.evil.invalid/v1",
        "https://sub.api.deepseek.com/v1", "https://api.deepseek.com:444/v1", "https://api.deepseek.com/v2",
        "https://api.deepseek.com/v1//", "https://api.deepseek.com//", "https://api.deepseek.com/V1",
        "https://u@api.deepseek.com/v1", "https://@api.deepseek.com/v1", "https://api.deepseek.com/v1?q=1",
        "https://api.deepseek.com/v1#f", "https://api.deepseek.com/a/../v1", "https://api.deepseek.com/%76%31",
        "https://api.deepseek.com./v1", "https://api.deepseek.com\\v1", " https://api.deepseek.com/v1 "
    };
    [Theory] [MemberData(nameof(InvalidUrls))]
    public async Task Disabled_invalid_endpoint_rejected_before_post_and_models_handler(string url)
    {
        using var handler = new CaptureHandler(); using var client = new HttpClient(handler); var transport = new ChatCompletionsTransport(client);
        var config = Config(1, url);
        await Assert.ThrowsAsync<ArgumentException>(() => Send(transport, Property(new ChatRequest(config, [new("user", "hello")]), "Purpose", 1)));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.ListModelsAsync(config, "synthetic", default));
        Assert.Empty(handler.Bodies);
    }
    [Theory] [InlineData(-1, 0)] [InlineData(2, 0)] [InlineData(0, -1)] [InlineData(0, 2)]
    public async Task Unknown_mode_or_purpose_never_reaches_handler(int mode, int purpose)
    {
        using var handler = new CaptureHandler(); using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Send(new(client), Property(new ChatRequest(Config(mode), [new("user", "hello")]), "Purpose", purpose)));
        Assert.Empty(handler.Bodies);
    }
}
