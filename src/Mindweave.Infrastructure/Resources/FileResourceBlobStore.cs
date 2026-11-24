using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Storage;
using Mindweave.Core.Resources;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Resources;

/// <summary>
/// Resource originals as files under <c>resources/&lt;first two hex digits&gt;/&lt;hash&gt;</c> (docs/design/resources.md).
/// A file is written to <c>resources/tmp</c>, flushed to the disk and renamed into place, so a name always holds the
/// whole content; it never changes afterwards. A sweep deletes a file once the database (or a database kept by a restore)
/// no longer names its hash, as a resource or as a binary derivative (architecture §15.2). The backup made before a delete
/// holds copies of the originals that delete removes (<c>&lt;backup&gt;.resources/</c>, owner's Q4 decision); a restored
/// database finds them there and puts them back when they are first asked for.
/// </summary>
internal sealed partial class FileResourceBlobStore(AppPaths paths, SqliteDatabase database, TimeProvider time, ILogger<FileResourceBlobStore> logger)
    : IResourceBlobStore, IDisposable
{
    /// <summary>Temporary files older than this are left by an interrupted write and removed by a sweep.</summary>
    internal static readonly TimeSpan StaleTemporaryAge = TimeSpan.FromHours(1);

    private readonly object _lock = new();
    private readonly Dictionary<ContentHash, int> _pinned = [];
    private readonly HashSet<ContentHash> _touched = [];

    /// <summary>What a file was found to be, with its length and write time then: read again only once those change.</summary>
    private readonly Dictionary<string, (long Length, DateTime Written, bool Whole)> _checked = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sweepGate = new(1, 1);

    /// <summary>The fan-out folders sit beside <c>tmp</c>, whose name is never two hex digits.</summary>
    private string BlobDirectory => paths.ResourceDirectory;

    private string TemporaryDirectory => Path.Combine(paths.ResourceDirectory, "tmp");

    /// <summary>Test seam: runs after a sweep has read which hashes are referenced and before it deletes anything.</summary>
    internal Action? AfterReferencesReadForTests { get; set; }

    internal string PathOf(ContentHash hash) => Path.Combine(BlobDirectory, hash.Hex[..2], hash.Hex);

    public async Task<BlobWrite> WriteAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var hash = ContentHash.FromBytes(SHA256.HashData(content.Span));
        Pin(hash);
        try
        {
            var target = PathOf(hash);
            if (Exists(hash))
            {
                return new BlobWrite(hash, content.Length, () => Unpin(hash));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.CreateDirectory(TemporaryDirectory);
            var temp = Path.Combine(TemporaryDirectory, Guid.NewGuid().ToString("N"));
            try
            {
                var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                // Same name, same content: replacing a file with other content (a damaged copy) is safe, and so is losing a
                // race with another write of the same content.
                File.Move(temp, target, overwrite: true);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }

            return new BlobWrite(hash, content.Length, () => Unpin(hash));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Unpin(hash);
            var kind = ex is UnauthorizedAccessException ? StorageFailureKind.ReadOnly
                : SqliteDiskProbe.IsDiskFull((IOException)ex) ? StorageFailureKind.Full
                : StorageFailureKind.IoError;
            throw new StorageWriteException(kind, $"Writing a resource original failed: {ex.Message}", ex);
        }
        catch
        {
            Unpin(hash);
            throw;
        }
    }

    public Stream? OpenRead(ContentHash hash)
    {
        try
        {
            return !Exists(hash) ? null : new FileStream(PathOf(hash), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public bool Exists(ContentHash hash) => IsWhole(PathOf(hash), hash);

    /// <summary>
    /// Whether <paramref name="path"/> holds the content its name says (architecture §15.10: a damaged original counts as
    /// missing). A file that cannot be read now is not known to be damaged, so it counts as there.
    /// </summary>
    private bool IsWhole(string path, ContentHash hash)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }

            lock (_lock)
            {
                if (_checked.TryGetValue(path, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc)
                {
                    return known.Whole;
                }
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.SequentialScan);
            var whole = ContentHash.FromBytes(SHA256.HashData(stream)) == hash;
            if (!whole)
            {
                LogDamaged(logger, path);
            }

            lock (_lock)
            {
                _checked[path] = (info.Length, info.LastWriteTimeUtc, whole);
            }

            return whole;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnreadable(logger, ex, path);
            return true;
        }
    }

    public bool TryRecover(ContentHash hash) => RecoverFromBackups(hash);

    public async Task CopyToBackupAsync(string backupPath, IReadOnlyCollection<ContentHash> hashes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(backupPath);
        ArgumentNullException.ThrowIfNull(hashes);
        var folder = BackupResources.DirectoryOf(backupPath);
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var hash in hashes.Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = PathOf(hash);
                if (!File.Exists(source))
                {
                    continue;
                }

                var target = Path.Combine(folder, hash.Hex);
                var temp = target + ".tmp";
                await Task.Run(() => File.Copy(source, temp, overwrite: true), cancellationToken).ConfigureAwait(false);
                File.Move(temp, target, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var kind = ex is UnauthorizedAccessException ? StorageFailureKind.ReadOnly
                : SqliteDiskProbe.IsDiskFull((IOException)ex) ? StorageFailureKind.Full
                : StorageFailureKind.IoError;
            throw new StorageWriteException(kind, $"Copying attachment originals into a backup failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Puts an original back from the copy a backup keeps (a restored database refers to it, a later delete had removed it).
    /// Returns whether it is in the store now.
    /// </summary>
    private bool RecoverFromBackups(ContentHash hash)
    {
        var target = PathOf(hash);
        if (Exists(hash))
        {
            return true;
        }

        if (!Directory.Exists(paths.BackupDirectory))
        {
            return false;
        }

        try
        {
            foreach (var folder in Directory.EnumerateDirectories(paths.BackupDirectory, "*" + BackupResources.Suffix))
            {
                var copy = Path.Combine(folder, hash.Hex);
                if (!IsWhole(copy, hash))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.CreateDirectory(TemporaryDirectory);
                var temp = Path.Combine(TemporaryDirectory, Guid.NewGuid().ToString("N"));
                File.Copy(copy, temp);
                File.Move(temp, target, overwrite: true);
                LogRecovered(logger, hash.Hex);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogDeleteFailed(logger, ex, target);
        }

        return false;
    }

    public void Dispose() => _sweepGate.Dispose();

    public async Task<BlobSweepResult> SweepAsync(CancellationToken cancellationToken)
    {
        await _sweepGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(paths.ResourceDirectory))
            {
                return new BlobSweepResult(0, 0);
            }

            if (!database.IsOpen)
            {
                return new BlobSweepResult(0, 0, Skipped: true);
            }

            lock (_lock)
            {
                // From here on, every hash written (pinned) or committed (unpinned) is remembered, so a row committed after
                // the references below were read can never lose its file.
                _touched.Clear();
            }

            HashSet<string> referenced;
            try
            {
                referenced = await database.ReadAsync(c => Hashes(c), cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return new BlobSweepResult(0, 0, Skipped: true); // Closed meanwhile.
            }

            return await Task.Run(() => SweepFiles(referenced), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sweepGate.Release();
        }
    }

    private BlobSweepResult SweepFiles(HashSet<string> referenced)
    {
        // Databases a restore kept under a "replaced" name count as live: putting one back must find its originals. Backups
        // do not: the one made before a delete keeps its own copies of what the delete removed (owner's Q4 decision).
        var copies = database.ListLeftovers().SelectMany(l => l.Files).Where(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal));
        foreach (var copy in copies)
        {
            try
            {
                using var connection = SqliteConnections.OpenReadOnly(copy);
                referenced.UnionWith(Hashes(connection));
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                // Unsure what it refers to, so nothing is deleted; the next sweep tries again.
                LogCopyUnreadable(logger, ex, copy);
                return new BlobSweepResult(0, 0, Skipped: true);
            }
        }

        AfterReferencesReadForTests?.Invoke();
        RemoveStaleTemporaries();

        int deleted = 0, kept = 0;
        if (!Directory.Exists(BlobDirectory))
        {
            return new BlobSweepResult(0, 0);
        }

        foreach (var file in Directory.EnumerateFiles(BlobDirectory, "*", SearchOption.AllDirectories))
        {
            // Only files this store could have written are considered; anything else in the folder is left alone.
            if (!ContentHash.TryParse(Path.GetFileName(file), out var hash) || !string.Equals(file, PathOf(hash), StringComparison.Ordinal))
            {
                continue;
            }

            if (referenced.Contains(hash.Hex))
            {
                kept++;
                continue;
            }

            lock (_lock)
            {
                if (_pinned.ContainsKey(hash) || _touched.Contains(hash))
                {
                    kept++;
                    continue;
                }

                if (TryDelete(file))
                {
                    deleted++;
                }
            }
        }

        if (deleted > 0)
        {
            LogSwept(logger, deleted, kept);
        }

        return new BlobSweepResult(deleted, kept);
    }

    private static HashSet<string> Hashes(SqliteConnection connection)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);

        // A copy made before resources existed has no table and refers to nothing.
        if (connection.Scalar<long>(null, "SELECT EXISTS (SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'resource');") == 0)
        {
            return hashes;
        }

        using (var command = connection.Command(null, "SELECT DISTINCT sha256 FROM resource;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                hashes.Add(reader.GetString(0));
            }
        }

        // Binary derivatives (a scaled image, v16) are blobs too.
        if (connection.Scalar<long>(null, "SELECT EXISTS (SELECT 1 FROM pragma_table_info('resource_derivative') WHERE name = 'blob_sha256');") != 0)
        {
            using var derived = connection.Command(null, "SELECT DISTINCT blob_sha256 FROM resource_derivative WHERE blob_sha256 IS NOT NULL;");
            using var reader = derived.ExecuteReader();
            while (reader.Read())
            {
                hashes.Add(reader.GetString(0));
            }
        }

        return hashes;
    }

    private void RemoveStaleTemporaries()
    {
        if (!Directory.Exists(TemporaryDirectory))
        {
            return;
        }

        var cutoff = time.GetUtcNow() - StaleTemporaryAge;
        foreach (var file in Directory.EnumerateFiles(TemporaryDirectory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                {
                    TryDelete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void Pin(ContentHash hash)
    {
        lock (_lock)
        {
            _pinned[hash] = _pinned.GetValueOrDefault(hash) + 1;
            _touched.Add(hash);
        }
    }

    private void Unpin(ContentHash hash)
    {
        lock (_lock)
        {
            if (_pinned[hash] == 1)
            {
                _pinned.Remove(hash);
            }
            else
            {
                _pinned[hash]--;
            }

            _touched.Add(hash);
        }
    }

    private bool TryDelete(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogDeleteFailed(logger, ex, file);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Put resource original {Hash} back from a backup")]
    private static partial void LogRecovered(ILogger logger, string hash);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The resource original {Path} is damaged: its content is not what its name says")]
    private static partial void LogDamaged(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the resource original {Path}")]
    private static partial void LogUnreadable(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Deleted} resource originals the database no longer refers to; {Kept} kept")]
    private static partial void LogSwept(ILogger logger, int deleted, int kept);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A kept database copy {Path} could not be read; no resource original was removed")]
    private static partial void LogCopyUnreadable(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete {Path}")]
    private static partial void LogDeleteFailed(ILogger logger, Exception exception, string path);
}
