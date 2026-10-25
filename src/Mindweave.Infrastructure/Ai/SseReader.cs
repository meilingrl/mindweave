using System.Text;

namespace Mindweave.Infrastructure.Ai;

/// <summary>
/// Minimal Server-Sent Events reader: joins multi-line <c>data:</c> fields, dispatches on a blank line,
/// ignores comments and other fields, handles LF and CRLF, and flushes a final event at end of stream.
/// </summary>
internal sealed class SseReader(Stream stream) : IDisposable
{
    private readonly StreamReader _reader = new(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
    private readonly StringBuilder _data = new();

    /// <summary>The next event's data, or null at end of stream.</summary>
    public async Task<string?> ReadEventAsync(CancellationToken cancellationToken)
    {
        var hasData = false;
        while (true)
        {
            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return hasData ? Take() : null;
            }

            if (line.Length == 0)
            {
                if (hasData)
                {
                    return Take();
                }

                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = colon < 0 ? line : line[..colon];
            if (field != "data")
            {
                continue;
            }

            var value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            if (hasData)
            {
                _data.Append('\n');
            }

            _data.Append(value);
            hasData = true;
        }
    }

    public void Dispose() => _reader.Dispose();

    private string Take()
    {
        var value = _data.ToString();
        _data.Clear();
        return value;
    }
}
