using Microsoft.Extensions.Logging;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Resources;

public enum ResourceCreateStatus
{
    Created,

    /// <summary>Nothing to store: an empty paste or an empty file.</summary>
    Empty,

    /// <summary>Larger than <see cref="ResourceLimits.MaxOriginalBytes"/>.</summary>
    TooLarge,

    /// <summary>A kind of file that is not supported yet (PDF, Office, archives…).</summary>
    Unsupported,

    /// <summary>A text file's name, but its bytes are not text in any encoding read here; or an image name with other bytes.</summary>
    NotReadable,

    /// <summary>The file could not be opened or read (gone, no permission).</summary>
    FileUnavailable,
}

/// <summary>Outcome of creating a resource; <paramref name="Resource"/> is set only when it was created.</summary>
public sealed record ResourceCreateResult(ResourceCreateStatus Status, StoredResource? Resource = null, string? Detail = null)
{
    public bool IsCreated => Status == ResourceCreateStatus.Created;
}

public static class ResourceLimits
{
    /// <summary>The largest original stored (32 MiB). Text this long is far beyond any context window; it is kept for chunked retrieval later.</summary>
    public const long MaxOriginalBytes = 32L * 1024 * 1024;
}

/// <summary>Creating and reading resources (docs/design/resources.md). No user interface uses this yet.</summary>
public interface IResources
{
    /// <summary>A pasted text as an unnamed resource: its original is the UTF-8 bytes, its derived text the paste itself.</summary>
    Task<ResourceCreateResult> CreateFromPasteAsync(WorkspaceId workspace, ConversationId? conversation, string text, CancellationToken cancellationToken);

    /// <summary>A file as a resource: text and source code with their text, images as originals only.</summary>
    Task<ResourceCreateResult> CreateFromFileAsync(WorkspaceId workspace, ConversationId? conversation, string path, CancellationToken cancellationToken);

    Task<StoredResource?> GetAsync(ResourceId id, CancellationToken cancellationToken);

    /// <summary>The original's bytes as a stream, or null when the resource or its file is gone.</summary>
    Task<Stream?> OpenOriginalAsync(ResourceId id, CancellationToken cancellationToken);

    Task<ResourceDeleteStatus> DeleteUnattachedAsync(ResourceId id, CancellationToken cancellationToken);
}

public sealed partial class ResourceService(
    IResourceStore store,
    IResourceBlobStore blobs,
    IEnumerable<ITextExtractor> extractors,
    StorageHealth health,
    IClock clock,
    ILogger<ResourceService> logger) : IResources
{
    private readonly ITextExtractor[] _extractors = [.. extractors];

    /// <summary>The block a message uses to refer to <paramref name="resource"/>.</summary>
    public static AttachmentBlock BlockFor(StoredResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var r = resource.Resource;
        return new AttachmentBlock(r.Id, r.Name, r.MediaType, r.Size, resource.Text);
    }

    public Task<ResourceCreateResult> CreateFromPasteAsync(WorkspaceId workspace, ConversationId? conversation, string text, CancellationToken cancellationToken)
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

        var derived = new ResourceDerivative(DerivativeKind.Text, PlainTextExtractor.NormalizeLineEndings(text), "paste/1");
        return StoreAsync(workspace, conversation, bytes, MediaTypes.PlainText, ResourceOrigin.Paste, null, derived, cancellationToken);
    }

    public async Task<ResourceCreateResult> CreateFromFileAsync(WorkspaceId workspace, ConversationId? conversation, string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var name = Path.GetFileName(path);
        if (FileTypes.MediaTypeOf(name) is not { } mediaType)
        {
            return new ResourceCreateResult(ResourceCreateStatus.Unsupported, Detail: Path.GetExtension(name));
        }

        byte[] bytes;
        try
        {
            if (await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false) is not { } read)
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
            // The bytes decide the type: a ".jpg" that is really a PNG is stored as a PNG; one that is no image is refused.
            return FileTypes.ImageTypeOf(bytes) is { } actual
                ? await StoreAsync(workspace, conversation, bytes, actual, ResourceOrigin.File, name, null, cancellationToken).ConfigureAwait(false)
                : new ResourceCreateResult(ResourceCreateStatus.NotReadable);
        }

        if (_extractors.FirstOrDefault(e => e.CanExtract(mediaType)) is not { } extractor)
        {
            return new ResourceCreateResult(ResourceCreateStatus.Unsupported, Detail: mediaType);
        }

        if (extractor.Extract(bytes, mediaType) is not { } text)
        {
            return new ResourceCreateResult(ResourceCreateStatus.NotReadable);
        }

        var derived = new ResourceDerivative(DerivativeKind.Text, text, extractor.Producer);
        return await StoreAsync(workspace, conversation, bytes, mediaType, ResourceOrigin.File, name, derived, cancellationToken).ConfigureAwait(false);
    }

    public Task<StoredResource?> GetAsync(ResourceId id, CancellationToken cancellationToken) => store.GetAsync(id, cancellationToken);

    public async Task<Stream?> OpenOriginalAsync(ResourceId id, CancellationToken cancellationToken)
    {
        var resource = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return resource is null ? null : blobs.OpenRead(resource.Resource.Hash);
    }

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

    private async Task<ResourceCreateResult> StoreAsync(
        WorkspaceId workspace,
        ConversationId? conversation,
        byte[] original,
        string mediaType,
        ResourceOrigin origin,
        string? fileName,
        ResourceDerivative? derived,
        CancellationToken cancellationToken)
    {
        health.ThrowIfBlocked();
        try
        {
            // The original first, then the row, while the hash is pinned so a sweep cannot take the file in between.
            using var blob = await blobs.WriteAsync(original, cancellationToken).ConfigureAwait(false);
            var resource = new Resource(
                ResourceId.New(), workspace, conversation, blob.Hash, mediaType, blob.Size, origin, fileName, null, Trusted: true, StoredTime(clock.UtcNow));
            await store.InsertAsync(resource, derived is null ? [] : [derived], cancellationToken).ConfigureAwait(false);
            return new ResourceCreateResult(ResourceCreateStatus.Created, new StoredResource(resource, derived?.Text));
        }
        catch (StorageWriteException ex)
        {
            health.Report(ex);
            throw;
        }
    }

    /// <summary>Times are stored to the millisecond; the record returned is the one read back later.</summary>
    private static DateTimeOffset StoredTime(DateTimeOffset at) => DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds());

    /// <summary>The file's bytes, or null when it is larger than the limit (only the limit plus one byte is ever read).</summary>
    private static async Task<byte[]?> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            if (stream.CanSeek && stream.Length > ResourceLimits.MaxOriginalBytes)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > ResourceLimits.MaxOriginalBytes)
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
}
