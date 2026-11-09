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

    private static readonly Regex BackupName = BackupNameRegex();

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

            var version = int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture);
            var created = DateTimeOffset.ParseExact(match.Groups["time"].Value, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            var reason = ReasonOf(match.Groups["reason"].Value);
            backups.Add(new BackupInfo(file, version, created, new FileInfo(file).Length, reason));
        }

        return backups.OrderByDescending(b => b.CreatedAt).ToArray();
    }

    /// <summary>Deletes one backup of this data directory; any other path is refused.</summary>
    public void Discard(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(paths.BackupDirectory), StringComparison.Ordinal)
            || !BackupNameRegex().IsMatch(Path.GetFileName(full)))
        {
            throw new ArgumentException("Not a backup of this data directory.", nameof(path));
        }

        try
        {
            File.Delete(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogDiscardFailed(logger, ex, full);
        }
    }

    /// <summary>Keeps the newest <paramref name="keep"/> upgrade backups; backups of any other reason are never pruned.</summary>
    public void Prune(int keep)
    {
        try
        {
            // Only upgrade backups rotate. One made before a workspace was deleted is the only copy of that workspace,
            // so it stays until the user removes it by hand, and it never pushes an upgrade backup out (round 4 review).
            foreach (var old in List().Where(b => b.Reason == BackupReason.Migration).Skip(keep))
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

            // VACUUM INTO writes a transactionally consistent copy, including content still in the WAL.
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "VACUUM INTO $path;";
                command.Parameters.AddWithValue("$path", partial);
                command.ExecuteNonQuery();
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
    /// and its WAL are kept under a "replaced" name; a failure moves everything back and rethrows.
    /// </summary>
    public void ReplaceDatabaseWith(BackupInfo backup)
    {
        if (!File.Exists(backup.Path))
        {
            throw new FileNotFoundException("Backup file not found.", backup.Path);
        }

        var stamp = time.GetUtcNow().UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture);
        var incoming = paths.DatabasePath + ".restoring";
        DeleteDatabaseFiles(incoming);
        File.Copy(backup.Path, incoming);

        // Keep the current database (and any WAL) under a "replaced" name; nothing is deleted. Any step can fail (on
        // Windows a file still open or mapped elsewhere cannot be renamed); then everything already moved goes back,
        // since the next start would otherwise find no database and create an empty one in its place.
        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var current = paths.DatabasePath + suffix;
                if (File.Exists(current))
                {
                    var replaced = $"{paths.DatabasePath}.replaced-{stamp}{suffix}";
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
        _ => string.Empty,
    };

    private static BackupReason ReasonOf(string suffix) => suffix switch
    {
        BeforeDeleteSuffix => BackupReason.BeforeWorkspaceDelete,
        BeforeConversationDeleteSuffix => BackupReason.BeforeConversationDelete,
        _ => BackupReason.Migration,
    };

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

    [GeneratedRegex(@"^mindweave-v(?<version>\d+)-(?<time>\d{8}T\d{9}Z)(?<reason>-before-delete|-before-conversation-delete)?\.db$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupNameRegex();

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
