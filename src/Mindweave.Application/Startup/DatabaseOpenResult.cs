namespace Mindweave.Application.Startup;

public enum DatabaseOpenStatus
{
    /// <summary>The database is open at the current schema version and accepts writes.</summary>
    Ready,

    /// <summary>The file is not a readable SQLite database, or its integrity check failed.</summary>
    Unreadable,

    /// <summary>A valid SQLite database that does not belong to Mindweave.</summary>
    ForeignDatabase,

    /// <summary>Written by a newer Mindweave; this build refuses to write to it.</summary>
    TooNew,

    /// <summary>A migration was needed but the pre-migration backup could not be made or verified. Nothing was migrated.</summary>
    BackupFailed,

    /// <summary>Migration failed and was rolled back; the database is still at its previous version.</summary>
    MigrationFailed,

    /// <summary>The data directory or database file could not be accessed.</summary>
    IoError,

    /// <summary>Another program holds the database (health check 2026-11-12): nothing is wrong with the file; retry once it lets go.</summary>
    InUse,
}

public sealed record DatabaseOpenResult(
    DatabaseOpenStatus Status,
    string DataDirectory,
    string DatabasePath,
    int? FoundVersion,
    int SupportedVersion,
    string? BackupPath = null,
    string? ErrorDetail = null)
{
    public bool IsReady => Status == DatabaseOpenStatus.Ready;
}

/// <summary>
/// Why a backup was made: before a schema upgrade, before a workspace was deleted (S7 round 4), before a conversation
/// delete that took branches or summaries along (requirements v0.12 §8.2), or before a backup was restored in the running
/// application (2026-11-12). Only upgrade backups are rotated.
/// </summary>
public enum BackupReason
{
    Migration,
    BeforeWorkspaceDelete,
    BeforeConversationDelete,
    BeforeRestore,
}

/// <summary>Why a backup cannot be restored, as checked before anything is changed.</summary>
public enum BackupProblem
{
    None,

    /// <summary>The file is gone.</summary>
    Missing,

    /// <summary>Not a readable SQLite database, or its integrity check failed.</summary>
    Unreadable,

    /// <summary>A database that is not Mindweave's.</summary>
    NotMindweave,

    /// <summary>Written by a newer Mindweave than this build.</summary>
    TooNew,
}

/// <summary>
/// The database files one earlier restore put aside ("mindweave.db.replaced-…", with its log files): unlisted by the
/// backups, kept until removed by hand. <paramref name="Files"/> are full paths in the data directory.
/// </summary>
public sealed record LeftoverDatabase(DateTimeOffset ReplacedAt, IReadOnlyList<string> Files, long SizeBytes);

/// <summary>Why an in-app restore did not happen; the current data was put back (or never touched).</summary>
public enum RestoreFailure
{
    None,

    /// <summary>The chosen backup failed its check when the restore began; nothing was replaced.</summary>
    BackupUnusable,

    /// <summary>Moving the files failed (for example a file held open elsewhere); everything was moved back.</summary>
    SwapFailed,

    /// <summary>The restored copy could not be opened or upgraded; the previous database was put back.</summary>
    RestoredUnusable,

    /// <summary>The previous database could not be put back after a failure; the recovery page takes over.</summary>
    PutBackFailed,
}

/// <param name="Restored">The backup is the database now.</param>
/// <param name="Open">The last open: of the restored copy, or of the previous database after a failure.</param>
public sealed record InPlaceRestoreResult(bool Restored, DatabaseOpenResult Open, RestoreFailure Failure = RestoreFailure.None, string? Detail = null);

public sealed record BackupInfo(string Path, int SchemaVersion, DateTimeOffset CreatedAt, long SizeBytes, BackupReason Reason = BackupReason.Migration);

/// <summary>A verified copy of the open database, made on the single writer between transactions.</summary>
public interface IDatabaseBackup
{
    /// <summary>
    /// Writes a transactionally consistent copy into the backup directory, checks it, and returns its path. Throws when
    /// the copy could not be made or failed its check; nothing is left behind then.
    /// </summary>
    Task<string> BackupAsync(BackupReason reason, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a backup made by <see cref="BackupAsync"/> that turned out not to be needed (the delete it was made for
    /// was refused after all). Best effort: a file that cannot be removed is left and logged.
    /// </summary>
    void Discard(string path);
}

/// <summary>Opening, migrating and restoring the local database. Implemented by Infrastructure.</summary>
public interface IDatabaseLifecycle
{
    Task<DatabaseOpenResult> OpenAsync(CancellationToken cancellationToken);

    IReadOnlyList<BackupInfo> ListBackups();

    /// <summary>
    /// Keeps the current database file under a "replaced" name, copies the backup into place,
    /// then opens it exactly as on startup. Only ever called after an explicit user choice.
    /// </summary>
    Task<DatabaseOpenResult> RestoreAsync(BackupInfo backup, CancellationToken cancellationToken);

    /// <summary>
    /// Restoring from inside the running application (2026-11-12), with the database closed by the caller's session: checks
    /// the backup again, puts a copy in place of the database file and opens it exactly as on startup (an older schema is
    /// migrated, with its own upgrade backup). When the copy cannot be opened the previous database is put back and opened
    /// again; it is never replaced by an empty one. After success the previous files are removed only when
    /// <paramref name="keptCopy"/> (the verified backup made just before, reason <see cref="BackupReason.BeforeRestore"/>)
    /// exists; otherwise they stay under their "replaced" name.
    /// </summary>
    Task<InPlaceRestoreResult> RestoreInPlaceAsync(BackupInfo backup, string keptCopy, CancellationToken cancellationToken);

    /// <summary>
    /// Exit sequence step 4: waits until every write queued so far has run, then tries to fold the write-ahead log back
    /// into the database file (<c>wal_checkpoint(TRUNCATE)</c>). The checkpoint is best effort: a busy reader or any
    /// failure is logged and ignored, since committed data is safe in the log. Does nothing when no database is open.
    /// </summary>
    Task DrainAsync();
}

/// <summary>
/// The backups and leftover files the storage page lists, checks and removes (2026-11-12). Removing only ever touches
/// files of this data directory that match their name scheme; a failure (a file in use, no permission) is thrown.
/// </summary>
public interface IBackupFiles
{
    IReadOnlyList<BackupInfo> ListBackups();

    IReadOnlyList<LeftoverDatabase> ListLeftovers();

    /// <summary>Reads the backup without changing it: it exists, passes the integrity check and is Mindweave's at a schema this build can open.</summary>
    BackupProblem Check(BackupInfo backup);

    void Delete(BackupInfo backup);

    void Delete(LeftoverDatabase leftover);
}
