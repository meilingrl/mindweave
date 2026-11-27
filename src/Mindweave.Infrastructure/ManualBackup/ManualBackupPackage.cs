using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Mindweave.Application.Exchange;
using Mindweave.Application.Export;
using Mindweave.Application.ManualBackup;
using Mindweave.Application.Share;
using Mindweave.Application.Storage;
using Mindweave.Core.Resources;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Share;

namespace Mindweave.Infrastructure.ManualBackup;

/// <summary>
/// The package of a manual backup (docs/design/sharing-backup.md §4): one zip, with these entries and no others.
/// <code>
/// manifest.json                    format, version, kind device, when, the app's version, the workspaces
/// index.html                       the entry page: a link to each workspace's page
/// workspaces/{id}/document.json    the exchange document of kind device of one workspace
/// workspaces/{id}/index.html       its readable page, drawn as a share with content is
/// originals/{sha256}               each original once, by the hash of its content
/// </code>
/// Unpacked, the pages read in a browser with the network off. It is written through the atomic file writer: to a
/// temporary file beside the target, then moved into place, so a cancel or a failure leaves no file.
/// </summary>
internal sealed class ManualBackupPackage(IResourceBlobStore blobs, IExportFileWriter files, ISharePageWriter pages) : IManualBackupWriter
{
    public const string FileExtension = "mwbackup";

    public const string ManifestEntry = "manifest.json";

    public const string IndexEntry = "index.html";

    public const string OriginalsFolder = "originals/";

    public string Extension => FileExtension;

    public static string DocumentEntry(WorkspaceId workspace) => "workspaces/" + workspace + "/document.json";

    public static string PageEntry(WorkspaceId workspace) => "workspaces/" + workspace + "/index.html";

    public async Task<ManualBackupSaved> WriteAsync(
        DateTimeOffset madeAt,
        string appVersion,
        IReadOnlyList<WorkspaceId> workspaces,
        Func<WorkspaceId, CancellationToken, Task<ManualBackupPart?>> read,
        ExportDestination destination,
        IProgress<ManualBackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(read);
        var written = new List<(ExchangeWorkspace Workspace, int Conversations)>();
        var originals = new Dictionary<ContentHash, string>();
        var mode = await files.WriteAsync(destination, async (stream, ct) =>
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
            progress?.Report(new ManualBackupProgress(0, workspaces.Count));
            for (var i = 0; i < workspaces.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (await read(workspaces[i], ct).ConfigureAwait(false) is { Document.Workspace: { } workspace } part)
                {
                    foreach (var resource in part.Document.Resources)
                    {
                        if (!originals.ContainsKey(resource.Hash))
                        {
                            originals[resource.Hash] = await CopyOriginalAsync(zip, resource.Hash, ct).ConfigureAwait(false);
                        }
                    }

                    await AddAsync(zip, DocumentEntry(workspace.Id), SharePage.Data(part.Document, hash => originals[hash]), ct).ConfigureAwait(false);
                    await AddAsync(zip, PageEntry(workspace.Id), pages.Encode(part.Page), ct).ConfigureAwait(false);
                    written.Add((workspace, part.Document.Conversations.Count));
                }

                progress?.Report(new ManualBackupProgress(i + 1, workspaces.Count));
            }

            await AddAsync(zip, IndexEntry, Index(written, madeAt), ct).ConfigureAwait(false);
            await AddAsync(zip, ManifestEntry, Manifest(written, originals, madeAt, appVersion), ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return new ManualBackupSaved(
            written.Count,
            written.Sum(w => w.Conversations),
            originals.Count(o => o.Value == Present),
            originals.Count(o => o.Value != Present),
            mode);
    }

    private const string Present = "present";

    /// <summary>Kept in <c>ManualBackupIndex.html</c>, beside this file.</summary>
    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(ManualBackupPackage).Assembly.GetManifestResourceStream("ManualBackup.ManualBackupIndex.html")
            ?? throw new FileNotFoundException("The manual backup's entry page is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    });

    /// <summary>
    /// Copies an original into the package, checking it against its hash on the way. One that is missing or damaged here
    /// is not carried, and the document says so; that never fails the save (§6).
    /// </summary>
    private async Task<string> CopyOriginalAsync(ZipArchive zip, ContentHash hash, CancellationToken cancellationToken)
    {
        Stream? source;
        try
        {
            source = blobs.OpenRead(hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "missing";
        }

        if (source is null)
        {
            return "missing";
        }

        await using (source.ConfigureAwait(false))
        {
            var entry = zip.CreateEntry(OriginalsFolder + hash.Hex, CompressionLevel.Fastest);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var target = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (target.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                int count;
                try
                {
                    while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        sha.AppendData(buffer, 0, count);
                        await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (IOException)
                {
                    // Unreadable halfway: what was copied does not match its name, and an import checks every original.
                    return "damaged";
                }
            }

            return ContentHash.FromBytes(sha.GetHashAndReset()) == hash ? Present : "damaged";
        }
    }

    private static Task AddAsync(ZipArchive zip, string name, string text, CancellationToken cancellationToken) =>
        AddAsync(zip, name, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text), cancellationToken);

    private static async Task AddAsync(ZipArchive zip, string name, byte[] bytes, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Manifest(List<(ExchangeWorkspace Workspace, int Conversations)> written, Dictionary<ContentHash, string> originals, DateTimeOffset madeAt, string appVersion) =>
        SharePage.Write(json =>
        {
            json.WriteStartObject();
            json.WriteString("format", ExchangeDocument.Format);
            json.WriteNumber("version", ExchangeDocument.Version);
            json.WriteString("kind", ExchangeDocument.DeviceKind);
            json.WriteString("made_at", SharePage.Instant(madeAt));
            json.WriteString("app_version", appVersion);
            json.WriteStartArray("workspaces");
            foreach (var (workspace, conversations) in written)
            {
                json.WriteStartObject();
                json.WriteString("id", workspace.Id.ToString());
                json.WriteString("name", workspace.Name);
                json.WriteNumber("conversations", conversations);
                json.WriteString("document", DocumentEntry(workspace.Id));
                json.WriteString("page", PageEntry(workspace.Id));
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteNumber("originals", originals.Count(o => o.Value == Present));
            json.WriteNumber("originals_missing", originals.Count(o => o.Value != Present));
            json.WriteEndObject();
        });

    /// <summary>The entry page: plain HTML that loads nothing and runs nothing, in both of the app's languages.</summary>
    private static string Index(List<(ExchangeWorkspace Workspace, int Conversations)> written, DateTimeOffset madeAt)
    {
        var day = madeAt.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var rows = new StringBuilder();
        foreach (var (workspace, conversations) in written)
        {
            rows.Append("<li><a href=\"").Append(WebUtility.HtmlEncode(PageEntry(workspace.Id))).Append("\">")
                .Append(WebUtility.HtmlEncode(workspace.Name)).Append("</a> <span class=\"count\">")
                .Append(conversations.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("</span></li>\n");
        }

        return Template.Value.Replace("{{day}}", day, StringComparison.Ordinal).Replace("{{rows}}", rows.ToString(), StringComparison.Ordinal);
    }
}
