using System.Globalization;
using System.Text.Json;
using Mindweave.Application.Exchange;
using Mindweave.Application.Import;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;

namespace Mindweave.Infrastructure.Import;

/// <summary>
/// Reads the exchange document out of a shared file (architecture §16.4): the bytes of its data block and nothing else. No
/// part of the page is run or drawn. Fields this version does not know are passed over, as a format version allows.
/// </summary>
public sealed class SharedFileReader : ISharedFileReader
{
    private static ReadOnlySpan<byte> BlockName => "id=\"mindweave-export\""u8;

    /// <summary>How far before its name the block's tag may begin; a file full of names is then still read through once.</summary>
    private const int LongestTag = 256;

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
            var near = Math.Max(0, name - LongestTag);
            var tag = near + bytes[near..name].LastIndexOf((byte)'<');
            if (tag < near || !bytes[tag..name].StartsWith("<script "u8) || bytes[tag..name].Contains((byte)'>'))
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

        var kind = root.GetProperty("kind").GetString();
        switch (kind)
        {
            case ExchangeDocument.StructureKind or ExchangeDocument.ContentKind:
                break;
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

        var withContent = kind == ExchangeDocument.ContentKind;
        if (withContent && conversations.EnumerateArray().Sum(c => (long)c.GetProperty("messages").GetArrayLength()) > ImportLimits.MaxMessages)
        {
            return SharedFileReading.Refused(ImportRefusal.TooLarge);
        }

        var document = new ExchangeDocument(
            kind,
            Text(root.GetProperty("title")),
            Instant(root.GetProperty("made_at")),
            Name(root.GetProperty("app_version")),
            [.. conversations.EnumerateArray().Select(c => Conversation(c, withContent))],
            [.. root.GetProperty("links").EnumerateArray().Select(l => new ExchangeLink(Identity(l.GetProperty("a")), Identity(l.GetProperty("b")))
            {
                Note = withContent && l.TryGetProperty("note", out var note) ? Name(note) : null,
            })]);
        return ImportRules.IsSound(document) ? SharedFileReading.Of(document) : SharedFileReading.Refused(ImportRefusal.Damaged);
    }

    private static ExchangeConversation Conversation(JsonElement conversation, bool withContent)
    {
        if (!withContent && (conversation.TryGetProperty("messages", out _) || conversation.TryGetProperty("source", out _) || conversation.TryGetProperty("summary", out _)))
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
            conversation.GetProperty("has_summary").GetBoolean())
        {
            Content = withContent ? Content(conversation) : null,
        };
    }

    private static ExchangeContent Content(JsonElement conversation) => new(
        [.. conversation.GetProperty("messages").EnumerateArray().Select(Message)],
        conversation.TryGetProperty("source", out var source) ? Source(source) : null,
        conversation.TryGetProperty("summary", out var summary) ? Summary(summary) : null);

    private static ExchangeMessage Message(JsonElement message) =>
        new(
            new MessageId(Guid(message.GetProperty("id"))),
            Number(message.GetProperty("seq")),
            Text(message.GetProperty("role")) switch
            {
                "question" => MessageRole.User,
                "answer" => MessageRole.Assistant,
                _ => throw new FormatException("A message is a question or an answer."),
            },
            Instant(message.GetProperty("at")),
            Text(message.GetProperty("state")) switch
            {
                "complete" => MessageState.Complete,
                "stopped" => MessageState.Stopped,
                "interrupted" => MessageState.Interrupted,
                "failed" => MessageState.Failed,
                _ => throw new FormatException("A shared message is finished and has text."),
            },
            Body(message.GetProperty("text")))
        {
            Attachments = message.TryGetProperty("attachments", out var attachments)
                ? attachments.GetArrayLength() > Application.Resources.ResourceLimits.MaxAttachmentsPerMessage
                    ? throw new FormatException("A question has too many attachments.")
                    : [.. attachments.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.Null ? null : ImportRules.OneLine(Name(a)) is { Length: > 0 } name ? name : null)]
                : [],
            Activity = message.TryGetProperty("activity", out var activity) ? Activity(activity) : null,
            Citations = message.TryGetProperty("citations", out var citations) ? [.. citations.EnumerateArray().Select(Citation)] : [],
            ReplacedBy = message.TryGetProperty("replaced_by", out var replacedBy) ? new MessageId(Guid(replacedBy)) : null,
        };

    /// <summary>Counts of nothing are none.</summary>
    private static ExchangeActivity? Activity(JsonElement activity)
    {
        var counts = new ExchangeActivity(
            Number(activity.GetProperty("attachment_searches")),
            Number(activity.GetProperty("summary_searches")),
            Number(activity.GetProperty("web_searches")),
            Number(activity.GetProperty("pages_read")),
            Number(activity.GetProperty("other_calls")));
        return counts == new ExchangeActivity(0, 0, 0, 0, 0) ? null : counts;
    }

    /// <summary>A title is kept on one line, cut where it is longer than any name; only a web page's address is one to open.</summary>
    private static ExchangeCitation Citation(JsonElement citation)
    {
        var kind = Text(citation.GetProperty("kind")) switch
        {
            "web" => CitationKind.Web,
            "attachment" => CitationKind.Attachment,
            "summary" => CitationKind.Summary,
            "material" => CitationKind.Material,
            _ => throw new FormatException("A citation is of something else."),
        };
        var number = Number(citation.GetProperty("number"));
        return new ExchangeCitation(
            number is < 1 or > ImportLimits.MaxCitation ? throw new FormatException("A citation's number is out of range.") : number,
            kind,
            citation.TryGetProperty("title", out var title) ? ConversationRules.Shorten(Text(title), ImportLimits.MaxNameLength) : null,
            kind == CitationKind.Web && citation.TryGetProperty("url", out var url) ? Address(url) : null,
            citation.TryGetProperty("conversation", out var conversation) ? Identity(conversation) : null);
    }

    /// <summary>An address of the web that is still one written out, as it is stored and read back.</summary>
    private static Uri Address(JsonElement value)
    {
        var text = Text(value);
        return text.Length <= ImportLimits.MaxAddressLength && Uri.TryCreate(text, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
            && url.Host.Length > 0 && Uri.TryCreate(url.AbsoluteUri, UriKind.Absolute, out var again) && again.AbsoluteUri == url.AbsoluteUri
            ? again
            : throw new FormatException("An address is none of the web's.");
    }

    /// <summary>A place and an anchor are places of a sentence; without one they mean nothing.</summary>
    private static ExchangeBranchSource Source(JsonElement source)
    {
        var message = new MessageId(Guid(source.GetProperty("message")));
        if (!source.TryGetProperty("quote", out var quoted))
        {
            return new ExchangeBranchSource(message, null, null, null);
        }

        var quote = Text(quoted);
        return new ExchangeBranchSource(
            message,
            string.IsNullOrWhiteSpace(quote) || quote.Length > BranchRules.MaxQuoteLength ? throw new FormatException("A quote is empty or too long.") : quote,
            source.TryGetProperty("position", out var position) ? new ExchangePosition(Number(position.GetProperty("block")), Number(position.GetProperty("offset"), ImportLimits.MaxTextLength)) : null,
            source.TryGetProperty("anchor", out var anchor) ? Anchor(anchor) : null);
    }

    private static ExchangeAnchor Anchor(JsonElement anchor)
    {
        var data = Text(anchor.GetProperty("data"));
        return new ExchangeAnchor(
            Number(anchor.GetProperty("version")),
            Name(anchor.GetProperty("profile")),
            data.Length > BranchRules.MaxAnchorLength ? throw new FormatException("An anchor is too long.") : data);
    }

    private static ExchangeSummary Summary(JsonElement summary)
    {
        var (title, body) = (ImportRules.OneLine(Name(summary.GetProperty("title"))), Body(summary.GetProperty("body")));
        return new ExchangeSummary(
            new SummaryVersionId(Guid(summary.GetProperty("id"))),
            title.Length == 0 ? throw new FormatException("A summary has a title.") : title,
            string.IsNullOrWhiteSpace(body) ? throw new FormatException("A summary has a text.") : body,
            Instant(summary.GetProperty("at")),
            Number(summary.GetProperty("to_seq")));
    }

    private static int Number(JsonElement value, int most = ImportLimits.MaxNumber) =>
        value.GetInt32() is var number && number >= 0 && number <= most ? number : throw new FormatException("A number is out of range.");

    private static string Body(JsonElement value)
    {
        var text = Text(value);
        return text.Length > ImportLimits.MaxTextLength ? throw new FormatException("A text is too long.") : text;
    }

    private static ConversationId Identity(JsonElement value) => new(Guid(value));

    private static Guid Guid(JsonElement value)
    {
        var id = System.Guid.ParseExact(Text(value), "D");
        return id == System.Guid.Empty ? throw new FormatException("An identity is never empty.") : id;
    }

    private static string Name(JsonElement value)
    {
        var text = Text(value);
        return text.Length > ImportLimits.MaxNameLength ? throw new FormatException("A name is too long.") : text;
    }

    /// <summary>A text as Markdown reads it: without the NUL character, which would also end it for the database's own checks.</summary>
    private static string Text(JsonElement value) => value.GetString()?.Replace('\0', '\uFFFD') ?? throw new FormatException("A text is missing.");

    private static DateTimeOffset Instant(JsonElement value)
    {
        var at = DateTimeOffset.Parse(Text(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return at.Year is >= ImportLimits.FirstYear and <= ImportLimits.LastYear ? at : throw new FormatException("A time is out of range.");
    }
}
