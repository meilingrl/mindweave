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
/// whole content; it never changes afterwards. A sweep deletes a file only when neither the database nor any kept backup
/// (nor a database kept by a restore) names its hash: a backup made before a delete can then always be restored whole.
/// </summary>
internal sealed partial class FileResourceBlobStore(AppPaths paths, SqliteDatabase database, TimeProvider time, ILogger<FileResourceBlobStore> logger)
    : IResourceBlobStore, IDisposable
{
    /// <summary>Temporary files older than this are left by an interrupted write and removed by a sweep.</summary>
    internal static readonly TimeSpan StaleTemporaryAge = TimeSpan.FromHours(1);

    private readonly object _lock = new();
    private readonly Dictionary<ContentHash, int> _pinned = [];
    private readonly HashSet<ContentHash> _touched = [];
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
            if (SizeOf(target) == content.Length)
            {
                return new BlobWrite(hash, content.Length, existed: true, () => Unpin(hash));
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

                // Same name, same content: replacing a file of the wrong size (a damaged copy) is safe, and so is losing a
                // race with another write of the same content.
                File.Move(temp, target, overwrite: true);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }

            return new BlobWrite(hash, content.Length, existed: false, () => Unpin(hash));
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
            return new FileStream(PathOf(hash), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public bool Exists(ContentHash hash) => File.Exists(PathOf(hash));

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
        // Every kept copy of the database counts: backups (any reason) and databases a restore kept under a "replaced" name.
        var copies = database.ListBackups().Select(b => b.Path)
            .Concat(database.ListLeftovers().SelectMany(l => l.Files).Where(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal)));
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

        using var command = connection.Command(null, "SELECT DISTINCT sha256 FROM resource;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            hashes.Add(reader.GetString(0));
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

    private static long? SizeOf(string file)
    {
        try
        {
            return new FileInfo(file) is { Exists: true } info ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Deleted} resource originals no database or backup refers to; {Kept} kept")]
    private static partial void LogSwept(ILogger logger, int deleted, int kept);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A kept database copy {Path} could not be read; no resource original was removed")]
    private static partial void LogCopyUnreadable(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete {Path}")]
    private static partial void LogDeleteFailed(ILogger logger, Exception exception, string path);
}
