using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Mindweave.Application.Exchange;
using Mindweave.Application.Import;
using Mindweave.Application.ManualBackup;
using Mindweave.Application.Resources;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Application.SummaryLinks;
using Mindweave.Infrastructure.Share;

namespace Mindweave.Infrastructure.ManualBackup;

/// <summary>
/// Reads a manual backup's package (docs/design/sharing-backup.md §3, §4): entry by entry, by the names <see
/// cref="ManualBackupPackage"/> writes and no others. A name in the package is never used as a path on disk, and the name of
/// a workspace's document is made from the identity the manifest gives, not taken from the zip. Every original is checked
/// against its hash; one that does not match is treated as missing. Fields this version does not know are passed over.
/// </summary>
public sealed class ManualBackupReader : IManualBackupReader
{
    /// <summary>One workspace's document; far beyond what one holds in text (the owner's whole folder makes about 1 MB).</summary>
    public const long MaxDocumentBytes = 512L * 1024 * 1024;

    /// <summary>The manifest names its workspaces and little else.</summary>
    public const long MaxManifestBytes = 16L * 1024 * 1024;

    public const int MaxWorkspaces = 10_000;

    public string Extension => ManualBackupPackage.FileExtension;

    public Task<ManualBackupReading> ReadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Task.Run(() => Read(path, cancellationToken), cancellationToken);
    }

    public Task<byte[]?> ReadOriginalAsync(string path, ContentHash hash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Task.Run(() =>
        {
            try
            {
                using var zip = ZipFile.OpenRead(path);
                return zip.GetEntry(ManualBackupPackage.OriginalsFolder + hash.Hex) is { } entry
                    && Bytes(entry, ResourceLimits.MaxOriginalBytes, cancellationToken) is { } bytes
                    && ContentHash.FromBytes(SHA256.HashData(bytes)) == hash
                    ? bytes
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return null;
            }
        }, cancellationToken);
    }

    private static ManualBackupReading Read(string path, CancellationToken cancellationToken)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException)
        {
            // Not a zip at all: some other kind of file.
            return ManualBackupReading.Refused(ImportRefusal.NotAShare);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ManualBackupReading.Refused(ImportRefusal.Unreadable);
        }

        using (zip)
        {
            try
            {
                return Read(zip, path, cancellationToken);
            }
            catch (TooLargeException)
            {
                return ManualBackupReading.Refused(ImportRefusal.TooLarge);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or InvalidDataException or ArgumentException or OverflowException)
            {
                // A missing field, a value of another type, an entry that does not unpack, or text that is no text.
                return ManualBackupReading.Refused(ImportRefusal.Damaged);
            }
            catch (IOException)
            {
                return ManualBackupReading.Refused(ImportRefusal.Unreadable);
            }
        }
    }

    private static ManualBackupReading Read(ZipArchive zip, string path, CancellationToken cancellationToken)
    {
        if (zip.GetEntry(ManualBackupPackage.ManifestEntry) is not { } manifestEntry)
        {
            return ManualBackupReading.Refused(ImportRefusal.NotAShare);
        }

        using var manifest = JsonDocument.Parse(Bytes(manifestEntry, MaxManifestBytes, cancellationToken));
        var root = manifest.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != ExchangeDocument.Format)
        {
            return ManualBackupReading.Refused(ImportRefusal.NotAShare);
        }

        if (root.GetProperty("version").GetInt64() is var version && version > ExchangeDocument.Version)
        {
            return ManualBackupReading.Refused(ImportRefusal.NewerFormat);
        }

        if (version < 1 || root.GetProperty("kind").GetString() != ExchangeDocument.DeviceKind)
        {
            return ManualBackupReading.Refused(ImportRefusal.Damaged);
        }

        var listed = root.GetProperty("workspaces");
        if (listed.GetArrayLength() > MaxWorkspaces)
        {
            return ManualBackupReading.Refused(ImportRefusal.TooLarge);
        }

        var documents = new List<ExchangeDocument>();
        foreach (var workspace in listed.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The entry's name is made from the identity, never taken from the package.
            var id = new WorkspaceId(Guid(workspace.GetProperty("id")));
            var entry = zip.GetEntry(ManualBackupPackage.DocumentEntry(id)) ?? throw new InvalidDataException("A workspace's document is missing.");
            using var document = JsonDocument.Parse(Bytes(entry, MaxDocumentBytes, cancellationToken));
            var read = Document(document.RootElement);
            if (read.Workspace?.Id != id)
            {
                return ManualBackupReading.Refused(ImportRefusal.Damaged);
            }

            documents.Add(read);
        }

        if (!ManualBackupImportRules.IsSound(documents))
        {
            return ManualBackupReading.Refused(ImportRefusal.Damaged);
        }

        // Every original against its hash; one that does not match is as good as missing (§6).
        var originals = new HashSet<ContentHash>();
        var mismatched = 0;
        foreach (var hash in documents.SelectMany(d => d.Resources).Select(r => r.Hash).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (zip.GetEntry(ManualBackupPackage.OriginalsFolder + hash.Hex) is not { } original)
            {
                continue;
            }

            if (Matches(original, hash, cancellationToken))
            {
                originals.Add(hash);
            }
            else
            {
                mismatched++;
            }
        }

        return ManualBackupReading.Of(new ManualBackupFile(path, Instant(root.GetProperty("made_at")), Name(root.GetProperty("app_version")), documents, originals, mismatched));
    }

    /// <summary>Streams the entry through the hash, never more than an original may be.</summary>
    private static bool Matches(ZipArchiveEntry entry, ContentHash hash, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = entry.Open();
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += count;
                if (total > ResourceLimits.MaxOriginalBytes)
                {
                    return false;
                }

                sha.AppendData(buffer, 0, count);
            }

            return ContentHash.FromBytes(sha.GetHashAndReset()) == hash;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>An entry's bytes, read no further than <paramref name="most"/>: what the zip says of its size is not trusted.</summary>
    private static byte[] Bytes(ZipArchiveEntry entry, long most, CancellationToken cancellationToken)
    {
        if (entry.Length > most)
        {
            throw new TooLargeException();
        }

        using var stream = entry.Open();
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes.Length + count > most)
            {
                throw new TooLargeException();
            }

            bytes.Write(buffer, 0, count);
        }

        return bytes.ToArray();
    }

    private sealed class TooLargeException : Exception;

    private static ExchangeDocument Document(JsonElement root)
    {
        if (root.GetProperty("format").GetString() != ExchangeDocument.Format || root.GetProperty("version").GetInt64() > ExchangeDocument.Version
            || root.GetProperty("kind").GetString() != ExchangeDocument.DeviceKind)
        {
            throw new FormatException("A workspace's document is not a manual backup's.");
        }

        var conversations = root.GetProperty("conversations");
        if (conversations.GetArrayLength() > ImportLimits.MaxConversations || root.GetProperty("links").GetArrayLength() > ImportLimits.MaxLinks
            || conversations.EnumerateArray().Sum(c => (long)c.GetProperty("messages").GetArrayLength()) > ImportLimits.MaxMessages)
        {
            throw new TooLargeException();
        }

        var workspace = root.GetProperty("workspace");
        var resources = root.GetProperty("resources").EnumerateArray().Select(Resource).ToList();
        var material = resources.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        return new ExchangeDocument(
            ExchangeDocument.DeviceKind,
            Text(root.GetProperty("title")),
            Instant(root.GetProperty("made_at")),
            Name(root.GetProperty("app_version")),
            [.. conversations.EnumerateArray().Select(c => Conversation(c, material))],
            [.. root.GetProperty("links").EnumerateArray().Select(Link)])
        {
            Workspace = new ExchangeWorkspace(
                new WorkspaceId(Guid(workspace.GetProperty("id"))),
                WorkspaceName.TryCreate(Text(workspace.GetProperty("name")), out var name, out _) ? name!.Value : throw new FormatException("A workspace's name is not one."),
                Instant(workspace.GetProperty("created_at"))),
            Resources = resources,
        };
    }

    private static ExchangeLink Link(JsonElement link) =>
        new(Identity(link.GetProperty("a")), Identity(link.GetProperty("b")))
        {
            Note = link.TryGetProperty("note", out var note) ? Name(note) : null,
            Stored = new ExchangeStoredLink(new SummaryLinkId(Guid(link.GetProperty("id"))), Parse<SummaryLinkOrigin>(link.GetProperty("origin")), Instant(link.GetProperty("at"))),
        };

    private static ExchangeConversation Conversation(JsonElement conversation, IReadOnlyDictionary<ResourceId, ExchangeResource> material)
    {
        var questions = conversation.GetProperty("questions").GetInt32();
        var history = conversation.TryGetProperty("summary_history", out var saved) ? History(saved) : null;
        return new ExchangeConversation(
            Identity(conversation.GetProperty("id")),
            conversation.TryGetProperty("parent", out var parent) ? Identity(parent) : null,
            conversation.TryGetProperty("name", out var name) ? Name(name) : null,
            Instant(conversation.GetProperty("started_at")),
            questions < 0 ? throw new FormatException("A number of questions is never negative.") : questions,
            conversation.GetProperty("has_summary").GetBoolean())
        {
            TitleByUser = conversation.TryGetProperty("title_by_user", out var byUser) && byUser.GetBoolean(),
            IsArchived = conversation.TryGetProperty("archived", out var archived) && archived.GetBoolean(),
            Received = conversation.TryGetProperty("received", out var received)
                ? new ExchangeReceived(Name(received.GetProperty("title")), Instant(received.GetProperty("at")))
                : null,
            Content = new ExchangeContent(
                [.. conversation.GetProperty("messages").EnumerateArray().Select(m => Message(m, material))],
                conversation.TryGetProperty("source", out var source) ? Source(source) : null,
                history is null ? null : Current(history))
            {
                History = history,
            },
        };
    }

    /// <summary>The current version as a share has it; the history is what an import uses.</summary>
    private static ExchangeSummary Current(ExchangeSummaryHistory history) =>
        history.Versions.FirstOrDefault(v => v.Id == history.Current) is { } current
            ? new ExchangeSummary(current.Id, current.Title, current.Body, current.At, current.ToSeq)
            : throw new FormatException("A summary's current version is not one of its versions.");

    private static ExchangeMessage Message(JsonElement message, IReadOnlyDictionary<ResourceId, ExchangeResource> material)
    {
        var role = Text(message.GetProperty("role")) switch
        {
            "question" => MessageRole.User,
            "answer" => MessageRole.Assistant,
            _ => throw new FormatException("A message is a question or an answer."),
        };
        var blocks = message.GetProperty("blocks");
        if (blocks.GetArrayLength() > ImportLimits.MaxNumber)
        {
            throw new FormatException("A message has too many blocks.");
        }

        var stored = new ExchangeStoredMessage([.. blocks.EnumerateArray().Select(b => Block(b, material))], Instant(message.GetProperty("created_at")))
        {
            CompletedAt = message.TryGetProperty("completed_at", out var completed) ? Instant(completed) : null,
            Supersedes = message.TryGetProperty("supersedes", out var supersedes) ? new MessageId(Guid(supersedes)) : null,
            Error = message.TryGetProperty("error", out var error) ? Parse<GenerationErrorKind>(error) : null,
            Model = message.TryGetProperty("model", out var model) ? Name(model) : null,
            Run = message.TryGetProperty("run", out var run) ? Run(run) : null,
        };
        return new ExchangeMessage(
            new MessageId(Guid(message.GetProperty("id"))),
            Number(message.GetProperty("seq")),
            role,
            Instant(message.GetProperty("at")),
            Text(message.GetProperty("state")) switch
            {
                "complete" => MessageState.Complete,
                "stopped" => MessageState.Stopped,
                "interrupted" => MessageState.Interrupted,
                "cancelled" => MessageState.Cancelled,
                "failed" => MessageState.Failed,
                _ => throw new FormatException("A message in a manual backup is finished."),
            },
            Body(message.GetProperty("text")))
        {
            Attachments = message.TryGetProperty("attachments", out var attachments)
                ? [.. attachments.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.Null ? null : Name(a))]
                : [],
            Stored = stored,
        };
    }

    private static ExchangeRun Run(JsonElement run) => new(
        [.. run.GetProperty("steps").EnumerateArray().Select(s => new ExchangeRunStep(
            Number(s.GetProperty("ordinal")), Parse<RunStepKind>(s.GetProperty("kind")), Parse<RunStepState>(s.GetProperty("state")), Instant(s.GetProperty("started_at")))
        {
            FinishedAt = s.TryGetProperty("finished_at", out var finished) ? Instant(finished) : null,
            CallId = Optional(s, "call_id"),
            Tool = Optional(s, "tool"),
            Risk = s.TryGetProperty("risk", out var risk) ? Parse<ToolRisk>(risk) : null,
            Input = Optional(s, "input"),
            Result = s.TryGetProperty("result", out var result) ? new ResourceId(Guid(result)) : null,
            Error = s.TryGetProperty("error", out var error) ? Parse<GenerationErrorKind>(error) : null,
            Approval = s.TryGetProperty("approval", out var approval) ? Parse<Approval>(approval) : null,
        })],
        run.TryGetProperty("limit_reached", out var limit) ? Parse<RunLimit>(limit) : null);

    /// <summary>Each block by its type; an attachment is what its resource says it is.</summary>
    private static ContentBlock Block(JsonElement block, IReadOnlyDictionary<ResourceId, ExchangeResource> material) => Text(block.GetProperty("type")) switch
    {
        "text" => new TextBlock(Body(block.GetProperty("markdown"))),
        "attachment" => new ResourceId(Guid(block.GetProperty("resource"))) is var id && material.TryGetValue(id, out var resource)
            ? new AttachmentBlock(id, resource.Name, resource.MediaType, resource.Size, resource.Text)
            : throw new FormatException("An attachment names material that is not described."),
        "absent_attachment" => new AbsentAttachmentBlock(Optional(block, "name")),
        "tool_call" => new ToolCallBlock(Text(block.GetProperty("call_id")), Name(block.GetProperty("tool")), Body(block.GetProperty("arguments")), Parse<ToolRisk>(block.GetProperty("risk")), Body(block.GetProperty("summary"))),
        "tool_result" => new ToolResultBlock(
            Text(block.GetProperty("call_id")),
            Parse<ToolResultStatus>(block.GetProperty("status")),
            Body(block.GetProperty("preview")),
            block.TryGetProperty("resource", out var result) ? new ResourceId(Guid(result)) : null,
            block.TryGetProperty("detail", out var detail) ? Body(detail) : null,
            block.TryGetProperty("findings", out var findings)
                ? [.. findings.EnumerateArray().Select(f => new ToolFinding(Parse<ToolFindingKind>(f.GetProperty("kind")), Body(f.GetProperty("id")), Body(f.GetProperty("title")), f.TryGetProperty("part", out var part) ? part.GetInt32() : null))]
                : null),
        "activity" => new ActivityCountBlock(
            Number(block.GetProperty("attachment_searches")), Number(block.GetProperty("summary_searches")), Number(block.GetProperty("web_searches")),
            Number(block.GetProperty("pages_read")), Number(block.GetProperty("other_calls"))),
        "citation" => new CitationBlock(
            Number(block.GetProperty("number")),
            Body(block.GetProperty("title")),
            block.TryGetProperty("url", out var url) ? Address(url) : null,
            block.TryGetProperty("fetched_at", out var fetched) ? Instant(fetched) : null,
            block.TryGetProperty("resource", out var cited) ? new ResourceId(Guid(cited)) : null,
            block.GetProperty("start").GetInt32(),
            block.GetProperty("end").GetInt32())
        {
            Kind = Text(block.GetProperty("kind")) switch
            {
                "web" => CitationKind.Web,
                "attachment" => CitationKind.Attachment,
                "summary" => CitationKind.Summary,
                "material" => CitationKind.Material,
                _ => throw new FormatException("A citation is of something else."),
            },
            Target = Optional(block, "target"),
        },
        _ => throw new FormatException("A block of a kind this version does not know."),
    };

    private static ExchangeBranchSource Source(JsonElement source)
    {
        var quote = source.TryGetProperty("quote", out var quoted) ? Text(quoted) : null;
        if (quote is { Length: > Core.Branches.BranchRules.MaxQuoteLength })
        {
            throw new FormatException("A quote is too long.");
        }

        return new ExchangeBranchSource(
            new MessageId(Guid(source.GetProperty("message"))),
            quote,
            source.TryGetProperty("position", out var position) ? new ExchangePosition(Number(position.GetProperty("block")), Number(position.GetProperty("offset"), ImportLimits.MaxTextLength)) : null,
            source.TryGetProperty("anchor", out var anchor)
                ? new ExchangeAnchor(Number(anchor.GetProperty("version")), Name(anchor.GetProperty("profile")), Text(anchor.GetProperty("data")) is { Length: <= Core.Branches.BranchRules.MaxAnchorLength } data ? data : throw new FormatException("An anchor is too long."))
                : null)
        {
            Block = source.TryGetProperty("block", out var block) ? Number(block) : null,
            At = source.TryGetProperty("at", out var at) ? Instant(at) : null,
        };
    }

    private static ExchangeSummaryHistory History(JsonElement history) => new(
        new SummaryNodeId(Guid(history.GetProperty("node"))),
        NonEmpty(history.GetProperty("name")),
        Parse<NameSource>(history.GetProperty("name_source")),
        NonEmpty(history.GetProperty("base_title")),
        new SummaryVersionId(Guid(history.GetProperty("current"))),
        Instant(history.GetProperty("created_at")),
        [.. history.GetProperty("versions").EnumerateArray().Select(v => new ExchangeSummaryVersion(
            new SummaryVersionId(Guid(v.GetProperty("id"))),
            Number(v.GetProperty("seq")) is > 0 and var seq ? seq : throw new FormatException("A version is numbered from 1."),
            NonEmpty(v.GetProperty("title")),
            Optional(v, "short_title"),
            Body(v.GetProperty("body")) is { Length: > 0 } body ? body : throw new FormatException("A version has a text."),
            Number(v.GetProperty("to_seq")),
            Parse<SummaryOrigin>(v.GetProperty("origin")),
            v.TryGetProperty("baseline", out var baseline) ? new SummaryVersionId(Guid(baseline)) : null,
            Optional(v, "model"),
            Instant(v.GetProperty("at")),
            v.TryGetProperty("read", out var read) ? Parse<ReadSource>(read) : null))]);

    private static ExchangeResource Resource(JsonElement resource)
    {
        var size = resource.GetProperty("size").GetInt64();
        return new ExchangeResource(
            new ResourceId(Guid(resource.GetProperty("id"))),
            resource.TryGetProperty("conversation", out var conversation) ? Identity(conversation) : null,
            ContentHash.Parse(Text(resource.GetProperty("hash"))),
            Name(resource.GetProperty("media_type")),
            size is >= 0 and <= ResourceLimits.MaxOriginalBytes ? size : throw new FormatException("An original's size is out of range."),
            Parse<ResourceOrigin>(resource.GetProperty("origin")),
            Optional(resource, "name"),
            resource.TryGetProperty("source_url", out var url) ? new Uri(Text(url), UriKind.Absolute) : null,
            Parse<ResourceProvenance>(resource.GetProperty("provenance")),
            Instant(resource.GetProperty("created_at")))
        {
            Text = resource.TryGetProperty("text", out var text) ? Body(text) : null,
            TextTruncated = resource.TryGetProperty("text_truncated", out var truncated) && truncated.GetBoolean(),
            TextProducer = Optional(resource, "text_producer"),
        };
    }

    /// <summary>A stored value by its name in the format (<see cref="SharePage.Name{T}"/>); a name this version does not know is damage.</summary>
    private static T Parse<T>(JsonElement value)
        where T : struct, Enum
    {
        var name = Text(value);
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (SharePage.Name(candidate) == name)
            {
                return candidate;
            }
        }

        throw new FormatException("A value this version does not know.");
    }

    private static Uri Address(JsonElement value) =>
        Uri.TryCreate(Text(value), UriKind.Absolute, out var url) && Text(value).Length <= ImportLimits.MaxAddressLength ? url : throw new FormatException("An address is not one.");

    private static string? Optional(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? Body(value) : null;

    private static int Number(JsonElement value, int most = ImportLimits.MaxNumber) =>
        value.GetInt32() is var number && number >= 0 && number <= most ? number : throw new FormatException("A number is out of range.");

    private static string Body(JsonElement value)
    {
        var text = Text(value);
        return text.Length > ImportLimits.MaxTextLength ? throw new FormatException("A text is too long.") : text;
    }

    private static string NonEmpty(JsonElement value) => Name(value) is { Length: > 0 } text ? text : throw new FormatException("A name is empty.");

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
    private static string Text(JsonElement value) => value.GetString()?.Replace('\0', '�') ?? throw new FormatException("A text is missing.");

    private static DateTimeOffset Instant(JsonElement value)
    {
        var at = DateTimeOffset.Parse(Text(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return at.Year is >= ImportLimits.FirstYear and <= ImportLimits.LastYear ? at : throw new FormatException("A time is out of range.");
    }
}
