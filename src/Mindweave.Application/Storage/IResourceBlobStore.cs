using Mindweave.Core.Resources;

namespace Mindweave.Application.Storage;

/// <summary>
/// An original written to the blob store. Until it is disposed its hash is pinned: a sweep running meanwhile keeps the
/// file even though no row refers to it yet. Dispose it after the row that refers to it is committed (or failed).
/// </summary>
public sealed class BlobWrite(ContentHash hash, long size, bool existed, Action release) : IDisposable
{
    private Action? _release = release;

    public ContentHash Hash { get; } = hash;

    public long Size { get; } = size;

    /// <summary>The same content was already stored; nothing new was written.</summary>
    public bool Existed { get; } = existed;

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>What one sweep did.</summary>
public sealed record BlobSweepResult(int Deleted, int Kept, bool Skipped = false);

/// <summary>
/// Resource originals on disk, content-addressed by hash (docs/design/resources.md). Files are written atomically and never
/// change afterwards; the same content is stored once.
/// </summary>
public interface IResourceBlobStore
{
    /// <summary>
    /// Writes <paramref name="content"/> unless the same content is stored already. A storage condition (a full disk, a
    /// folder that cannot be written) is a <see cref="StorageWriteException"/> and leaves nothing behind.
    /// </summary>
    Task<BlobWrite> WriteAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>Opens a stored original for reading, or null when it is not there.</summary>
    Stream? OpenRead(ContentHash hash);

    bool Exists(ContentHash hash);

    /// <summary>
    /// Deletes originals that neither the database nor any kept backup refers to, and temporary files left by an
    /// interrupted write. Conservative: when the database is closed or a backup cannot be read, nothing is deleted.
    /// </summary>
    Task<BlobSweepResult> SweepAsync(CancellationToken cancellationToken);
}
