using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Sqlite;

public sealed class SqliteDatabaseOptions
{
    /// <summary>
    /// How long a write waits for a lock held by another connection (for example another program holding the file)
    /// before it fails as busy.
    /// </summary>
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromMilliseconds(SqliteConnections.BusyTimeoutMs);
}

/// <summary>
/// Owns the database file: classifies it on open, backs up and migrates when needed, restores a backup on
/// explicit request, and afterwards exposes one serialized writer plus short-lived read-only connections.
/// Must only be used while the single-instance lock for the same data directory is held.
/// </summary>
public sealed partial class SqliteDatabase : IDatabaseLifecycle, IDatabaseBackup, IStorageProbe, IAsyncDisposable
{
    public const int MigrationBackupsKept = 5;

    private readonly AppPaths _paths;
    private readonly MigrationCatalog _catalog;
    private readonly ILogger<SqliteDatabase> _logger;
    private readonly SqliteBackupFiles _backups;
    private readonly int _busyTimeoutMs;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private SqliteWriteQueue? _writer;
    private int _disposed;

    public SqliteDatabase(AppPaths paths, MigrationCatalog catalog, TimeProvider time, ILogger<SqliteDatabase> logger, SqliteDatabaseOptions? options = null)
    {
        _paths = paths;
        _catalog = catalog;
        _logger = logger;
        _backups = new SqliteBackupFiles(paths, time, logger);
        _busyTimeoutMs = (int)(options ?? new SqliteDatabaseOptions()).BusyTimeout.TotalMilliseconds;
    }

    /// <summary>
    /// Fault-injection seam for the test host only: runs on the connection right before the pre-migration backup is
    /// written, with the temporary path it is written to, so a test can stop the process in the middle of it.
    /// </summary>
    internal Action<SqliteConnection, string>? BeforeBackupForTests { get; set; }

    /// <summary>
    /// Fault-injection seam for the test host only: runs inside every later write transaction after its statements and
    /// before COMMIT, so a test can stop the process while a multi-table write is still uncommitted.
    /// </summary>
    internal Action<SqliteConnection, SqliteTransaction>? BeforeCommitForTests { get; set; }

    public bool IsOpen => Volatile.Read(ref _writer) is not null;

    public async Task<DatabaseOpenResult> OpenAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
            return await Task.Run(OpenCore, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<DatabaseOpenResult> RestoreAsync(BackupInfo backup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() => _backups.ReplaceDatabaseWith(backup), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogRestoreFailed(_logger, ex, backup.Path);
                return Result(DatabaseOpenStatus.IoError, null, detail: $"Restore failed: {ex.Message}");
            }

            return await Task.Run(OpenCore, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public IReadOnlyList<BackupInfo> ListBackups() => _backups.List();

    /// <summary>
    /// A verified copy of the open database, made on the single writer between two transactions so it holds every
    /// write committed before it and none after. Kept under the same name scheme as the migration backups, so the
    /// storage page lists it and the recovery page can restore it.
    /// </summary>
    public Task<string> BackupAsync(BackupReason reason, CancellationToken cancellationToken)
    {
        var writer = Volatile.Read(ref _writer) ?? throw new InvalidOperationException("The database is not open.");
        return writer.EnqueueWithoutTransactionAsync(connection => _backups.CreateVerified(connection, _catalog.LatestVersion, reason, BeforeBackupForTests), cancellationToken);
    }

    /// <inheritdoc />
    public void Discard(string path) => _backups.Discard(path);

    /// <summary>Runs <paramref name="work"/> in its own transaction on the single writer.</summary>
    public Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> work, CancellationToken cancellationToken)
    {
        var writer = Volatile.Read(ref _writer) ?? throw new InvalidOperationException("The database is not open.");
        if (BeforeCommitForTests is { } beforeCommit)
        {
            var inner = work;
            work = (connection, transaction) =>
            {
                var result = inner(connection, transaction);
                beforeCommit(connection, transaction);
                return result;
            };
        }

        return writer.EnqueueAsync(work, cancellationToken);
    }

    /// <summary>
    /// Asks whether storage takes writes again, in two steps. First, on the single writer, a large write is made and
    /// rolled back: a lock held elsewhere, a read-only or damaged file, or the database's own size limit fails it. In WAL
    /// mode a rolled-back write never reaches the disk, though, so second, the same amount is written to a file beside
    /// the database and flushed through to the disk, then deleted: a full disk fails that. Either failure is a
    /// <see cref="Application.Storage.StorageWriteException"/>; nothing is stored either way.
    /// </summary>
    public async Task ProbeWriteAsync(CancellationToken cancellationToken)
    {
        var writer = Volatile.Read(ref _writer) ?? throw new InvalidOperationException("The database is not open.");
        await writer.EnqueueWithoutTransactionAsync(
            connection =>
            {
                using var transaction = connection.BeginTransaction();
                SqliteConnections.Execute(connection, $"INSERT OR REPLACE INTO app_state (key, value) VALUES ('storage-probe', hex(zeroblob({ProbeBytes / 2})));", transaction);
                transaction.Rollback();
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
        await Task.Run(ProbeDisk, cancellationToken).ConfigureAwait(false);
    }

    private void ProbeDisk()
    {
        var probe = Path.Combine(_paths.DataDirectory, ".storage-probe");
        try
        {
            using (var stream = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(new byte[ProbeBytes]);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new Application.Storage.StorageWriteException(Application.Storage.StorageFailureKind.ReadOnly, ex.Message, ex);
        }
        catch (IOException ex)
        {
            var kind = IsDiskFull(ex) ? Application.Storage.StorageFailureKind.Full : Application.Storage.StorageFailureKind.IoError;
            throw new Application.Storage.StorageWriteException(kind, ex.Message, ex);
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL on Windows, ENOSPC elsewhere.</summary>
    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 0x70 or 0x27 || ex.HResult == 28;

    /// <summary>How much the probe writes: more than a typical question or summary, so recovery is not declared early.</summary>
    internal const int ProbeBytes = 64 * 1024;

    /// <summary>Runs <paramref name="work"/> on a fresh read-only connection in one snapshot; it never waits for the writer.</summary>
    public Task<T> ReadAsync<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("The database is not open.");
        }

        return Task.Run(
            () =>
            {
                using var connection = SqliteConnections.OpenReadOnly(_paths.DatabasePath);

                // One snapshot for the whole read: a message row and its blocks are read by separate statements,
                // and a commit between them would otherwise pair a finished row with its earlier content. A raw
                // BEGIN keeps commands usable without a transaction object.
                SqliteConnections.Execute(connection, "BEGIN;");
                try
                {
                    return work(connection);
                }
                finally
                {
                    SqliteConnections.Execute(connection, "COMMIT;");
                }
            },
            cancellationToken);
    }

    public async Task DrainAsync()
    {
        if (Volatile.Read(ref _writer) is not { } writer)
        {
            return;
        }

        try
        {
            // Queued after every earlier write, so its completion means they have all run.
            var (busy, log, folded) = await writer.EnqueueWithoutTransactionAsync(
                    c =>
                    {
                        // TRUNCATE waits through the busy handler for readers; at exit a short wait is enough, a
                        // reader still holding a snapshot then just leaves the log for the next start.
                        SqliteConnections.Execute(c, $"PRAGMA busy_timeout = {(int)CheckpointWait.TotalMilliseconds};");
                        try
                        {
                            using var command = c.CreateCommand();
                            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                            using var reader = command.ExecuteReader();
                            reader.Read();
                            return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
                        }
                        finally
                        {
                            SqliteConnections.Execute(c, $"PRAGMA busy_timeout = {_busyTimeoutMs};");
                        }
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (busy != 0)
            {
                LogCheckpointBusy(_logger, log, folded);
            }
        }
        catch (ObjectDisposedException)
        {
            // Closed meanwhile: closing drains the queue itself.
        }
        catch (Exception ex) when (ex is SqliteException or Application.Storage.StorageWriteException)
        {
            LogCheckpointFailed(_logger, ex);
        }
    }

    /// <summary>Drains queued writes and closes the writer. Safe to call more than once, also after disposal.</summary>
    public async Task CloseAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Idempotent: the container disposes this instance once per service type it is registered under.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifecycleGate.Dispose();
        }
    }

    private async Task CloseCoreAsync()
    {
        var writer = Interlocked.Exchange(ref _writer, null);
        if (writer is not null)
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private DatabaseOpenResult OpenCore()
    {
        try
        {
            Directory.CreateDirectory(_paths.DataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result(DatabaseOpenStatus.IoError, null, detail: ex.Message);
        }

        if (!File.Exists(_paths.DatabasePath))
        {
            return CreateNew();
        }

        // Real elapsed time, not the injected clock: tests freeze that clock, and a frozen deadline would never pass.
        var deadline = Environment.TickCount64 + (long)TransientOpenWindow.TotalMilliseconds;
        while (true)
        {
            try
            {
                return OpenExistingFile();
            }
            catch (SqliteException ex) when (IsTransient(ex) && Environment.TickCount64 < deadline)
            {
                LogTransientOpenFailure(_logger, ex.SqliteExtendedErrorCode);
                Thread.Sleep(TransientOpenDelay);
            }
            catch (SqliteException ex)
            {
                LogUnreadable(_logger, ex, _paths.DatabasePath);
                return Result(DatabaseOpenStatus.Unreadable, null, detail: $"{ex.Message} (extended code {ex.SqliteExtendedErrorCode})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result(DatabaseOpenStatus.IoError, null, detail: ex.Message);
            }
        }
    }

    /// <summary>
    /// Errors that a process which just ended (a crash, or being killed) can leave behind for a moment. On Windows its
    /// memory mapping of the WAL index can outlive it, and SQLite's reset of that file then fails with
    /// <c>SQLITE_IOERR_TRUNCATE</c>, which SQLite does not retry (seen on Windows CI). Lock conflicts that outlast the
    /// busy timeout are included; any other I/O error is retried too, since waiting costs only a delay before the
    /// same classification.
    /// </summary>
    private static bool IsTransient(SqliteException ex) => ex.SqliteErrorCode is SqliteBusy or SqliteLocked or SqliteIoError;

    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteIoError = 10;

    /// <summary>How long opening keeps retrying transient errors before classifying the file.</summary>
    internal static readonly TimeSpan TransientOpenWindow = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan TransientOpenDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>How long the exit checkpoint waits for readers to leave the log before giving up on truncating it.</summary>
    private static readonly TimeSpan CheckpointWait = TimeSpan.FromMilliseconds(200);

    private DatabaseOpenResult OpenExistingFile()
    {
        SqliteConnection? connection = null;
        try
        {
            connection = SqliteConnections.OpenReadWrite(_paths.DatabasePath, busyTimeoutMs: _busyTimeoutMs);
            var outcome = OpenExisting(connection);
            if (outcome.IsReady)
            {
                _writer = new SqliteWriteQueue(connection);
                connection = null;
            }

            return outcome;
        }
        finally
        {
            connection?.Dispose();
        }
    }

    private DatabaseOpenResult OpenExisting(SqliteConnection connection)
    {
        var applicationId = SqliteConnections.Scalar<int>(connection, "PRAGMA application_id;");
        var version = SqliteConnections.Scalar<int>(connection, "PRAGMA user_version;");

        if (applicationId != SqliteConnections.ApplicationId)
        {
            var objects = SqliteConnections.Scalar<long>(connection, "SELECT count(*) FROM sqlite_schema;");
            if (applicationId != 0 || version != 0 || objects != 0)
            {
                return Result(DatabaseOpenStatus.ForeignDatabase, version, detail: $"application_id {applicationId}");
            }

            // A zero-length or schema-less file holds nothing to lose; initialize it in place.
            InitializeEmpty(connection);
            return Result(DatabaseOpenStatus.Ready, 0);
        }

        if (SqliteConnections.QuickCheck(connection) is { } problem)
        {
            return Result(DatabaseOpenStatus.Unreadable, version, detail: problem);
        }

        if (version > _catalog.LatestVersion)
        {
            return Result(DatabaseOpenStatus.TooNew, version);
        }

        if (version == _catalog.LatestVersion)
        {
            return Result(DatabaseOpenStatus.Ready, version);
        }

        string backupPath;
        try
        {
            backupPath = _backups.CreateVerified(connection, version, BackupReason.Migration, BeforeBackupForTests);
        }
#pragma warning disable CA1031 // Any backup failure means: do not migrate, report, keep the original.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBackupFailed(_logger, ex, version);
            return Result(DatabaseOpenStatus.BackupFailed, version, detail: ex.Message);
        }

        try
        {
            Migrate(connection, version);
        }
#pragma warning disable CA1031 // The migration was rolled back; the failure is reported, never swallowed silently.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogMigrationFailed(_logger, ex, version, _catalog.LatestVersion);
            return Result(DatabaseOpenStatus.MigrationFailed, version, backupPath, ex.Message);
        }

        LogMigrated(_logger, version, _catalog.LatestVersion, backupPath);
        _backups.Prune(MigrationBackupsKept);
        return Result(DatabaseOpenStatus.Ready, version, backupPath);
    }

    private DatabaseOpenResult CreateNew()
    {
        // Build under a temporary name and rename, so a crash never leaves a half-initialized database
        // under the real name.
        var temp = _paths.DatabasePath + ".creating";
        SqliteBackupFiles.DeleteDatabaseFiles(temp);
        try
        {
            using (var connection = SqliteConnections.OpenReadWrite(temp, create: true))
            {
                InitializeEmpty(connection);
            }

            File.Move(temp, _paths.DatabasePath);
            _writer = new SqliteWriteQueue(SqliteConnections.OpenReadWrite(_paths.DatabasePath, busyTimeoutMs: _busyTimeoutMs));
            LogCreated(_logger, _paths.DatabasePath, _catalog.LatestVersion);
            return Result(DatabaseOpenStatus.Ready, null);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            SqliteBackupFiles.DeleteDatabaseFiles(temp);
            return Result(DatabaseOpenStatus.IoError, null, detail: ex.Message);
        }
    }

    private void InitializeEmpty(SqliteConnection connection)
    {
        SqliteConnections.Scalar<string>(connection, "PRAGMA journal_mode = WAL;");
        SqliteConnections.Execute(connection, $"PRAGMA application_id = {SqliteConnections.ApplicationId};");
        Migrate(connection, 0);
    }

    /// <summary>
    /// Applies every pending migration in one transaction, following SQLite's documented procedure for
    /// schema changes: foreign keys off outside the transaction, <c>foreign_key_check</c> before commit.
    /// </summary>
    private void Migrate(SqliteConnection connection, int fromVersion)
    {
        SqliteConnections.Execute(connection, "PRAGMA foreign_keys = OFF;");
        try
        {
            using var transaction = connection.BeginTransaction();
            foreach (var migration in _catalog.Migrations.Where(m => m.Version > fromVersion))
            {
                migration.Apply(connection, transaction);
            }

            if (SqliteConnections.ForeignKeyViolation(connection, transaction) is { } violation)
            {
                throw new InvalidOperationException($"Migration left a dangling reference: {violation}.");
            }

            SqliteConnections.Execute(connection, $"PRAGMA user_version = {_catalog.LatestVersion};", transaction);
            transaction.Commit();
        }
        finally
        {
            SqliteConnections.Execute(connection, "PRAGMA foreign_keys = ON;");
        }
    }

    private DatabaseOpenResult Result(DatabaseOpenStatus status, int? foundVersion, string? backupPath = null, string? detail = null) =>
        new(status, _paths.DataDirectory, _paths.DatabasePath, foundVersion, _catalog.LatestVersion, backupPath, detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created database {Path} at schema {Version}")]
    private static partial void LogCreated(ILogger logger, string path, int version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrated schema {From} -> {To}; backup {BackupPath}")]
    private static partial void LogMigrated(ILogger logger, int from, int to, string backupPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Opening the database failed with SQLite extended code {Code}; retrying")]
    private static partial void LogTransientOpenFailure(ILogger logger, int code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Database {Path} could not be read")]
    private static partial void LogUnreadable(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pre-migration backup of schema {Version} failed; not migrating")]
    private static partial void LogBackupFailed(ILogger logger, Exception exception, int version);

    [LoggerMessage(Level = LogLevel.Error, Message = "Migration {From} -> {To} failed and was rolled back")]
    private static partial void LogMigrationFailed(ILogger logger, Exception exception, int from, int to);

    [LoggerMessage(Level = LogLevel.Information, Message = "Exit checkpoint was blocked by a reader ({Folded} of {Log} log frames folded); the log stays")]
    private static partial void LogCheckpointBusy(ILogger logger, int log, int folded);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Exit checkpoint failed; committed data stays in the log")]
    private static partial void LogCheckpointFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Restore from {BackupPath} failed")]
    private static partial void LogRestoreFailed(ILogger logger, Exception exception, string backupPath);
}
