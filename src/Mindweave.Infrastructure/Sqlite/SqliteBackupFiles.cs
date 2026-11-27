using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Startup;
using Mindweave.Infrastructure.Hosting;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>
/// The backup files of one data directory: their name scheme, listing, discarding and pruning, writing a verified copy
/// of a database, and swapping a backup in for the database file. Holds no lifecycle state; the caller decides when
/// the database is closed or which connection a copy is made from.
/// </summary>
internal sealed partial class SqliteBackupFiles(AppPaths paths, TimeProvider time, ILogger logger)
{
    private const string StampFormat = "yyyyMMdd'T'HHmmssfff'Z'";
    private const string BeforeDeleteSuffix = "-before-delete";
    private const string BeforeConversationDeleteSuffix = "-before-conversation-delete";
    private const string BeforeRestoreSuffix = "-before-restore";
    private const string ReplacedInfix = ".replaced-";

    private static readonly Regex BackupName = BackupNameRegex();

    private string? _writing;
    private long _expectedBytes;

    /// <summary>
    /// How far the copy being written is: the size of its file so far against the pages in use in the source, from 0 to
    /// 1; null when no copy is being written (performance-7, D-042).
    /// </summary>
    public double? Progress
    {
        get
        {
            var writing = Volatile.Read(ref _writing);
            var expected = Interlocked.Read(ref _expectedBytes);
            if (writing is null || expected <= 0)
            {
                return null;
            }

            return Math.Clamp((double)(SizeOf(writing) ?? 0) / expected, 0, 1);
        }
    }

    /// <summary>What one restore moved aside: each current file and its "replaced" name.</summary>
    internal sealed record Replacement(IReadOnlyList<(string From, string To)> Moved);

    public IReadOnlyList<BackupInfo> List()
    {
        if (!Directory.Exists(paths.BackupDirectory))
        {
            return [];
        }

        var backups = new List<BackupInfo>();
        foreach (var file in Directory.EnumerateFiles(paths.BackupDirectory, "mindweave-v*.db"))
        {
            var match = BackupName.Match(Path.GetFileName(file));
            if (!match.Success)
            {
                continue;
            }

            // A name that only looks right (a version too large, an impossible date) or a file gone meanwhile is skipped:
            // listing runs on the recovery page and the storage page, and must not fail there (low findings
            // architecture-1, errors-8).
            if (!int.TryParse(match.Groups["version"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                || !DateTimeOffset.TryParseExact(match.Groups["time"].Value, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created)
                || SizeOf(file) is not { } size)
            {
                continue;
            }

            var reason = ReasonOf(match.Groups["reason"].Value);
            backups.Add(new BackupInfo(file, version, created, size + Resources.BackupResources.SizeFor(file), reason));
        }

        return backups.OrderByDescending(b => b.CreatedAt).ToArray();
    }

    /// <summary>Deletes one backup of this data directory, best effort (a failure is logged); any other path is refused.</summary>
    public void Discard(string path)
    {
        var full = BackupPathOf(path);
        try
        {
            File.Delete(full);
            Resources.BackupResources.DeleteFor(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogDiscardFailed(logger, ex, full);
        }
    }

    /// <summary>Deletes one backup of this data directory at the user's request; any other path is refused, a failure is thrown.</summary>
    public void Delete(string path)
    {
        var full = BackupPathOf(path);
        File.Delete(full);
        Resources.BackupResources.DeleteFor(full);
        LogDeleted(logger, full);
    }

    /// <summary>
    /// Reads a backup without changing it (opened read-only): it exists, carries Mindweave's application id and a schema
    /// version no newer than <paramref name="latestVersion"/>, and passes <c>quick_check</c>.
    /// </summary>
    public BackupProblem Check(BackupInfo backup, int latestVersion)
    {
        ArgumentNullException.ThrowIfNull(backup);
        var full = SourcePathOf(backup);
        if (!File.Exists(full))
        {
            return BackupProblem.Missing;
        }

        try
        {
            using var connection = SqliteConnections.OpenReadOnly(full);
            if (SqliteConnections.Scalar<int>(connection, "PRAGMA application_id;") != SqliteConnections.ApplicationId)
            {
                return BackupProblem.NotMindweave;
            }

            if (SqliteConnections.Scalar<int>(connection, "PRAGMA user_version;") > latestVersion)
            {
                return BackupProblem.TooNew;
            }

            return SqliteConnections.QuickCheck(connection) is null ? BackupProblem.None : BackupProblem.Unreadable;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            LogCheckFailed(logger, ex, full);
            return BackupProblem.Unreadable;
        }
    }

    /// <summary>
    /// The files earlier restores kept under a "replaced" name in the data directory, one entry per restore (the database
    /// with its log files), newest first.
    /// </summary>
    public IReadOnlyList<LeftoverDatabase> ListLeftovers()
    {
        if (!Directory.Exists(paths.DataDirectory))
        {
            return [];
        }

        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(paths.DataDirectory, Path.GetFileName(paths.DatabasePath) + ReplacedInfix + "*"))
        {
            if (LeftoverStampOf(file) is { } stamp)
            {
                if (!groups.TryGetValue(stamp, out var files))
                {
                    groups[stamp] = files = [];
                }

                files.Add(file);
            }
        }

        return groups
            .Where(g => DateTimeOffset.TryParseExact(g.Key, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            .Select(g => new LeftoverDatabase(
                DateTimeOffset.ParseExact(g.Key, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                [.. g.Value.Order(StringComparer.Ordinal)],
                g.Value.Sum(f => SizeOf(f) ?? 0),
                HeaderVersionOf(g.Value.FirstOrDefault(IsMainFile))))
            .OrderByDescending(l => l.ReplacedAt)
            .ToArray();
    }

    /// <summary>
    /// The schema version in a database file's header (its <c>user_version</c>, bytes 60–63, big-endian), read without
    /// opening it: listing must not touch the file. Null when there is no such file or it is not Mindweave's (application
    /// id, bytes 68–71). A version changed only in the file's write-ahead log is not seen here; the check before a restore
    /// reads the file properly.
    /// </summary>
    private static int? HeaderVersionOf(string? file)
    {
        if (file is null)
        {
            return null;
        }

        try
        {
            Span<byte> header = stackalloc byte[100];
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.ReadExactly(header);
            if (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[68..]) != SqliteConnections.ApplicationId)
            {
                return null;
            }

            return System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[60..]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The database file of a leftover group, not one of its log files.</summary>
    private static bool IsMainFile(string file) => !file.EndsWith("-wal", StringComparison.Ordinal) && !file.EndsWith("-shm", StringComparison.Ordinal);

    /// <summary>The file's size, or null when it is gone or cannot be read.</summary>
    private static long? SizeOf(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Deletes the files of one leftover entry; a path that is not a leftover of this data directory is refused before anything is deleted.</summary>
    public void Delete(LeftoverDatabase leftover)
    {
        ArgumentNullException.ThrowIfNull(leftover);
        var files = leftover.Files.Select(Path.GetFullPath).ToArray();
        foreach (var file in files)
        {
            if (!string.Equals(Path.GetDirectoryName(file), Path.GetFullPath(paths.DataDirectory), StringComparison.Ordinal) || LeftoverStampOf(file) is null)
            {
                throw new ArgumentException("Not a leftover database file of this data directory.", nameof(leftover));
            }
        }

        foreach (var file in files)
        {
            File.Delete(file);
            LogDeleted(logger, file);
        }
    }

    /// <summary>Keeps the newest <paramref name="keep"/> upgrade backups; backups of any other reason are never pruned.</summary>
    /// <param name="alsoKeep">A backup always among the kept, whatever its place in the order (the one just made for a failed upgrade).</param>
    public void Prune(int keep, string? alsoKeep = null)
    {
        try
        {
            // Only upgrade backups rotate. One made before a workspace was deleted is the only copy of that workspace,
            // so it stays until the user removes it by hand, and it never pushes an upgrade backup out (round 4 review).
            // The one to keep counts first among the kept, whatever its time says.
            var upgrades = List().Where(b => b.Reason == BackupReason.Migration).OrderByDescending(b => b.Path == alsoKeep);
            foreach (var old in upgrades.Skip(keep))
            {
                File.Delete(old.Path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogPruneFailed(logger, ex);
        }
    }

    /// <summary>
    /// Writes a verified copy of the database open on <paramref name="connection"/> at schema <paramref name="version"/>
    /// and returns its path. <paramref name="beforeWrite"/> is the test host's fault-injection seam; it runs with the
    /// temporary path right before the copy is written.
    /// </summary>
    public string CreateVerified(SqliteConnection connection, int version, BackupReason reason, Action<SqliteConnection, string>? beforeWrite)
    {
        Directory.CreateDirectory(paths.BackupDirectory);
        var stamp = time.GetUtcNow().UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture);
        var final = Path.Combine(paths.BackupDirectory, $"mindweave-v{version}-{stamp}{SuffixOf(reason)}.db");
        if (File.Exists(final))
        {
            throw new IOException($"Backup target already exists: {final}");
        }

        // A process killed while writing a backup leaves its temporary file under an earlier time stamp. It is never
        // listed (the name does not end in ".db"), but it would take disk space forever.
        foreach (var stale in Directory.EnumerateFiles(paths.BackupDirectory, "mindweave-v*.db.partial*"))
        {
            TryDelete(stale);
        }

        var partial = final + ".partial";
        DeleteDatabaseFiles(partial);
        try
        {
            beforeWrite?.Invoke(connection, partial);

            var pageSize = SqliteConnections.Scalar<long>(connection, "PRAGMA page_size;");
            var inUse = SqliteConnections.Scalar<long>(connection, "PRAGMA page_count;") - SqliteConnections.Scalar<long>(connection, "PRAGMA freelist_count;");
            Interlocked.Exchange(ref _expectedBytes, inUse * pageSize);
            Volatile.Write(ref _writing, partial);

            // VACUUM INTO writes a transactionally consistent copy, including content still in the WAL.
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "VACUUM INTO $path;";
                command.Parameters.AddWithValue("$path", partial);
                command.ExecuteNonQuery();
            }
            finally
            {
                Volatile.Write(ref _writing, null);
            }

            using (var copy = SqliteConnections.OpenReadWrite(partial))
            {
                if (SqliteConnections.QuickCheck(copy) is { } problem)
                {
                    throw new InvalidOperationException($"Backup failed its integrity check: {problem}");
                }

                if (SqliteConnections.Scalar<int>(copy, "PRAGMA user_version;") != version
                    || SqliteConnections.Scalar<int>(copy, "PRAGMA application_id;") != SqliteConnections.ApplicationId)
                {
                    throw new InvalidOperationException("Backup header does not match the source database.");
                }
            }

            File.Move(partial, final);
            return final;
        }
        catch
        {
            DeleteDatabaseFiles(partial);
            throw;
        }
    }

    /// <summary>
    /// Puts a copy of <paramref name="backup"/> in place of the database file, which must be closed. The current file
    /// and its WAL are kept under a "replaced" name and returned; a failure moves everything back and rethrows.
    /// <paramref name="beforeSwap"/> is the test host's fault-injection seam, run after the copy is made and before
    /// anything is moved.
    /// </summary>
    public Replacement ReplaceDatabaseWith(BackupInfo backup, Action? beforeSwap = null)
    {
        var source = SourcePathOf(backup);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Backup file not found.", source);
        }

        var stamp = time.GetUtcNow().UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture);
        var incoming = paths.DatabasePath + ".restoring";
        DeleteDatabaseFiles(incoming);
        if (backup.Reason == BackupReason.Replaced)
        {
            CopyReplaced(source, incoming);
        }
        else
        {
            CopyFlushed(source, incoming);
        }

        // Keep the current database (and any WAL) under a "replaced" name; nothing is deleted. Any step can fail (on
        // Windows a file still open or mapped elsewhere cannot be renamed); then everything already moved goes back,
        // since the next start would otherwise find no database and create an empty one in its place.
        var moved = new List<(string From, string To)>();
        try
        {
            beforeSwap?.Invoke();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var current = paths.DatabasePath + suffix;
                if (File.Exists(current))
                {
                    var replaced = $"{paths.DatabasePath}{ReplacedInfix}{stamp}{suffix}";
                    File.Move(current, replaced);
                    moved.Add((current, replaced));
                }
            }

            File.Move(incoming, paths.DatabasePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            for (var i = moved.Count - 1; i >= 0; i--)
            {
                try
                {
                    File.Move(moved[i].To, moved[i].From);
                }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
                {
                    LogRestoreUndoFailed(logger, undo, moved[i].To, moved[i].From);
                }
            }

            TryDelete(incoming);
            throw;
        }

        LogRestored(logger, backup.Path);
        return new Replacement(moved);
    }

    /// <summary>
    /// Whether a restore was interrupted in the middle of its swap: the copy being put in place is still there.
    /// Only the copy being put in place counts: it exists exactly while a swap is under way, whereas databases kept under a
    /// "replaced" name stay after any finished restore (review 2026-11-14).
    public bool HasRestoreLeftovers() => File.Exists(paths.DatabasePath + ".restoring");

    /// <summary>
    /// Copies <paramref name="from"/> and flushes the copy through to the disk before it is renamed into place (health
    /// check 2026-11-12, storage-4): a crash right after the swap must not leave a database file whose content never
    /// reached the disk.
    /// </summary>
    private static void CopyFlushed(string from, string to)
    {
        using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var target = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
        target.Flush(flushToDisk: true);
    }

    /// <summary>
    /// A replaced database may have content in its own write-ahead log ("…-wal" beside it, which SQLite finds by name), so
    /// it is not copied as a file: <c>VACUUM INTO</c> from a read-only connection writes one self-contained copy, which
    /// is then flushed to the disk like any copy put in place (gap-schema-5, D-042).
    /// </summary>
    private static void CopyReplaced(string from, string to)
    {
        try
        {
            using (var connection = SqliteConnections.OpenReadOnly(from))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "VACUUM INTO $path;";
                command.Parameters.AddWithValue("$path", to);
                command.ExecuteNonQuery();
            }

            using var written = new FileStream(to, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            written.Flush(flushToDisk: true);
        }
        catch (SqliteException ex)
        {
            DeleteDatabaseFiles(to);
            throw new IOException($"The replaced database could not be copied: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// After the restored copy failed to open: puts the replaced database back. The copy's own log files go first, then
    /// the database file is moved back over the copy (one rename, so a database file is always in place), then its log
    /// files. Throws when a step fails; the file in place is then the copy or the original, never nothing.
    /// </summary>
    public void PutBack(Replacement replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            File.Delete(paths.DatabasePath + suffix);
        }

        // The database file (the shortest name) first, then its -wal and -shm.
        foreach (var (from, to) in replacement.Moved.OrderBy(m => m.From.Length))
        {
            File.Move(to, from, overwrite: true);
        }

        LogPutBack(logger);
    }

    /// <summary>Removes the files a successful restore kept under a "replaced" name; best effort, a failure is logged.</summary>
    public void DropReplaced(Replacement replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        foreach (var (_, to) in replacement.Moved)
        {
            TryDelete(to);
        }
    }

    /// <summary>Deletes a database file together with its WAL, shared-memory and rollback-journal files.</summary>
    internal static void DeleteDatabaseFiles(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            File.Delete(path + suffix);
        }
    }

    private static string SuffixOf(BackupReason reason) => reason switch
    {
        BackupReason.BeforeWorkspaceDelete => BeforeDeleteSuffix,
        BackupReason.BeforeConversationDelete => BeforeConversationDeleteSuffix,
        BackupReason.BeforeRestore => BeforeRestoreSuffix,
        _ => string.Empty,
    };

    private static BackupReason ReasonOf(string suffix) => suffix switch
    {
        BeforeDeleteSuffix => BackupReason.BeforeWorkspaceDelete,
        BeforeConversationDeleteSuffix => BackupReason.BeforeConversationDelete,
        BeforeRestoreSuffix => BackupReason.BeforeRestore,
        _ => BackupReason.Migration,
    };

    /// <summary>
    /// The file a restore reads: a backup in the backup directory, or a database an earlier restore replaced (reason
    /// <see cref="BackupReason.Replaced"/>), which lives in the data directory. Anything else is refused.
    /// </summary>
    private string SourcePathOf(BackupInfo backup)
    {
        if (backup.Reason != BackupReason.Replaced)
        {
            return BackupPathOf(backup.Path);
        }

        ArgumentException.ThrowIfNullOrEmpty(backup.Path);
        var full = Path.GetFullPath(backup.Path);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(paths.DataDirectory), StringComparison.Ordinal)
            || LeftoverStampOf(full) is null
            || !IsMainFile(full))
        {
            throw new ArgumentException("Not a replaced database of this data directory.", nameof(backup));
        }

        return full;
    }

    /// <summary>The replaced database a <see cref="BackupReason.Replaced"/> entry names, with its log files; null when it is gone.</summary>
    public LeftoverDatabase? LeftoverOf(BackupInfo backup)
    {
        var full = SourcePathOf(backup);
        return ListLeftovers().FirstOrDefault(l => l.Files.Contains(full, StringComparer.Ordinal));
    }

    /// <summary>A backup's full path, refused (<see cref="ArgumentException"/>) unless it names a backup of this data directory.</summary>
    private string BackupPathOf(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(paths.BackupDirectory), StringComparison.Ordinal)
            || !BackupNameRegex().IsMatch(Path.GetFileName(full)))
        {
            throw new ArgumentException("Not a backup of this data directory.", nameof(path));
        }

        return full;
    }

    /// <summary>The time stamp of a "replaced" database file of this data directory ("mindweave.db.replaced-…[-wal|-shm]"), or null.</summary>
    private string? LeftoverStampOf(string file)
    {
        var name = Path.GetFileName(file);
        var prefix = Path.GetFileName(paths.DatabasePath) + ReplacedInfix;
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var match = LeftoverSuffixRegex().Match(name[prefix.Length..]);
        return match.Success ? match.Groups["time"].Value : null;
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogDeleteFailed(logger, ex, path);
        }
    }

    [GeneratedRegex(@"^mindweave-v(?<version>\d+)-(?<time>\d{8}T\d{9}Z)(?<reason>-before-delete|-before-conversation-delete|-before-restore)?\.db$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupNameRegex();

    [GeneratedRegex(@"^(?<time>\d{8}T\d{9}Z)(-wal|-shm)?$", RegexOptions.CultureInvariant)]
    private static partial Regex LeftoverSuffixRegex();

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Path} at the user's request")]
    private static partial void LogDeleted(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup {Path} could not be read")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The restored copy could not be opened; the previous database was put back")]
    private static partial void LogPutBack(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Old migration backups could not be pruned")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unneeded backup {Path} could not be removed")]
    private static partial void LogDiscardFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restored database from {BackupPath}")]
    private static partial void LogRestored(ILogger logger, string backupPath);

    [LoggerMessage(Level = LogLevel.Critical, Message = "A failed restore could not move {From} back to {To}; move it back by hand before starting again")]
    private static partial void LogRestoreUndoFailed(ILogger logger, Exception exception, string from, string to);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Leftover file {Path} could not be deleted")]
    private static partial void LogDeleteFailed(ILogger logger, Exception exception, string path);
}
