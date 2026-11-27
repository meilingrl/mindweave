using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Mindweave.Application.Exchange;
using Mindweave.Application.Share;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Infrastructure.ManualBackup;

namespace Mindweave.Infrastructure.Share;

/// <summary>
/// The one HTML file a share is (docs/design/sharing.md S2, §6.2): the page kept in <c>SharePage.html</c> with the
/// exchange document in its data block, which the page's script draws from and an importing app reads. UTF-8 without BOM.
/// The lines of the page between <c>{{#part}}</c> and <c>{{/part}}</c> belong to one kind of share: <c>structure</c>,
/// <c>content</c>, and with content <c>math</c> when there is a formula, else <c>plain</c>. A share with content also holds its answers and
/// summaries drawn, as data too: the page has no Markdown of its own, and never turns text into markup.
/// </summary>
public static partial class SharePage
{
    private const string DataPlaceholder = "{{data}}";
    private const string DrawnPlaceholder = "{{drawn}}";

    /// <summary>Letters of every script stay as they are; <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c> are escaped, so no text can end the block.</summary>
    private static readonly JsonWriterOptions Json = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    private static readonly Lazy<string> Structure = new(() => Template("structure"));
    private static readonly Lazy<string> Content = new(() => Template("content", "plain"));
    private static readonly Lazy<string> ContentWithMath = new(() => Template("content", "math"));

    public static byte[] Encode(ExchangeDocument document, IShareMarkdown? markdown = null) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Format(document, markdown));

    /// <param name="markdown">Needed for a share with content.</param>
    public static string Format(ExchangeDocument document, IShareMarkdown? markdown = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Kind == ExchangeDocument.StructureKind)
        {
            return Filled(Structure.Value, Data(document), null);
        }

        ArgumentNullException.ThrowIfNull(markdown);
        var drawn = Drawn(document, markdown);
        return Filled((drawn.Values.Any(HasMath) ? ContentWithMath : Content).Value, Data(document), Written(drawn));
    }

    /// <summary>Each placeholder is in the page once; what is put in is never searched, so a text may hold their names.</summary>
    private static string Filled(string template, string data, string? drawn)
    {
        var at = template.IndexOf(DataPlaceholder, StringComparison.Ordinal);
        var rest = template[(at + DataPlaceholder.Length)..];
        return template[..at] + data + (drawn is null ? rest : rest.Replace(DrawnPlaceholder, drawn, StringComparison.Ordinal));
    }

    /// <summary>Every shared answer and summary by its identity. A mark is drawn only for a branch that is in the share.</summary>
    private static Dictionary<string, IReadOnlyList<ShareNode>> Drawn(ExchangeDocument document, IShareMarkdown markdown)
    {
        var marks = document.Conversations.Where(c => c.Content?.Source?.Quote is not null)
            .ToLookup(c => c.Content!.Source!.Message, c => new ShareMark(c.Id, c.Content!.Source!));
        var drawn = new Dictionary<string, IReadOnlyList<ShareNode>>();
        foreach (var content in document.Conversations.Select(c => c.Content).OfType<ExchangeContent>())
        {
            foreach (var answer in content.Messages.Where(m => m.Role == MessageRole.Assistant))
            {
                drawn[answer.Id.ToString()] = markdown.Draw(answer.Text, answer.Citations.Select(c => c.Number).ToHashSet(), [.. marks[answer.Id]]);
            }

            if (content.Summary is { } summary)
            {
                drawn[summary.Id.ToString()] = markdown.Draw(summary.Body, new HashSet<int>(), []);
            }
        }

        return drawn;
    }

    private static bool HasMath(IReadOnlyList<ShareNode> nodes) =>
        nodes.Any(n => n.Kind is ShareNode.MathKind or ShareNode.MathBlockKind || HasMath(n.Parts));

    private static string Written(Dictionary<string, IReadOnlyList<ShareNode>> drawn) => Write(json =>
    {
        json.WriteStartObject();
        foreach (var (id, nodes) in drawn)
        {
            json.WriteStartArray(id);
            foreach (var node in nodes)
            {
                Write(json, node);
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
    });

    /// <summary>A text is a string; an element is [kind, value or null, its parts…].</summary>
    private static void Write(Utf8JsonWriter json, ShareNode node)
    {
        if (node.Kind == ShareNode.TextKind)
        {
            json.WriteStringValue(node.Value);
            return;
        }

        json.WriteStartArray();
        json.WriteStringValue(node.Kind);
        json.WriteStringValue(node.Value);
        foreach (var part in node.Parts)
        {
            Write(json, part);
        }

        json.WriteEndArray();
    }

    /// <summary>
    /// The exchange document as JSON (E5). A manual backup's document also holds its workspace and material, each original
    /// with what <paramref name="originals"/> says of it; a share has neither, so its JSON is as it always was.
    /// </summary>
    internal static string Data(ExchangeDocument document, Func<ContentHash, string>? originals = null) => Write(json =>
    {
        json.WriteStartObject();
        json.WriteString("format", ExchangeDocument.Format);
        json.WriteNumber("version", ExchangeDocument.Version);
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
            WriteIfAny(json, "note", link.Note);
            if (link.Stored is { } stored)
            {
                json.WriteString("id", stored.Id.ToString());
                json.WriteString("origin", Name(stored.Origin));
                json.WriteString("at", Instant(stored.At));
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        if (document.Workspace is { } workspace)
        {
            json.WriteStartObject("workspace");
            json.WriteString("id", workspace.Id.ToString());
            json.WriteString("name", workspace.Name);
            json.WriteString("created_at", Instant(workspace.CreatedAt));
            json.WriteEndObject();
            json.WriteStartArray("resources");
            foreach (var resource in document.Resources)
            {
                ManualBackupJson.Write(json, resource, originals?.Invoke(resource.Hash));
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
    });

    /// <summary>Optional fields are left out when there is nothing: a root has no parent, a conversation without a title no name.</summary>
    private static void Write(Utf8JsonWriter json, ExchangeConversation conversation)
    {
        json.WriteStartObject();
        json.WriteString("id", conversation.Id.ToString());
        WriteIfAny(json, "parent", conversation.Parent?.ToString());
        WriteIfAny(json, "name", conversation.Name);
        json.WriteString("started_at", Instant(conversation.StartedAt));
        json.WriteNumber("questions", conversation.Questions);
        json.WriteBoolean("has_summary", conversation.HasSummary);
        if (conversation.TitleByUser)
        {
            json.WriteBoolean("title_by_user", true);
        }

        if (conversation.IsArchived)
        {
            json.WriteBoolean("archived", true);
        }

        if (conversation.Received is { } received)
        {
            json.WriteStartObject("received");
            json.WriteString("title", received.Title);
            json.WriteString("at", Instant(received.At));
            json.WriteEndObject();
        }

        if (conversation.Content is { } content)
        {
            Write(json, content);
        }

        json.WriteEndObject();
    }

    private static void Write(Utf8JsonWriter json, ExchangeContent content)
    {
        if (content.Source is { } source)
        {
            json.WriteStartObject("source");
            json.WriteString("message", source.Message.ToString());
            WriteIfAny(json, "quote", source.Quote);
            if (source.Position is { } position)
            {
                json.WriteStartObject("position");
                json.WriteNumber("block", position.Block);
                json.WriteNumber("offset", position.Offset);
                json.WriteEndObject();
            }

            if (source.Anchor is { } anchor)
            {
                json.WriteStartObject("anchor");
                json.WriteNumber("version", anchor.Version);
                json.WriteString("profile", anchor.Profile);
                json.WriteString("data", anchor.Data);
                json.WriteEndObject();
            }

            if (source.Block is { } block)
            {
                json.WriteNumber("block", block);
            }

            if (source.At is { } made)
            {
                json.WriteString("at", Instant(made));
            }

            json.WriteEndObject();
        }

        if (content.Summary is { } summary)
        {
            json.WriteStartObject("summary");
            json.WriteString("id", summary.Id.ToString());
            json.WriteString("title", summary.Title);
            json.WriteString("body", summary.Body);
            json.WriteString("at", Instant(summary.At));
            json.WriteNumber("to_seq", summary.ToSeq);
            json.WriteEndObject();
        }

        if (content.History is { } history)
        {
            ManualBackupJson.Write(json, history);
        }

        json.WriteStartArray("messages");
        foreach (var message in content.Messages)
        {
            Write(json, message);
        }

        json.WriteEndArray();
    }

    private static void Write(Utf8JsonWriter json, ExchangeMessage message)
    {
        json.WriteStartObject();
        json.WriteString("id", message.Id.ToString());
        json.WriteNumber("seq", message.Seq);
        json.WriteString("role", message.Role == MessageRole.User ? "question" : "answer");
        json.WriteString("at", Instant(message.At));
        json.WriteString("state", message.State switch
        {
            MessageState.Complete => "complete",
            MessageState.Stopped => "stopped",
            MessageState.Interrupted => "interrupted",
            MessageState.Cancelled => "cancelled",
            _ => "failed",
        });
        json.WriteString("text", message.Text);
        if (message.Attachments.Count > 0)
        {
            json.WriteStartArray("attachments");
            foreach (var name in message.Attachments)
            {
                json.WriteStringValue(name);
            }

            json.WriteEndArray();
        }

        if (message.Activity is { } activity)
        {
            json.WriteStartObject("activity");
            json.WriteNumber("attachment_searches", activity.AttachmentSearches);
            json.WriteNumber("summary_searches", activity.SummarySearches);
            json.WriteNumber("web_searches", activity.WebSearches);
            json.WriteNumber("pages_read", activity.PagesRead);
            json.WriteNumber("other_calls", activity.OtherCalls);
            json.WriteEndObject();
        }

        if (message.Citations.Count > 0)
        {
            json.WriteStartArray("citations");
            foreach (var citation in message.Citations)
            {
                Write(json, citation);
            }

            json.WriteEndArray();
        }

        WriteIfAny(json, "replaced_by", message.ReplacedBy?.ToString());
        if (message.Stored is { } stored)
        {
            ManualBackupJson.Write(json, stored);
        }

        json.WriteEndObject();
    }

    /// <summary>A citation's kind as the format names it.</summary>
    internal static string KindOf(CitationKind kind) => kind switch
    {
        CitationKind.Web => "web",
        CitationKind.Attachment => "attachment",
        CitationKind.Summary => "summary",
        _ => "material",
    };

    /// <summary>A stored value's name in the format: its code name in lower case, words joined by "_" (LocalRead is local_read).</summary>
    internal static string Name<T>(T value)
        where T : struct, Enum => SnakeCase().Replace(value.ToString(), "$1_$2").ToLowerInvariant();

    private static void Write(Utf8JsonWriter json, ExchangeCitation citation)
    {
        json.WriteStartObject();
        json.WriteNumber("number", citation.Number);
        json.WriteString("kind", KindOf(citation.Kind));
        WriteIfAny(json, "title", citation.Title);
        WriteIfAny(json, "url", citation.Url?.AbsoluteUri);
        WriteIfAny(json, "conversation", citation.Summary?.ToString());
        json.WriteEndObject();
    }

    internal static void WriteIfAny(Utf8JsonWriter json, string name, string? value)
    {
        if (value is not null)
        {
            json.WriteString(name, value);
        }
    }

    internal static string Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, Json))
        {
            write(json);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The page with the parts of this kind of share. With content, only the file's own scripts may run: the policy names
    /// each by its hash, so nothing that came from a conversation can, whatever it looks like.
    /// </summary>
    private static string Template(params string[] parts)
    {
        // Twice: a part can stand inside another.
        string Kept(string text) => Part().Replace(text, m => parts.Contains(m.Groups[1].Value) ? m.Groups[2].Value : string.Empty);
        var page = Kept(Kept(Read("SharePage.html")));
        if (parts.Contains("math"))
        {
            page = page
                .Replace("{{katex-licence}}", Read("katex.LICENSE"), StringComparison.Ordinal)
                .Replace("{{katex-style}}", FontUrl().Replace(Read("katex.katex.min.css"), m => "src:url(data:font/woff2;base64," + Font(m.Groups[1].Value) + ") format(\"woff2\")"), StringComparison.Ordinal)
                .Replace("{{katex-script}}", Read("katex.katex.min.js"), StringComparison.Ordinal);
        }

        return page.Replace(
            "{{script-hashes}}",
            string.Join(' ', Script().Matches(page).Select(m => "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(m.Groups[1].Value))) + "'")),
            StringComparison.Ordinal);
    }

    private static string Read(string name)
    {
        using var stream = Open(name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    private static string Font(string name)
    {
        using var stream = Open("katex.fonts." + name);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return Convert.ToBase64String(bytes.GetBuffer(), 0, (int)bytes.Length);
    }

    private static Stream Open(string name) => typeof(SharePage).Assembly.GetManifestResourceStream("Share." + name)
        ?? throw new FileNotFoundException("The shared page's " + name + " is not embedded.");

    [GeneratedRegex(@"^\{\{#(\w+)\}\}\n(.*?)^\{\{/\1\}\}\n", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex Part();

    /// <summary>A font's addresses in the library's style: only its first form, woff2, is carried.</summary>
    [GeneratedRegex(@"src:url\(fonts/([\w-]+\.woff2)\)[^;}]*")]
    private static partial Regex FontUrl();

    [GeneratedRegex("<script>(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex Script();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex SnakeCase();
}

/// <summary>The shared file as the application asks for it (architecture §16.1: Application writes no HTML).</summary>
internal sealed class SharePageWriter(IShareMarkdown markdown) : ISharePageWriter
{
    public byte[] Encode(ExchangeDocument document) => SharePage.Encode(document, markdown);
}
