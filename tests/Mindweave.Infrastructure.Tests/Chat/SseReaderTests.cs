using System.Text;
using Mindweave.Infrastructure.Ai;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>How the stream is cut into lines and events; low finding security-4 (D2 §2.5).</summary>
public class SseReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("data: a\n\ndata: b\n\n")]
    [InlineData("data: a\r\n\r\ndata: b\r\n\r\n")]
    [InlineData("data: a\r\rdata: b\r\r")]
    [InlineData("data: a\r\n\ndata: b\n\r\n")]
    [InlineData(": keep-alive\ndata: a\n\nevent: x\ndata: b")]
    public async Task Every_line_break_ends_a_line(string stream)
    {
        using var reader = new SseReader(new MemoryStream(Encoding.UTF8.GetBytes(stream)));

        Assert.Equal("a", await reader.ReadEventAsync(Ct));
        Assert.Equal("b", await reader.ReadEventAsync(Ct));
        Assert.Null(await reader.ReadEventAsync(Ct));
    }

    [Fact]
    public async Task Lines_longer_than_the_buffer_and_data_over_several_lines_are_joined()
    {
        var long1 = new string('x', 10_000);
        var long2 = new string('火', 9_000);
        using var reader = new SseReader(new MemoryStream(Encoding.UTF8.GetBytes($"data: {long1}\r\ndata:{long2}\r\n\r\n")));

        Assert.Equal(long1 + "\n" + long2, await reader.ReadEventAsync(Ct));
        Assert.Null(await reader.ReadEventAsync(Ct));
    }

    /// <summary>A line that never breaks used to be read without limit inside the stream reader; now it fails once past the limit.</summary>
    [Fact]
    public async Task A_line_that_never_ends_fails_once_it_is_longer_than_any_event()
    {
        using var endless = new EndlessLine();
        using var reader = new SseReader(endless);

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadEventAsync(Ct));
        Assert.InRange(endless.Served, SseReader.MaxEventChars, SseReader.MaxEventChars + 64 * 1024);
    }
}

/// <summary>"data: " and then letters for ever, never a line break.</summary>
internal sealed class EndlessLine : Stream
{
    private static readonly byte[] Start = Encoding.ASCII.GetBytes("data: ");

    public long Served { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        for (var i = 0; i < count; i++)
        {
            buffer[offset + i] = Served < Start.Length ? Start[Served] : (byte)'x';
            Served++;
        }

        return count;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
