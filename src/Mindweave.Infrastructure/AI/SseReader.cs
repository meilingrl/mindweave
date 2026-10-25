using System.Runtime.CompilerServices;
using System.Text;
using Mindweave.Application.Chat;

namespace Mindweave.Infrastructure.AI;

internal static class SseReader
{
    // Bounds apply to both an unfinished line and the joined event, before JSON allocation.
    private const int MaximumEventCharacters = 256 * 1024;

    public static async IAsyncEnumerable<string> ReadAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 1024, leaveOpen: true);
        var buffer = new char[1024];
        var line = new StringBuilder();
        var data = new StringBuilder();
        var afterCarriageReturn = false;
        var firstCharacter = true;
        while (true)
        {
            var count = await reader.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            for (var i = 0; i < count; i++)
            {
                var character = buffer[i];
                if (firstCharacter)
                {
                    firstCharacter = false;
                    if (character == '\uFEFF') continue;
                }
                if (character == '\n' && afterCarriageReturn) { afterCarriageReturn = false; continue; }
                afterCarriageReturn = character == '\r';
                if (character is '\r' or '\n')
                {
                    if (line.Length == 0)
                    {
                        if (data.Length > 0)
                        {
                            yield return data.ToString(0, data.Length - 1);
                            data.Clear();
                        }
                    }
                    else
                    {
                        if (line.ToString().StartsWith("data:", StringComparison.Ordinal))
                        {
                            var value = line.ToString(5, line.Length - 5);
                            if (value.StartsWith(' ')) value = value[1..];
                            if (data.Length + value.Length + 1 > MaximumEventCharacters) throw new ChatTransportException("ResponseTooLarge");
                            data.Append(value).Append('\n');
                        }
                        line.Clear();
                    }
                }
                else
                {
                    if (line.Length >= MaximumEventCharacters) throw new ChatTransportException("ResponseTooLarge");
                    line.Append(character);
                }
            }
        }
        // SSE only dispatches events terminated by a blank line; partial EOF is not a message.
    }
}
