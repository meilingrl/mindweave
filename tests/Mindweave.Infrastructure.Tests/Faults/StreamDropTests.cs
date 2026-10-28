using System.Net;
using System.Text;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Ai;
using Mindweave.Infrastructure.Tests.Chat;

namespace Mindweave.Infrastructure.Tests.Faults;

/// <summary>
/// The connection drops in the middle of an answer's body, at the HTTP level, after some text arrived: through the
/// real client, coordinator and stores, the answer keeps what arrived, fails as a network error and can be retried.
/// </summary>
public class StreamDropTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Drops => ["io", "http-io"];

    [Theory]
    [MemberData(nameof(Drops))]
    public async Task A_body_cut_after_some_text_keeps_the_text_fails_as_network_and_offers_retry(string drop)
    {
        var handler = new DroppingHandler(drop);
        using var http = new HttpClient(handler);
        await using var h = await ChatHarness.StartAsync(client: new ChatCompletionsClient(http, new ChatClientOptions()));

        var sent = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal(MessageState.Failed, answer.State);
        Assert.Equal(GenerationErrorKind.Network, answer.ErrorKind);
        Assert.Equal("蒸汽机车最早", answer.Text);
        Assert.Equal("failed", await h.TaskStateAsync(sent.Answer!.Value));
        var history = (await h.Conversations.LoadAsync(sent.Conversation, Ct))!.Messages;
        Assert.True(ConversationRules.CanRetry(history, answer.Id));
        Assert.False(h.Conversations.IsStorageBlocked);

        handler.Drop = null;
        var retry = await h.Conversations.RetryAsync(sent.Conversation, answer.Id, Ct);
        Assert.True(retry.Accepted, retry.Rejection.ToString());
        var second = await h.WaitForAnswerAsync(retry.Answer!.Value);
        Assert.Equal(MessageState.Complete, second.State);
        Assert.Equal("蒸汽机车最早出现在英国。", second.Text);

        // The failed attempt stays readable next to the new one.
        Assert.Equal("蒸汽机车最早", (await h.GetMessageAsync(answer.Id))!.Text);
    }

    /// <summary>Answers with SSE chunks; while <see cref="Drop"/> is set, the body breaks after the first two chunks.</summary>
    private sealed class DroppingHandler(string? drop) : HttpMessageHandler
    {
        private static readonly string[] Chunks =
        [
            "data: {\"choices\":[{\"delta\":{\"content\":\"蒸汽机车\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"最早\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"出现在英国。\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
        ];

        public string? Drop { get; set; } = drop;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") });
            }

            Exception? failure = Drop switch
            {
                "io" => new IOException("Unable to read data from the transport connection: Connection reset by peer."),
                "http-io" => new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."),
                _ => null,
            };
            var chunks = failure is null ? Chunks : Chunks[..2];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BreakingStream(chunks.Select(Encoding.UTF8.GetBytes).ToArray(), failure)),
            });
        }
    }

    /// <summary>Returns one chunk per read, then throws <paramref name="failure"/> (or ends when it is null).</summary>
    private sealed class BreakingStream(byte[][] chunks, Exception? failure) : Stream
    {
        private int _next;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (_next < chunks.Length)
            {
                var chunk = chunks[_next++];
                chunk.CopyTo(buffer);
                return chunk.Length;
            }

            return failure is null ? 0 : throw failure;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
