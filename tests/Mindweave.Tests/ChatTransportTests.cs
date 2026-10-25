using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;
using Mindweave.Infrastructure.AI;
using Xunit;

namespace Mindweave.Tests;

public sealed class ChatTransportTests
{
    private static AiConfiguration Config(string url = "https://supplier.invalid/v1/") =>
        new(Guid.NewGuid(), Guid.NewGuid(), "test", url, "owned-reference", "configured-model", 32768, 2048);
    private const string Success = "data: {\"model\":\"actual-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"你好\",\"reasoning_content\":\"hidden\",\"tool_calls\":[{}]},\"finish_reason\":null}]}\r\n\r\ndata: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"total_tokens\":5}}\r\n\r\ndata: {\"choices\":[],\"usage\":{\"total_tokens\":5}}\r\n\r\ndata: [DONE]\r\n\r\n";

    [Theory]
    [InlineData("max_tokens")]
    [InlineData("max_completion_tokens")]
    public async Task StreamsUtf8ContentAndPostsExactConfiguredPayloadOnce(string parameter)
    {
        var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://supplier.invalid/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-token", request.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("configured-model", json.RootElement.GetProperty("model").GetString());
            Assert.True(json.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal(2048, json.RootElement.GetProperty(parameter).GetInt32());
            Assert.False(json.RootElement.TryGetProperty(parameter == "max_tokens" ? "max_completion_tokens" : "max_tokens", out _));
            Assert.Equal("user", json.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
            Assert.Equal("问候", json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            return Response(": comment\r\n\r\n" + Success);
        });
        var events = await Collect(new(new HttpClient(handler)), Config() with { TokenLimitParameter = parameter });
        Assert.Equal("你好", string.Concat(events.Select(x => x.Text)));
        Assert.Contains(events, x => x.FinishReason == "stop");
        Assert.Contains(events, x => x.ActualModelId == "actual-model");
        Assert.Equal(1, handler.Calls);
        Assert.True(handler.LastStream!.Disposed);
    }

    [Fact]
    public async Task AcceptsMultilineSseAndEofAfterExplicitStop()
    {
        var handler = new Handler(_ => Task.FromResult(Response("data: {\"choices\":\r\ndata: [{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\r\n\r\n")));
        Assert.Equal("ok", string.Concat((await Collect(new(new HttpClient(handler)), Config())).Select(x => x.Text)));
    }

    [Theory]
    [InlineData("data: [DONE]\n\n", "EmptyResponse")]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n", "IncompleteResponse")]
    [InlineData("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", "EmptyResponse")]
    [InlineData("data: malformed-private-body\n\n", "InvalidResponse")]
    [InlineData("data: {\"error\":{\"message\":\"private-body\"}}\n\n", "ServiceError")]
    [InlineData("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"hidden\"},\"finish_reason\":\"stop\"}]}\n\n", "EmptyResponse")]
    public async Task NeverTreatsMalformedEmptyOrUnfinishedStreamsAsSuccess(string stream, string error)
    {
        var handler = new Handler(_ => Task.FromResult(Response(stream)));
        var exception = await Assert.ThrowsAsync<ChatTransportException>(() => Collect(new(new HttpClient(handler)), Config()));
        Assert.Equal(error, exception.ErrorCode);
        Assert.DoesNotContain("private-body", exception.ToString());
        Assert.Equal(1, handler.Calls);
        Assert.True(handler.LastStream!.Disposed);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    [InlineData("tool_calls")]
    [InlineData("unknown")]
    public async Task FailedFinishPreservesTextAndReason(string reason)
    {
        var stream = "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":\"" + reason + "\"}]}\n\n";
        var transport = new ChatCompletionsTransport(new HttpClient(new Handler(_ => Task.FromResult(Response(stream)))));
        var received = new List<ChatStreamEvent>();
        await Assert.ThrowsAsync<ChatTransportException>(async () =>
        {
            await foreach (var item in transport.StreamAsync(new(Config(), [new("user", "question")]), "synthetic-token", default)) received.Add(item);
        });
        Assert.Equal("partial", string.Concat(received.Select(x => x.Text)));
        Assert.Contains(received, x => x.FinishReason == reason);
    }

    [Theory]
    [InlineData(302, "RedirectRejected")]
    [InlineData(401, "ServiceError")]
    [InlineData(429, "ServiceError")]
    [InlineData(500, "ServiceError")]
    public async Task RejectsHttpErrorsWithoutRetryOrLeakingErrorBody(int status, string error)
    {
        var handler = new Handler(_ => Task.FromResult(Response("private-body", (HttpStatusCode)status)));
        var exception = await Assert.ThrowsAsync<ChatTransportException>(() => Collect(new(new HttpClient(handler)), Config()));
        Assert.Equal(error, exception.ErrorCode);
        Assert.DoesNotContain("private-body", exception.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("http://remote.invalid/v1")]
    [InlineData("https://user:secret@remote.invalid")]
    [InlineData("https://remote.invalid?token=private")]
    [InlineData("https://remote.invalid/#private")]
    [InlineData("file:///tmp")]
    public async Task InvalidEndpointNeverSendsRequest(string url)
    {
        var handler = new Handler(_ => Task.FromResult(Response(Success)));
        await Assert.ThrowsAsync<ArgumentException>(() => Collect(new(new HttpClient(handler)), Config(url)));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ModelsAreExplicitGetWithNoGenerationAndOwnBearer()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("http://127.0.0.1:1234/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("synthetic-token", request.Headers.Authorization!.Parameter);
            Assert.Null(request.Content);
            return Task.FromResult(Response("{\"data\":[{\"id\":\"b\"},{\"id\":\"a\"},{\"id\":\"b\"}]}", mediaType: "application/json"));
        });
        var models = await new ChatCompletionsTransport(new HttpClient(handler)).ListModelsAsync(Config("http://127.0.0.1:1234/v1"), "synthetic-token", default);
        Assert.Equal(["b", "a"], models);
        Assert.Equal(1, handler.Calls);
        Assert.True(handler.LastStream!.Disposed);
    }

    [Fact]
    public async Task EnforcesEventSizeBeforeReceivingUnboundedLine()
    {
        var handler = new Handler(_ => Task.FromResult(Response("data: " + new string('x', 300_000))));
        Assert.Equal("ResponseTooLarge", (await Assert.ThrowsAsync<ChatTransportException>(() => Collect(new(new HttpClient(handler)), Config()))).ErrorCode);
    }

    [Fact]
    public async Task TimeoutCoversBodyReadAndDisposesResponse()
    {
        var stream = new BlockingStream();
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        var transport = new ChatCompletionsTransport(new HttpClient(handler), TimeSpan.FromMilliseconds(80));
        Assert.Equal("Timeout", (await Assert.ThrowsAsync<ChatTransportException>(() => Collect(transport, Config()))).ErrorCode);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellationDuringBodyRead()
    {
        var stream = new BlockingStream();
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        using var cancellation = new CancellationTokenSource(80);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(new(new HttpClient(handler)), Config(), cancellation.Token));
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task FactoryNeverFollowsRealLoopbackRedirect()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        destination.Start();
        var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            Assert.Equal("POST /v1/chat/completions HTTP/1.1", await reader.ReadLineAsync(deadline.Token));
            var headers = new List<string>();
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(deadline.Token))) headers.Add(line);
            Assert.Contains("Authorization: Bearer synthetic-token", headers);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{destinationPort}/stolen\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), deadline.Token);
        });
        using var client = ChatCompletionsTransport.CreateHttpClient();
        var exception = await Assert.ThrowsAsync<ChatTransportException>(() => Collect(new(client), Config($"http://127.0.0.1:{port}/v1"), deadline.Token));
        Assert.Equal("RedirectRejected", exception.ErrorCode);
        await server;
        Assert.False(destination.Pending());
    }

    [Fact]
    public async Task EarlyConsumerDisposalClosesResponse()
    {
        var handler = new Handler(_ => Task.FromResult(Response(Success)));
        var events = new ChatCompletionsTransport(new HttpClient(handler)).StreamAsync(new(Config(), [new("user", "hello")]), "synthetic-token", default).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        await events.DisposeAsync();
        Assert.True(handler.LastStream!.Disposed);
    }

    [Theory]
    [InlineData("max_new_tokens")]
    [InlineData("")]
    public async Task InvalidTokenParameterNeverSendsRequest(string parameter)
    {
        var handler = new Handler(_ => Task.FromResult(Response(Success)));
        await Assert.ThrowsAsync<ArgumentException>(() => Collect(new(new HttpClient(handler)), Config() with { TokenLimitParameter = parameter }));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(307)]
    [InlineData(401)]
    public async Task ModelsRejectErrorsWithoutGenerationOrRetry(int status)
    {
        var handler = new Handler(_ => Task.FromResult(Response("private-body", (HttpStatusCode)status)));
        var exception = await Assert.ThrowsAsync<ChatTransportException>(() => new ChatCompletionsTransport(new HttpClient(handler)).ListModelsAsync(Config(), "synthetic-token", default));
        Assert.Equal(status == 307 ? "RedirectRejected" : "ServiceError", exception.ErrorCode);
        Assert.DoesNotContain("private-body", exception.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":[{}]}")]
    [InlineData("{\"data\":[{\"id\":null}]}")]
    public async Task ModelsRejectMalformedCatalog(string catalog)
    {
        var handler = new Handler(_ => Task.FromResult(Response(catalog)));
        Assert.Equal("InvalidResponse", (await Assert.ThrowsAsync<ChatTransportException>(() => new ChatCompletionsTransport(new HttpClient(handler)).ListModelsAsync(Config(), "synthetic-token", default))).ErrorCode);
        Assert.True(handler.LastStream!.Disposed);
    }

    [Fact]
    public async Task ModelsBoundBodySize()
    {
        var handler = new Handler(_ => Task.FromResult(Response(new string(' ', 1024 * 1024 + 1))));
        Assert.Equal("ResponseTooLarge", (await Assert.ThrowsAsync<ChatTransportException>(() => new ChatCompletionsTransport(new HttpClient(handler)).ListModelsAsync(Config(), "synthetic-token", default))).ErrorCode);
    }

    [Fact]
    public async Task TransportFailureDoesNotLeakHandlerExceptionOrRetry()
    {
        var handler = new Handler(_ => throw new HttpRequestException("private-token-and-body"));
        var exception = await Assert.ThrowsAsync<ChatTransportException>(() => Collect(new(new HttpClient(handler)), Config()));
        Assert.Equal("TransportError", exception.ErrorCode);
        Assert.DoesNotContain("private-token-and-body", exception.ToString());
        Assert.Null(exception.InnerException);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("private-secret\0")]
    [InlineData("private-secret with space")]
    [InlineData("private-secret\r\nHeader: value")]
    public async Task InvalidBearerRejectedSafelyBeforeHttp(string secret)
    {
        var handler = new Handler(_ => Task.FromResult(Response(Success)));
        var transport = new ChatCompletionsTransport(new HttpClient(handler));
        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var item in transport.StreamAsync(new(Config(), [new("user", "question")]), secret, default)) _ = item;
        });
        Assert.DoesNotContain("private-secret", exception.ToString());
        Assert.Equal(0, handler.Calls);
    }

    private static async Task<List<ChatStreamEvent>> Collect(ChatCompletionsTransport transport, AiConfiguration config, CancellationToken token = default)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var item in transport.StreamAsync(new(config, [new("user", "问候")]), "synthetic-token", token)) events.Add(item);
        return events;
    }
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "text/event-stream")
    {
        var response = new HttpResponseMessage(status) { Content = new TrackingContent(new ByteStream(Encoding.UTF8.GetBytes(body))) };
        response.Content.Headers.ContentType = new(mediaType);
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public ByteStream? LastStream { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = await send(request);
            LastStream = (response.Content as TrackingContent)?.Original;
            return response;
        }
    }
    private sealed class TrackingContent(ByteStream stream) : StreamContent(stream)
    {
        public ByteStream Original { get; } = stream;
    }
    private sealed class ByteStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class BlockingStream : Stream
    {
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
