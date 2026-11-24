using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Share;

/// <summary>
/// The one HTML file a share is (docs/design/sharing.md S2, §6.2): the page kept in <c>SharePage.html</c> with the
/// exchange document in its data block, which the page's script draws from and an importing app reads. UTF-8 without BOM.
/// </summary>
public static class SharePage
{
    private const string Placeholder = "{{data}}";

    /// <summary>Letters of every script stay as they are; <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c> are escaped, so no title can end the block.</summary>
    private static readonly JsonWriterOptions Json = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    private static readonly Lazy<string> Template = new(ReadTemplate);

    public static byte[] Encode(ShareDocument document) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Format(document));

    public static string Format(ShareDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Template.Value.Replace(Placeholder, Data(document), StringComparison.Ordinal);
    }

    private static string Data(ShareDocument document)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, Json))
        {
            json.WriteStartObject();
            json.WriteString("format", ShareDocument.Format);
            json.WriteNumber("version", ShareDocument.Version);
            json.WriteString("kind", document.Kind);
            json.WriteString("title", document.Title);
            json.WriteString("made_at", Instant(document.MadeAt));
            json.WriteString("app_version", document.AppVersion);
            json.WriteStartArray("conversations");
            foreach (var conversation in document.Conversations)
            {
                Write(json, conversation);
            }

            json.WriteEndArray();
            json.WriteStartArray("links");
            foreach (var link in document.Links)
            {
                json.WriteStartObject();
                json.WriteString("a", link.A.ToString());
                json.WriteString("b", link.B.ToString());
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>A root has no parent and a conversation without a title no name: the fields are then left out.</summary>
    private static void Write(Utf8JsonWriter json, SharedConversation conversation)
    {
        json.WriteStartObject();
        json.WriteString("id", conversation.Id.ToString());
        if (conversation.Parent is { } parent)
        {
            json.WriteString("parent", parent.ToString());
        }

        if (conversation.Name is { } name)
        {
            json.WriteString("name", name);
        }

        json.WriteString("started_at", Instant(conversation.StartedAt));
        json.WriteNumber("questions", conversation.Questions);
        json.WriteBoolean("has_summary", conversation.HasSummary);
        json.WriteEndObject();
    }

    private static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string ReadTemplate()
    {
        using var stream = typeof(SharePage).Assembly.GetManifestResourceStream("Share.SharePage.html")
            ?? throw new FileNotFoundException("The share page is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }
}
