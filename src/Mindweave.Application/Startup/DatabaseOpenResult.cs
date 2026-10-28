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

public sealed record BackupInfo(string Path, int SchemaVersion, DateTimeOffset CreatedAt, long SizeBytes);

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
    /// Exit sequence step 4: waits until every write queued so far has run, then tries to fold the write-ahead log back
    /// into the database file (<c>wal_checkpoint(TRUNCATE)</c>). The checkpoint is best effort: a busy reader or any
    /// failure is logged and ignored, since committed data is safe in the log. Does nothing when no database is open.
    /// </summary>
    Task DrainAsync();
}
