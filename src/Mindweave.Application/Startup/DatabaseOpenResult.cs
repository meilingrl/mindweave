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

/// <param name="Upgrade">Set when this open upgraded the data: from which version to which, and where the backup is.</param>
public sealed record DatabaseOpenResult(
    DatabaseOpenStatus Status,
    string DataDirectory,
    string DatabasePath,
    int? FoundVersion,
    int SupportedVersion,
    string? BackupPath = null,
    string? ErrorDetail = null,
    DataUpgrade? Upgrade = null)
{
    public bool IsReady => Status == DatabaseOpenStatus.Ready;
}

/// <summary>
/// An upgrade of stored data that went through (gap-schema-4, D-042): the notice after start-up says to which version,
/// and where the copy of the data from before it is. <paramref name="BackupPath"/> is the backup made just before; after
/// a restore it is the restored backup itself, which still holds the same data (<paramref name="BackupMadeNow"/> false).
/// </summary>
public sealed record DataUpgrade(int From, int To, string BackupPath, bool BackupMadeNow);

/// <summary>
/// Opening is about to upgrade the data (gap-schema-4): the start-up page says so, and whether a backup is made first.
/// Raised on the thread that opens, before the backup.
/// </summary>
public sealed record DataUpgradeStarting(int From, int To, bool BackupFirst);

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

    /// <summary>
    /// Not made as a backup: the database an earlier restore replaced, kept in the data directory under a "replaced"
    /// name. It can be restored like a backup (gap-schema-5, D-042); its <see cref="BackupInfo.Path"/> is that file.
    /// </summary>
    Replaced,
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
/// The database files one earlier restore put aside ("mindweave.db.replaced-…", with its log files): kept until removed
/// by hand, and restorable like a backup (gap-schema-5, D-042). <paramref name="Files"/> are full paths in the data
/// directory. <paramref name="SchemaVersion"/> is read from the database file's header; null when there is no database
/// file among them or it is not Mindweave's.
/// </summary>
public sealed record LeftoverDatabase(DateTimeOffset ReplacedAt, IReadOnlyList<string> Files, long SizeBytes, int? SchemaVersion = null)
{
    /// <summary>The replaced database as a backup to restore, or null when it cannot be one (see <see cref="SchemaVersion"/>).</summary>
    public BackupInfo? AsBackup() =>
        SchemaVersion is { } version && Files.FirstOrDefault(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal)) is { } database
            ? new BackupInfo(database, version, ReplacedAt, SizeBytes, BackupReason.Replaced)
            : null;
}

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
    /// How far the copy being written is, from 0 to 1, or null when none is being written (performance-7, D-042): a
    /// dialog waiting on a long backup polls it to show progress. An estimate: the copy's size against the data's.
    /// </summary>
    double? Progress => null;

    /// <summary>
    /// Removes a backup made by <see cref="BackupAsync"/> that turned out not to be needed (the delete it was made for
    /// was refused after all). Best effort: a file that cannot be removed is left and logged.
    /// </summary>
    void Discard(string path);
}

/// <summary>Opening, migrating and restoring the local database. Implemented by Infrastructure.</summary>
public interface IDatabaseLifecycle
{
    /// <summary>An open (at start-up, or after a restore) is about to upgrade the data; raised on the opening thread.</summary>
    event EventHandler<DataUpgradeStarting>? UpgradeStarting;

    Task<DatabaseOpenResult> OpenAsync(CancellationToken cancellationToken);

    IReadOnlyList<BackupInfo> ListBackups();

    /// <summary>
    /// What the recovery page offers to restore: the backups and the databases earlier restores replaced (reason
    /// <see cref="BackupReason.Replaced"/>), newest first.
    /// </summary>
    IReadOnlyList<BackupInfo> ListRestorable();

    /// <summary>
    /// Keeps the current database file under a "replaced" name, copies the backup into place,
    /// then opens it exactly as on startup, except that an older schema is upgraded without a backup of its own while
    /// the restored file still exists (gap-schema-6). Only ever called after an explicit user choice.
    /// </summary>
    Task<DatabaseOpenResult> RestoreAsync(BackupInfo backup, CancellationToken cancellationToken);

    /// <summary>
    /// Restoring from inside the running application (2026-11-12), with the database closed by the caller's session: checks
    /// the backup again, puts a copy in place of the database file and opens it exactly as on startup (an older schema is
    /// migrated; the restored backup, which still exists, stands for the upgrade backup, gap-schema-6). When the copy cannot be opened the previous database is put back and opened
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
