using System.Globalization;
using System.Text.Json;
using Mindweave.Application.Exchange;
using Mindweave.Application.Import;
using Mindweave.Core.Conversations;

namespace Mindweave.Infrastructure.Import;

/// <summary>
/// Reads the exchange document out of a shared file (architecture §16.4): the bytes of its data block and nothing else. No
/// part of the page is run or drawn. Fields this version does not know are passed over, as a format version allows.
/// </summary>
public sealed class SharedFileReader : ISharedFileReader
{
    private static ReadOnlySpan<byte> BlockName => "id=\"mindweave-export\""u8;

    public async Task<SharedFileReading> ReadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] bytes;
        try
        {
            // The length is taken from the open file, and no more than the limit is ever read.
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            if (file.Length > ImportLimits.MaxFileBytes)
            {
                return SharedFileReading.Refused(ImportRefusal.TooLarge);
            }

            bytes = new byte[file.Length];
            var read = await file.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            bytes = bytes[..read];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return SharedFileReading.Refused(ImportRefusal.Unreadable);
        }

        return Read(bytes);
    }

    private static SharedFileReading Read(byte[] bytes)
    {
        if (!TryFindBlock(bytes, out var from, out var to))
        {
            return SharedFileReading.Refused(ImportRefusal.NotAShare);
        }

        if (to < 0)
        {
            return SharedFileReading.Refused(ImportRefusal.Damaged);
        }

        try
        {
            using var parsed = JsonDocument.Parse(bytes.AsMemory(from, to - from));
            return Read(parsed.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            // A missing field, a value of another type, or text that is no text.
            return SharedFileReading.Refused(ImportRefusal.Damaged);
        }
    }

    /// <summary>The bytes between the data block's tags; <paramref name="to"/> is -1 when it does not end.</summary>
    private static bool TryFindBlock(ReadOnlySpan<byte> bytes, out int from, out int to)
    {
        from = to = -1;
        var start = 0;
        for (int at; (at = bytes[start..].IndexOf(BlockName)) >= 0; start += at + BlockName.Length)
        {
            var name = start + at;
            var tag = bytes[..name].LastIndexOf((byte)'<');
            if (tag < 0 || !bytes[tag..name].StartsWith("<script "u8) || bytes[tag..name].Contains((byte)'>'))
            {
                continue;
            }

            var opened = bytes[name..].IndexOf((byte)'>');
            if (opened < 0)
            {
                return true;
            }

            from = name + opened + 1;
            var length = bytes[from..].IndexOf("</script"u8);
            to = length < 0 ? -1 : from + length;
            return true;
        }

        return false;
    }

    private static SharedFileReading Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != ExchangeDocument.Format)
        {
            return SharedFileReading.Refused(ImportRefusal.NotAShare);
        }

        var version = root.GetProperty("version");
        if (version.GetInt64() is var number && number > ExchangeDocument.Version)
        {
            return SharedFileReading.Refused(ImportRefusal.NewerFormat);
        }

        if (number < 1)
        {
            return SharedFileReading.Refused(ImportRefusal.Damaged);
        }

        switch (root.GetProperty("kind").GetString())
        {
            case ExchangeDocument.StructureKind:
                break;
            case ExchangeDocument.ContentKind:
                return SharedFileReading.Refused(ImportRefusal.WithContent);
            case ExchangeDocument.DeviceKind:
                return SharedFileReading.Refused(ImportRefusal.DeviceCopy);
            default:
                return SharedFileReading.Refused(ImportRefusal.Damaged);
        }

        var conversations = root.GetProperty("conversations");
        if (conversations.GetArrayLength() > ImportLimits.MaxConversations || root.GetProperty("links").GetArrayLength() > ImportLimits.MaxLinks)
        {
            return SharedFileReading.Refused(ImportRefusal.TooLarge);
        }

        var document = new ExchangeDocument(
            ExchangeDocument.StructureKind,
            Name(root.GetProperty("title")),
            Instant(root.GetProperty("made_at")),
            Name(root.GetProperty("app_version")),
            [.. conversations.EnumerateArray().Select(Conversation)],
            [.. root.GetProperty("links").EnumerateArray().Select(l => new ExchangeLink(Identity(l.GetProperty("a")), Identity(l.GetProperty("b"))))]);
        return ImportRules.IsSound(document) ? SharedFileReading.Of(document) : SharedFileReading.Refused(ImportRefusal.Damaged);
    }

    private static ExchangeConversation Conversation(JsonElement conversation)
    {
        if (conversation.TryGetProperty("messages", out _) || conversation.TryGetProperty("source", out _) || conversation.TryGetProperty("summary", out _))
        {
            throw new FormatException("A structure-only share holds no content.");
        }

        var questions = conversation.GetProperty("questions").GetInt32();
        return new ExchangeConversation(
            Identity(conversation.GetProperty("id")),
            conversation.TryGetProperty("parent", out var parent) ? Identity(parent) : null,
            conversation.TryGetProperty("name", out var name) ? Name(name) : null,
            Instant(conversation.GetProperty("started_at")),
            questions < 0 ? throw new FormatException("A number of questions is never negative.") : questions,
            conversation.GetProperty("has_summary").GetBoolean());
    }

    private static ConversationId Identity(JsonElement value)
    {
        var id = Guid.ParseExact(Text(value), "D");
        return id == Guid.Empty ? throw new FormatException("An identity is never empty.") : new ConversationId(id);
    }

    private static string Name(JsonElement value)
    {
        var text = Text(value);
        return text.Length > ImportLimits.MaxNameLength ? throw new FormatException("A name is too long.") : text;
    }

    private static string Text(JsonElement value) => value.GetString() ?? throw new FormatException("A text is missing.");

    private static DateTimeOffset Instant(JsonElement value)
    {
        var at = DateTimeOffset.Parse(Text(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return at.Year is >= ImportLimits.FirstYear and <= ImportLimits.LastYear ? at : throw new FormatException("A time is out of range.");
    }
}
