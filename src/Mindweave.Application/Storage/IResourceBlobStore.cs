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
    /// Puts back an original missing from the store from the copy a backup keeps (a database restored from the backup made
    /// before a delete names it again). Only asked for originals a stored resource names. Returns whether it is there now.
    /// </summary>
    bool TryRecover(ContentHash hash);

    /// <summary>
    /// Copies the originals named by <paramref name="hashes"/> beside the backup at <paramref name="backupPath"/> (owner's Q4
    /// decision 2026-11-17: the backup made before a delete includes the attachment files being deleted). Hashes not in the
    /// store are skipped. A storage condition is a <see cref="StorageWriteException"/>; the copy is then incomplete and the
    /// caller drops the backup.
    /// </summary>
    Task CopyToBackupAsync(string backupPath, IReadOnlyCollection<ContentHash> hashes, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes originals the database no longer refers to (nor a database a restore kept aside), and temporary files left by
    /// an interrupted write (architecture §15.2). A backup made before a delete keeps its own copies of what that delete
    /// removed. Conservative: when the database is closed or a kept database cannot be read, nothing is deleted.
    /// </summary>
    Task<BlobSweepResult> SweepAsync(CancellationToken cancellationToken);
}
