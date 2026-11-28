using System.Text;

namespace Mindweave.Infrastructure.Ai;

/// <summary>
/// Minimal Server-Sent Events reader: joins multi-line <c>data:</c> fields, dispatches on a blank line,
/// ignores comments and other fields, handles LF, CRLF and CR, and flushes a final event at end of stream. Lines are read
/// here, not by <see cref="StreamReader.ReadLineAsync(CancellationToken)"/>, which reads a line without a break with no
/// limit (low finding security-4, D2 §2.5): a line longer than any event fails at once.
/// </summary>
internal sealed class SseReader(Stream stream) : IDisposable
{
    private readonly StreamReader _reader = new(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
    private readonly StringBuilder _data = new();
    private readonly StringBuilder _line = new();
    private readonly char[] _buffer = new char[4096];
    private int _start;
    private int _end;

    /// <summary>The last line ended with CR: an LF right after it belongs to the same break.</summary>
    private bool _afterCarriageReturn;

    /// <summary>The largest event accepted, in characters.</summary>
    internal const int MaxEventChars = 8 * 1024 * 1024;

    /// <summary>Called for every line read, comments included: a keep-alive comment shows the service is alive.</summary>
    public Action? LineRead { get; set; }

    /// <summary>The next event's data, or null at end of stream.</summary>
    public async Task<string?> ReadEventAsync(CancellationToken cancellationToken)
    {
        var hasData = false;
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is not null)
            {
                LineRead?.Invoke();
            }

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
            if (_data.Length > MaxEventChars)
            {
                // A runaway event would grow without bound (low finding security-4); no real event comes near this.
                throw new InvalidDataException("A stream event is larger than any answer could be.");
            }
        }
    }

    public void Dispose() => _reader.Dispose();

    /// <summary>The next line without its break, or null at end of stream; throws once a line is longer than any event.</summary>
    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        _line.Clear();
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (_end == 0)
                {
                    return _line.Length > 0 ? _line.ToString() : null;
                }
            }

            if (_afterCarriageReturn)
            {
                _afterCarriageReturn = false;
                if (_buffer[_start] == '\n')
                {
                    _start++;
                    continue;
                }
            }

            for (var i = _start; i < _end; i++)
            {
                if (_buffer[i] is '\n' or '\r')
                {
                    _line.Append(_buffer, _start, i - _start);
                    _afterCarriageReturn = _buffer[i] == '\r';
                    _start = i + 1;
                    return _line.ToString();
                }
            }

            _line.Append(_buffer, _start, _end - _start);
            _start = _end;
            if (_line.Length > MaxEventChars)
            {
                throw new InvalidDataException("A stream line is longer than any answer could be.");
            }
        }
    }

    private string Take()
    {
        var value = _data.ToString();
        _data.Clear();
        return value;
    }
}
