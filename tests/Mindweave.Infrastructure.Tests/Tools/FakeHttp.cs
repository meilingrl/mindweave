using System.Net;
using System.Text;

namespace Mindweave.Infrastructure.Tests.Tools;

/// <summary>One request as the fake handler saw it.</summary>
internal sealed record SeenRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>An HttpMessageHandler that answers from a function and records every request; nothing touches the network.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    public List<SeenRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.NonValidated.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new SeenRequest(request.Method, request.RequestUri!, headers, body));
        return await respond(request, cancellationToken);
    }
}

internal static class Responses
{
    public static HttpResponseMessage Text(string text, string mediaType = "text/html", HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, mediaType) };

    public static HttpResponseMessage Bytes(byte[] bytes, string? contentType)
    {
        var content = new ByteArrayContent(bytes);
        if (contentType is not null)
        {
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    public static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }
}

/// <summary>A body that yields <paramref name="chunk"/> bytes per read, <paramref name="total"/> in all, pausing before each read.</summary>
internal sealed class SlowStream(long total, int chunk, TimeSpan pause) : Stream
{
    private long _sent;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_sent >= total)
        {
            return 0;
        }

        if (pause > TimeSpan.Zero)
        {
            await Task.Delay(pause, cancellationToken);
        }

        var count = (int)Math.Min(Math.Min(buffer.Length, chunk), total - _sent);
        buffer.Span[..count].Fill((byte)'a');
        _sent += count;
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
