using Microsoft.Extensions.Logging;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Workspaces;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Application.Resources;

public enum ResourceCreateStatus
{
    Created,

    /// <summary>Nothing to store: an empty paste or an empty file.</summary>
    Empty,

    /// <summary>Larger than <see cref="ResourceLimits.MaxOriginalBytes"/> (an image: <see cref="ResourceLimits.MaxImageBytes"/> or <see cref="ResourceLimits.MaxImagePixels"/>).</summary>
    TooLarge,

    /// <summary>A kind of file that is not supported (archives, spreadsheets, old .doc…).</summary>
    Unsupported,

    /// <summary>The bytes are not what the name promised (binary data in a ".txt", a damaged PDF, an image name with other bytes).</summary>
    NotReadable,

    /// <summary>The file holds no text to extract, as a scanned PDF does (no OCR in phase 1).</summary>
    NoText,

    /// <summary>The file is protected by a password.</summary>
    Encrypted,

    /// <summary>The file could not be opened or read (gone, no permission).</summary>
    FileUnavailable,
}

/// <summary>Outcome of creating a resource; <paramref name="Resource"/> is set only when it was created.</summary>
public sealed record ResourceCreateResult(ResourceCreateStatus Status, StoredResource? Resource = null, string? Detail = null)
{
    public bool IsCreated => Status == ResourceCreateStatus.Created;
}

/// <summary>An image as it goes into a request: its type and bytes (scaled for sending when it had to be).</summary>
public sealed record ImageForSending(string MediaType, byte[] Bytes);

/// <summary>Creating, reading and removing resources: attachments of the composer and of sent questions (requirements §10.4.3).</summary>
public interface IResources
{
    /// <summary>A pasted text as an unnamed resource: its original is the UTF-8 bytes, its derived text the paste itself.</summary>
    Task<ResourceCreateResult> CreateFromPasteAsync(WorkspaceId workspace, ConversationId? conversation, string text, CancellationToken cancellationToken);

    /// <summary>A file as a resource: text, source code, PDF and DOCX with their extracted text; images as originals.</summary>
    Task<ResourceCreateResult> CreateFromFileAsync(WorkspaceId workspace, ConversationId? conversation, string path, CancellationToken cancellationToken);

    /// <summary>A paste kept as an attachment of <paramref name="draft"/>.</summary>
    Task<ResourceCreateResult> AttachPasteAsync(DraftSlot draft, string text, CancellationToken cancellationToken);

    /// <summary>A file attached to <paramref name="draft"/>; extraction runs off the caller's thread.</summary>
    Task<ResourceCreateResult> AttachFileAsync(DraftSlot draft, string path, CancellationToken cancellationToken);

    /// <summary>An image (pasted from the clipboard) attached to <paramref name="draft"/>; <paramref name="name"/> may be null.</summary>
    Task<ResourceCreateResult> AttachImageAsync(DraftSlot draft, byte[] bytes, string? name, CancellationToken cancellationToken);

    /// <summary>The draft's attachments in order (they survive a restart).</summary>
    Task<IReadOnlyList<StoredResource>> GetDraftAsync(DraftSlot draft, CancellationToken cancellationToken);

    Task<StoredResource?> GetAsync(ResourceId id, CancellationToken cancellationToken);

    /// <summary>The original's bytes as a stream, or null when the resource or its file is gone.</summary>
    Task<Stream?> OpenOriginalAsync(ResourceId id, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the original file is there and whole (requirements §10.4.3: a missing or damaged one is shown as 原件缺失). It
    /// may read the whole file, so never on the caller's thread.
    /// </summary>
    Task<bool> HasOriginalAsync(Resource resource);

    Task<ResourceDeleteStatus> DeleteUnattachedAsync(ResourceId id, CancellationToken cancellationToken);

    /// <summary>
    /// Startup clean-up (architecture §15.2): unsent resources in no draft, then originals nothing refers to and leftover
    /// temporary files. Best effort: a failure is logged.
    /// </summary>
    Task CleanUpAsync(CancellationToken cancellationToken);
}

public sealed partial class ResourceService(
    IResourceStore store,
    IResourceBlobStore blobs,
    IEnumerable<ITextExtractor> extractors,
    StorageHealth health,
    IClock clock,
    ILogger<ResourceService> logger,
    IImageProcessor? images = null) : IResources
{
    /// <summary>The producer of a scaled image derivative.</summary>
    public const string ScaledImageProducer = "image-scale/1";

    private readonly ITextExtractor[] _extractors = [.. extractors];

    /// <summary>The block a message uses to refer to <paramref name="resource"/>.</summary>
    public static AttachmentBlock BlockFor(StoredResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var r = resource.Resource;
        return new AttachmentBlock(r.Id, r.Name, r.MediaType, r.Size, resource.Text);
    }

    public Task<ResourceCreateResult> CreateFromPasteAsync(WorkspaceId workspace, ConversationId? conversation, string text, CancellationToken cancellationToken) =>
        PasteAsync(workspace, conversation, text, null, cancellationToken);

    public Task<ResourceCreateResult> CreateFromFileAsync(WorkspaceId workspace, ConversationId? conversation, string path, CancellationToken cancellationToken) =>
        FileAsync(workspace, conversation, path, null, cancellationToken);

    public Task<ResourceCreateResult> AttachPasteAsync(DraftSlot draft, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return PasteAsync(draft.Workspace, draft.Conversation, text, draft, cancellationToken);
    }

    public Task<ResourceCreateResult> AttachFileAsync(DraftSlot draft, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return FileAsync(draft.Workspace, draft.Conversation, path, draft, cancellationToken);
    }

    public async Task<ResourceCreateResult> AttachImageAsync(DraftSlot draft, byte[] bytes, string? name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
        {
            return new ResourceCreateResult(ResourceCreateStatus.Empty);
        }

        return await ImageAsync(draft.Workspace, draft.Conversation, bytes, name ?? T("Image", "图片"), draft, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<StoredResource>> GetDraftAsync(DraftSlot draft, CancellationToken cancellationToken) => store.GetDraftAsync(draft, cancellationToken);

    public Task<StoredResource?> GetAsync(ResourceId id, CancellationToken cancellationToken) => store.GetAsync(id, cancellationToken);

    public async Task<Stream?> OpenOriginalAsync(ResourceId id, CancellationToken cancellationToken)
    {
        var resource = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return resource is null ? null : await Task.Run(() => OpenOriginal(resource.Resource), cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> HasOriginalAsync(Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return Task.Run(() => blobs.Exists(resource.Hash) || blobs.TryRecover(resource.Hash));
    }

    /// <summary>The original of a stored resource, put back from a backup when a restore left it missing or it is damaged.</summary>
    private Stream? OpenOriginal(Resource resource) => blobs.OpenRead(resource.Hash) ?? (blobs.TryRecover(resource.Hash) ? blobs.OpenRead(resource.Hash) : null);

    public async Task<ResourceDeleteStatus> DeleteUnattachedAsync(ResourceId id, CancellationToken cancellationToken)
    {
        health.ThrowIfBlocked();
        ResourceDeleteStatus status;
        try
        {
            status = await store.DeleteUnattachedAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageWriteException ex)
        {
            health.Report(ex);
            throw;
        }

        if (status == ResourceDeleteStatus.Deleted)
        {
            await SweepQuietlyAsync(blobs, logger).ConfigureAwait(false);
        }

        return status;
    }

    public async Task CleanUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            var removed = await store.DeleteAbandonedAsync(cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                LogAbandoned(logger, removed);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Best effort: a clean-up that fails changes nothing and runs again next start.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSweepFailed(logger, ex);
            return;
        }

        await SweepQuietlyAsync(blobs, logger).ConfigureAwait(false);
    }

    /// <summary>
    /// The image as a request sends it (architecture §15.4): scaled to <see cref="ResourceLimits.SentImageLongSide"/> once and
    /// kept as a derivative, the original when it needs no scaling. Null when the original is gone.
    /// </summary>
    public async Task<ImageForSending?> ImageForSendingAsync(Resource resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var known = await store.GetDerivativesAsync([resource.Id], DerivativeKind.ImageScaled, null, cancellationToken).ConfigureAwait(false);
        if (known.TryGetValue(resource.Id, out var scaled) && scaled.Derivative.BlobHash is { } scaledHash
            && await ReadAllAsync(blobs.OpenRead(scaledHash), cancellationToken).ConfigureAwait(false) is { } scaledBytes)
        {
            return new ImageForSending(scaled.Derivative.Text, scaledBytes);
        }

        if (await ReadAllAsync(OpenOriginal(resource), cancellationToken).ConfigureAwait(false) is not { } original)
        {
            return null;
        }

        if (images?.ScaleForSending(original, ResourceLimits.SentImageLongSide) is not { } encoded)
        {
            return new ImageForSending(resource.MediaType, original);
        }

        try
        {
            using var blob = await blobs.WriteAsync(encoded.Bytes, cancellationToken).ConfigureAwait(false);
            // The derivative's text is the media type of its bytes.
            await store.AddDerivativeAsync(resource.Id, new ResourceDerivative(DerivativeKind.ImageScaled, encoded.MediaType, ScaledImageProducer, BlobHash: blob.Hash), clock.UtcNow, cancellationToken)
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A cache that could not be kept is made again next time; the request goes on.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogScaledNotKept(logger, ex);
        }

        return new ImageForSending(encoded.MediaType, encoded.Bytes);
    }

    /// <summary>
    /// A sweep after something was deleted, best effort: a failure is logged and never changes the outcome of the delete
    /// that ran it; whatever it left is removed by a later sweep.
    /// </summary>
    public static async Task SweepQuietlyAsync(IResourceBlobStore? blobs, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (blobs is null)
        {
            return;
        }

        try
        {
            await blobs.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort by design: the delete it follows has already succeeded.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSweepFailed(logger, ex);
        }
    }

    private Task<ResourceCreateResult> PasteAsync(WorkspaceId workspace, ConversationId? conversation, string text, DraftSlot? draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(new ResourceCreateResult(ResourceCreateStatus.Empty));
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        if (bytes.LongLength > ResourceLimits.MaxOriginalBytes)
        {
            return Task.FromResult(new ResourceCreateResult(ResourceCreateStatus.TooLarge));
        }

        var bounded = ResourceLimits.Bounded(PlainTextExtractor.NormalizeLineEndings(text));
        var derived = new ResourceDerivative(DerivativeKind.Text, bounded.Text!, "paste/1", Truncated: bounded.Truncated);
        return StoreAsync(workspace, conversation, bytes, MediaTypes.PlainText, ResourceOrigin.Paste, null, derived, draft, cancellationToken);
    }

    private async Task<ResourceCreateResult> FileAsync(WorkspaceId workspace, ConversationId? conversation, string path, DraftSlot? draft, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var name = Path.GetFileName(path);
        if (FileTypes.MediaTypeOf(name) is not { } mediaType)
        {
            return new ResourceCreateResult(ResourceCreateStatus.Unsupported, Detail: Path.GetExtension(name));
        }

        var limit = MediaTypes.IsImage(mediaType) ? ResourceLimits.MaxImageBytes : ResourceLimits.MaxOriginalBytes;
        byte[] bytes;
        try
        {
            if (await ReadBoundedAsync(path, limit, cancellationToken).ConfigureAwait(false) is not { } read)
            {
                return new ResourceCreateResult(ResourceCreateStatus.TooLarge);
            }

            bytes = read;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFileUnavailable(logger, ex);
            return new ResourceCreateResult(ResourceCreateStatus.FileUnavailable, Detail: ex.Message);
        }

        if (bytes.Length == 0)
        {
            return new ResourceCreateResult(ResourceCreateStatus.Empty);
        }

        if (MediaTypes.IsImage(mediaType))
        {
            return await ImageAsync(workspace, conversation, bytes, name, draft, cancellationToken).ConfigureAwait(false);
        }

        if (_extractors.FirstOrDefault(e => e.CanExtract(mediaType)) is not { } extractor)
        {
            return new ResourceCreateResult(ResourceCreateStatus.Unsupported, Detail: mediaType);
        }

        // Parsing a large PDF takes a while: never on the caller's (the window's) thread.
        var extraction = await Task.Run(() => extractor.Extract(bytes, mediaType), cancellationToken).ConfigureAwait(false);
        if (extraction.Failure is { } failure || extraction.Text is null)
        {
            return new ResourceCreateResult(StatusOf(extraction.Failure ?? ExtractionFailure.NotReadable));
        }

        if (string.IsNullOrWhiteSpace(extraction.Text))
        {
            return new ResourceCreateResult(MediaTypes.IsImage(mediaType) || FileTypes.IsTextMediaType(mediaType) ? ResourceCreateStatus.Empty : ResourceCreateStatus.NoText);
        }

        var derived = new ResourceDerivative(DerivativeKind.Text, extraction.Text, extractor.Producer, Truncated: extraction.Truncated);
        return await StoreAsync(workspace, conversation, bytes, mediaType, ResourceOrigin.File, name, derived, draft, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResourceCreateResult> ImageAsync(WorkspaceId workspace, ConversationId? conversation, byte[] bytes, string name, DraftSlot? draft, CancellationToken cancellationToken)
    {
        if (bytes.LongLength > ResourceLimits.MaxImageBytes)
        {
            return new ResourceCreateResult(ResourceCreateStatus.TooLarge);
        }

        // The bytes decide the type: a ".jpg" that is really a PNG is stored as a PNG; one that is no image is refused.
        string actual;
        if (images is not null)
        {
            if (images.Probe(bytes) is not { } info)
            {
                return new ResourceCreateResult(ResourceCreateStatus.NotReadable);
            }

            if (info.Pixels > ResourceLimits.MaxImagePixels)
            {
                return new ResourceCreateResult(ResourceCreateStatus.TooLarge);
            }

            actual = info.MediaType;
        }
        else if (FileTypes.ImageTypeOf(bytes) is { } signature)
        {
            actual = signature;
        }
        else
        {
            return new ResourceCreateResult(ResourceCreateStatus.NotReadable);
        }

        return await StoreAsync(workspace, conversation, bytes, actual, ResourceOrigin.Image, NameWithType(name, actual), null, draft, cancellationToken).ConfigureAwait(false);
    }

    private static string NameWithType(string name, string mediaType) =>
        Path.HasExtension(name) ? name : name + mediaType switch
        {
            MediaTypes.Png => ".png",
            MediaTypes.Jpeg => ".jpg",
            MediaTypes.Gif => ".gif",
            MediaTypes.WebP => ".webp",
            _ => string.Empty,
        };

    private static ResourceCreateStatus StatusOf(ExtractionFailure failure) => failure switch
    {
        ExtractionFailure.NoText => ResourceCreateStatus.NoText,
        ExtractionFailure.Encrypted => ResourceCreateStatus.Encrypted,
        ExtractionFailure.TooLarge => ResourceCreateStatus.TooLarge,
        _ => ResourceCreateStatus.NotReadable,
    };

    private async Task<ResourceCreateResult> StoreAsync(
        WorkspaceId workspace,
        ConversationId? conversation,
        byte[] original,
        string mediaType,
        ResourceOrigin origin,
        string? fileName,
        ResourceDerivative? derived,
        DraftSlot? draft,
        CancellationToken cancellationToken)
    {
        health.ThrowIfBlocked();
        var chunks = derived is null ? [] : ResourceChunker.Split(derived.Text);
        if (derived is not null)
        {
            derived = derived with { EstimatedTokens = Core.Context.TokenEstimator.Estimate(derived.Text) };
        }

        try
        {
            // The original first, then the row, while the hash is pinned so a sweep cannot take the file in between.
            using var blob = await blobs.WriteAsync(original, cancellationToken).ConfigureAwait(false);
            var resource = new Resource(
                ResourceId.New(), workspace, conversation, blob.Hash, mediaType, blob.Size, origin, fileName, null, ResourceProvenance.User, StoredTime(clock.UtcNow));
            await store.InsertAsync(resource, derived is null ? [] : [derived], chunks, draft, cancellationToken).ConfigureAwait(false);
            return new ResourceCreateResult(ResourceCreateStatus.Created, new StoredResource(resource, derived?.Text, derived?.Truncated ?? false));
        }
        catch (StorageWriteException ex)
        {
            health.Report(ex);
            throw;
        }
    }

    /// <summary>Times are stored to the millisecond; the record returned is the one read back later.</summary>
    private static DateTimeOffset StoredTime(DateTimeOffset at) => DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds());

    private static async Task<byte[]?> ReadAllAsync(Stream? stream, CancellationToken cancellationToken)
    {
        if (stream is null)
        {
            return null;
        }

        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
    }

    /// <summary>The file's bytes, or null when it is larger than <paramref name="limit"/> (only the limit plus one byte is ever read).</summary>
    private static async Task<byte[]?> ReadBoundedAsync(string path, long limit, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            if (stream.CanSeek && stream.Length > limit)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > limit)
                {
                    return null;
                }
            }

            return buffer.ToArray();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A file chosen as a resource could not be read")]
    private static partial void LogFileUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sweeping unreferenced resource originals failed; a later sweep retries")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} unsent resources no draft refers to")]
    private static partial void LogAbandoned(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A scaled image could not be kept; it is made again next time")]
    private static partial void LogScaledNotKept(ILogger logger, Exception exception);
}
