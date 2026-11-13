using Microsoft.Data.Sqlite;
using Mindweave.Application.Startup;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests;

public sealed class SqliteDatabaseTests : IDisposable
{
    private static readonly int Current = MigrationCatalog.Default.LatestVersion;
    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Fresh_start_creates_a_wal_database_at_the_latest_schema()
    {
        await using var database = _dir.Database();

        var result = await database.OpenAsync(Ct);

        Assert.Equal(DatabaseOpenStatus.Ready, result.Status);
        Assert.Null(result.FoundVersion);
        await database.CloseAsync();
        using var raw = _dir.Raw();
        Assert.Equal(Current, Scalar<long>(raw, "PRAGMA user_version;"));
        Assert.Equal(SqliteConnections.ApplicationId, Scalar<long>(raw, "PRAGMA application_id;"));
        Assert.Equal("wal", Scalar<string>(raw, "PRAGMA journal_mode;"));
        Assert.False(File.Exists(_dir.Paths.DatabasePath + ".creating"));
        Assert.Empty(database.ListBackups());
    }

    [Fact]
    public async Task A_read_sees_one_snapshot_even_when_a_write_commits_part_way()
    {
        await SeedCurrentAsync("before");
        await using var database = _dir.Database();
        Assert.True((await database.OpenAsync(Ct)).IsReady);

        var (first, second) = await database.ReadAsync(
            c =>
            {
                var first = Scalar<string>(c, "SELECT name FROM workspace;");
                database.WriteAsync((w, t) => Exec(w, t, "UPDATE workspace SET name = 'after';"), Ct).GetAwaiter().GetResult();
                return (first, Scalar<string>(c, "SELECT name FROM workspace;"));
            },
            Ct);

        Assert.Equal(("before", "before"), (first, second));
    }

    [Fact]
    public async Task Reopening_a_current_database_makes_no_backup()
    {
        await SeedCurrentAsync("kept");
        await using var database = _dir.Database();

        var result = await database.OpenAsync(Ct);

        Assert.Equal(DatabaseOpenStatus.Ready, result.Status);
        Assert.Equal(Current, result.FoundVersion);
        Assert.Empty(database.ListBackups());
    }

    [Fact]
    public async Task Upgrade_backs_up_first_then_migrates()
    {
        await SeedCurrentAsync("before upgrade");
        await using var database = _dir.Database(WithNext(Migration.Sql(Current + 1, "add note", "CREATE TABLE note (id TEXT PRIMARY KEY) STRICT;")));

        var result = await database.OpenAsync(Ct);

        Assert.Equal(DatabaseOpenStatus.Ready, result.Status);
        Assert.Equal(Current, result.FoundVersion);
        var backup = Assert.Single(database.ListBackups());
        Assert.Equal(result.BackupPath, backup.Path);
        Assert.Equal(Current, backup.SchemaVersion);
        await database.CloseAsync();

        using (var raw = _dir.Raw())
        {
            Assert.Equal(Current + 1, Scalar<long>(raw, "PRAGMA user_version;"));
            Assert.Equal(1, Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE name = 'note';"));
        }

        using var copy = SqliteConnections.OpenReadWrite(backup.Path);
        Assert.Equal(Current, Scalar<long>(copy, "PRAGMA user_version;"));
        Assert.Equal("before upgrade", Scalar<string>(copy, "SELECT name FROM workspace;"));
    }

    [Fact]
    public async Task Failed_migration_rolls_back_every_step_and_keeps_the_backup()
    {
        await SeedCurrentAsync("original");
        var catalog = WithNext(
            Migration.Sql(Current + 1, "fine", "CREATE TABLE note (id TEXT PRIMARY KEY) STRICT; INSERT INTO app_state VALUES ('step1', 'done');"),
            new Migration(Current + 2, "broken", (_, _) => throw new InvalidOperationException("boom")));
        await using var database = _dir.Database(catalog);

        var result = await database.OpenAsync(Ct);

        Assert.Equal(DatabaseOpenStatus.MigrationFailed, result.Status);
        Assert.Contains("boom", result.ErrorDetail, StringComparison.Ordinal);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.False(database.IsOpen);
        AssertUnchangedAtCurrent("original");
    }

    [Fact]
    public async Task Migration_that_leaves_a_dangling_reference_is_rolled_back()
    {
        await SeedCurrentAsync("original");
        await using var database = _dir.Database(WithNext(Migration.Sql(
            Current + 1,
            "orphan",
            "INSERT INTO workspace_location VALUES ('no-such-workspace', 'explore', NULL, 0);")));

        var result = await database.OpenAsync(Ct);

        Assert.Equal(DatabaseOpenStatus.MigrationFailed, result.Status);
        Assert.Contains("dangling", result.ErrorDetail, StringComparison.Ordinal);
        AssertUnchangedAtCurrent("original");
    }

    [Fact]
    public async Task Backup_failure_prevents_migration()
    {
        await SeedCurrentAsync("original");
        await File.WriteAllTextAsync(_dir.Paths.BackupDirectory, "a file where the backup directory should be", Ct);
        await using var database = _dir.Database(WithNext(Migration.Sql(Current + 1, "add note", "CREATE TABLE note (id TEXT PRIMARY KEY) STRICT;")));

        var result = await database.OpenAsync(Ct);

        Assert.Equal(DatabaseOpenStatus.BackupFailed, result.Status);
        Assert.Null(result.BackupPath);
        AssertUnchangedAtCurrent("original");
    }

    [Fact]
    public async Task Newer_schema_is_refused_without_writing()
    {
        await SeedCurrentAsync("from the future");
        using (var raw = _dir.Raw())
        {
            Execute(raw, $"PRAGMA user_version = {Current + 5};");
        }

        var before = await HashAsync(_dir.Paths.DatabasePath);
        await using var database = _dir.Database();

        var result = await database.OpenAsync(Ct);

        Assert.Equal(DatabaseOpenStatus.TooNew, result.Status);
        Assert.Equal(Current + 5, result.FoundVersion);
        Assert.False(database.IsOpen);
        Assert.Equal(before, await HashAsync(_dir.Paths.DatabasePath));
    }

    [Fact]
    public async Task A_sqlite_file_from_another_application_is_not_touched()
    {
        using (var foreign = SqliteConnections.OpenReadWrite(_dir.Paths.DatabasePath, create: true))
        {
            Execute(foreign, "CREATE TABLE theirs (x INTEGER);");
        }

        var before = await HashAsync(_dir.Paths.DatabasePath);
        await using var database = _dir.Database();

        var result = await database.OpenAsync(Ct);

        Assert.Equal(DatabaseOpenStatus.ForeignDatabase, result.Status);
        Assert.Equal(before, await HashAsync(_dir.Paths.DatabasePath));
    }

    /// <summary>
    /// Health check 2026-11-12, errors-4: a database another program holds is in use, not damaged; the user is not
    /// steered towards replacing a healthy database with an older backup.
    /// </summary>
    [Fact]
    public async Task A_database_locked_by_another_program_is_reported_in_use_not_damaged()
    {
        await SeedCurrentAsync("kept");
        using var other = _dir.Raw();
        SqliteConnections.Execute(other, "BEGIN IMMEDIATE;");
        await using var database = _dir.Database(options: new SqliteDatabaseOptions { BusyTimeout = TimeSpan.FromMilliseconds(100) });

        var result = await database.OpenAsync(Ct);

        Assert.Equal(DatabaseOpenStatus.InUse, result.Status);
        Assert.False(database.IsOpen);
        SqliteConnections.Execute(other, "ROLLBACK;");
    }

    [Fact]
    public async Task Restoring_from_the_recovery_page_checks_the_backup_and_replaces_nothing_when_it_is_damaged()
    {
        await SeedCurrentAsync("current");
        await using (var first = _dir.Database())
        {
            Assert.True((await first.OpenAsync(Ct)).IsReady);
            await first.BackupAsync(Mindweave.Application.Startup.BackupReason.BeforeRestore, Ct);
        }

        await using var database = _dir.Database();
        var backup = Assert.Single(database.ListBackups());
        var garbage = new byte[8192];
        Random.Shared.NextBytes(garbage);
        await File.WriteAllBytesAsync(backup.Path, garbage, Ct);

        var result = await database.RestoreAsync(backup, Ct);

        Assert.True(result.IsReady, $"{result.Status}: {result.ErrorDetail}");
        Assert.Contains("backup", result.ErrorDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["current"], await database.ReadAsync(c => Strings(c, "SELECT name FROM workspace;"), Ct));
        Assert.Empty(Directory.GetFiles(_dir.Paths.DataDirectory, "mindweave.db.replaced-*"));
    }

    [Fact]
    public async Task A_non_database_file_is_reported_unreadable_and_kept()
    {
        var garbage = new byte[4096];
        Random.Shared.NextBytes(garbage);
        await File.WriteAllBytesAsync(_dir.Paths.DatabasePath, garbage, Ct);
        await using var database = _dir.Database();

        var result = await database.OpenAsync(Ct);

        Assert.Equal(DatabaseOpenStatus.Unreadable, result.Status);
        Assert.Equal(garbage, await File.ReadAllBytesAsync(_dir.Paths.DatabasePath, Ct));

        // No handle may be left open: on Windows a leaked connection would block moving the file away.
        File.Move(_dir.Paths.DatabasePath, _dir.Paths.DatabasePath + ".moved");
    }

    [Fact]
    public async Task A_zero_length_file_is_initialized_in_place()
    {
        await File.WriteAllBytesAsync(_dir.Paths.DatabasePath, [], Ct);
        await using var database = _dir.Database();

        var result = await database.OpenAsync(Ct);

        Assert.Equal(DatabaseOpenStatus.Ready, result.Status);
    }

    [Fact]
    public async Task Restore_keeps_the_replaced_file_and_reopens_through_the_normal_path()
    {
        await SeedCurrentAsync("in backup");
        var catalog = WithNext(Migration.Sql(Current + 1, "add note", "CREATE TABLE note (id TEXT PRIMARY KEY) STRICT;"));
        await using (var upgraded = _dir.Database(catalog))
        {
            Assert.True((await upgraded.OpenAsync(Ct)).IsReady);
            await upgraded.WriteAsync((c, t) => Exec(c, t, "INSERT INTO workspace VALUES ('w2', 'after backup', 2);"), Ct);
        }

        await using var database = _dir.Database(catalog);
        var backup = Assert.Single(database.ListBackups());

        var result = await database.RestoreAsync(backup, Ct);

        Assert.Equal(DatabaseOpenStatus.Ready, result.Status);
        Assert.Equal(Current, result.FoundVersion);
        var names = await database.ReadAsync(c => Strings(c, "SELECT name FROM workspace ORDER BY name;"), Ct);
        Assert.Equal(["in backup"], names);
        var replaced = Directory.GetFiles(_dir.Paths.DataDirectory, "mindweave.db.replaced-*");
        Assert.Contains(replaced, f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal));
    }

    /// <summary>
    /// Health check 2026-11-12 (storage-1): backups are written by VACUUM INTO, which does not carry WAL mode, so a
    /// restored database came back in rollback-journal mode for good. Every open puts WAL back.
    /// </summary>
    [Fact]
    public async Task A_restored_database_is_back_in_wal_mode()
    {
        await SeedCurrentAsync("kept");
        string backupPath;
        await using (var first = _dir.Database())
        {
            Assert.True((await first.OpenAsync(Ct)).IsReady);
            backupPath = await first.BackupAsync(BackupReason.BeforeRestore, Ct);
            var kept = await first.BackupAsync(BackupReason.BeforeRestore, Ct);
            await first.CloseAsync();
            using (var copy = SqliteConnections.OpenReadWrite(backupPath))
            {
                Assert.Equal("delete", Scalar<string>(copy, "PRAGMA journal_mode;"));
            }

            SqliteConnection.ClearAllPools();
            var result = await first.RestoreInPlaceAsync(first.ListBackups().Single(b => b.Path == backupPath), kept, Ct);
            Assert.True(result.Restored, result.Failure.ToString());
            Assert.Equal("wal", await first.ReadAsync(c => Scalar<string>(c, "PRAGMA journal_mode;"), Ct));
        }

        await using var second = _dir.Database();
        var restored = await second.RestoreAsync(second.ListBackups().Single(b => b.Path == backupPath), Ct);
        Assert.True(restored.IsReady);
        Assert.Equal("wal", await second.ReadAsync(c => Scalar<string>(c, "PRAGMA journal_mode;"), Ct));
    }

    [Fact]
    public async Task Only_the_newest_migration_backups_are_kept()
    {
        var time = new SteppingTime();
        for (var round = 0; round < SqliteDatabase.MigrationBackupsKept + 2; round++)
        {
            await SeedCurrentAsync($"round {round}");
            await using var database = _dir.Database(WithNext(Migration.Sql(Current + 1, "add note", "CREATE TABLE note (id TEXT PRIMARY KEY) STRICT;")), time);
            Assert.True((await database.OpenAsync(Ct)).IsReady);
            await database.CloseAsync();
            File.Delete(_dir.Paths.DatabasePath);
        }

        await using var probe = _dir.Database();
        Assert.Equal(SqliteDatabase.MigrationBackupsKept, probe.ListBackups().Count);
    }

    [Fact]
    public async Task Backups_made_before_a_workspace_delete_are_never_rotated_out_and_never_push_upgrade_backups_out()
    {
        // Older than every upgrade backup below, so a rotation over all backups would remove them first.
        Directory.CreateDirectory(_dir.Paths.BackupDirectory);
        string[] beforeDelete =
        [
            Path.Combine(_dir.Paths.BackupDirectory, $"mindweave-v{Current}-20000101T000000000Z-before-delete.db"),
            Path.Combine(_dir.Paths.BackupDirectory, $"mindweave-v{Current}-20000102T000000000Z-before-delete.db"),
        ];
        foreach (var path in beforeDelete)
        {
            await File.WriteAllTextAsync(path, "copy", Ct);
        }

        var time = new SteppingTime();
        for (var round = 0; round < SqliteDatabase.MigrationBackupsKept + 2; round++)
        {
            await SeedCurrentAsync($"round {round}");
            await using var database = _dir.Database(WithNext(Migration.Sql(Current + 1, "add note", "CREATE TABLE note (id TEXT PRIMARY KEY) STRICT;")), time);
            Assert.True((await database.OpenAsync(Ct)).IsReady);
            await database.CloseAsync();
            File.Delete(_dir.Paths.DatabasePath);
        }

        await using var probe = _dir.Database();
        var backups = probe.ListBackups();
        Assert.Equal(SqliteDatabase.MigrationBackupsKept, backups.Count(b => b.Reason == BackupReason.Migration));
        Assert.Equal(beforeDelete.Reverse(), backups.Where(b => b.Reason == BackupReason.BeforeWorkspaceDelete).Select(b => b.Path));
    }

    [Fact]
    public async Task Writes_are_serialized_and_a_failed_write_rolls_back_alone()
    {
        await using var database = _dir.Database();
        Assert.True((await database.OpenAsync(Ct)).IsReady);

        var writes = Enumerable.Range(0, 50).Select(i =>
            database.WriteAsync((c, t) => Exec(c, t, $"INSERT INTO app_state VALUES ('k{i}', 'v');"), Ct));
        await Task.WhenAll(writes);
        await Assert.ThrowsAsync<SqliteException>(() => database.WriteAsync(
            (c, t) =>
            {
                Exec(c, t, "INSERT INTO app_state VALUES ('partial', 'v');");
                return Exec(c, t, "INSERT INTO app_state VALUES ('k0', 'duplicate');");
            },
            Ct));
        await database.WriteAsync((c, t) => Exec(c, t, "INSERT INTO app_state VALUES ('after', 'v');"), Ct);

        var keys = await database.ReadAsync(c => Strings(c, "SELECT key FROM app_state;"), Ct);
        Assert.Equal(51, keys.Count);
        Assert.DoesNotContain("partial", keys);
    }

    [Fact]
    public async Task Closing_drains_queued_writes()
    {
        await using var database = _dir.Database();
        Assert.True((await database.OpenAsync(Ct)).IsReady);
        var pending = Enumerable.Range(0, 20)
            .Select(i => database.WriteAsync((c, t) => Exec(c, t, $"INSERT INTO app_state VALUES ('k{i}', 'v');"), Ct))
            .ToArray();

        await database.CloseAsync();

        await Task.WhenAll(pending);
        using var raw = _dir.Raw();
        Assert.Equal(20, Scalar<long>(raw, "SELECT count(*) FROM app_state;"));
        Assert.Throws<InvalidOperationException>(() => { _ = database.WriteAsync((c, t) => 0, Ct); });
    }

    [Fact]
    public async Task Draining_runs_queued_writes_and_folds_the_log_into_the_database()
    {
        await using var database = _dir.Database();
        Assert.True((await database.OpenAsync(Ct)).IsReady);
        var pending = Enumerable.Range(0, 20)
            .Select(i => database.WriteAsync((c, t) => Exec(c, t, $"INSERT INTO app_state VALUES ('k{i}', 'v');"), Ct))
            .ToArray();

        await database.DrainAsync();

        Assert.All(pending, p => Assert.True(p.IsCompletedSuccessfully));
        Assert.Equal(0, new FileInfo(_dir.Paths.DatabasePath + "-wal").Length);
        Assert.Equal(20L, await database.ReadAsync(c => Scalar<long>(c, "SELECT count(*) FROM app_state;"), Ct));

        // Still open for writing afterwards (an exit may be retried or abandoned).
        await database.WriteAsync((c, t) => Exec(c, t, "INSERT INTO app_state VALUES ('after', 'v');"), Ct);
    }

    [Fact]
    public async Task Draining_while_a_reader_holds_a_snapshot_completes_and_keeps_the_log()
    {
        await using var database = _dir.Database();
        Assert.True((await database.OpenAsync(Ct)).IsReady);
        await database.WriteAsync((c, t) => Exec(c, t, "INSERT INTO app_state VALUES ('k', 'v');"), Ct);
        using var reader = SqliteConnections.OpenReadOnly(_dir.Paths.DatabasePath);
        SqliteConnections.Execute(reader, "BEGIN;");
        Assert.Equal(1L, Scalar<long>(reader, "SELECT count(*) FROM app_state;"));
        await database.WriteAsync((c, t) => Exec(c, t, "INSERT INTO app_state VALUES ('k2', 'v');"), Ct);

        // The checkpoint waits for the reader only briefly (200 ms), not the connection's 5 s busy timeout.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await database.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"Draining took {watch.Elapsed}.");

        SqliteConnections.Execute(reader, "COMMIT;");
        Assert.True(new FileInfo(_dir.Paths.DatabasePath + "-wal").Length > 0);
        Assert.Equal(2L, await database.ReadAsync(c => Scalar<long>(c, "SELECT count(*) FROM app_state;"), Ct));
    }

    [Fact]
    public async Task Draining_a_closed_database_does_nothing()
    {
        await using var database = _dir.Database();
        await database.DrainAsync();
        Assert.True((await database.OpenAsync(Ct)).IsReady);
        await database.CloseAsync();
        await database.DrainAsync();
    }

    [Fact]
    public async Task Disposing_twice_and_closing_after_dispose_are_harmless()
    {
        var database = _dir.Database();
        Assert.True((await database.OpenAsync(Ct)).IsReady);

        await database.DisposeAsync();
        await database.DisposeAsync();
        await database.CloseAsync();

        Assert.False(database.IsOpen);
    }

    /// <summary>
    /// On Windows a killed process's memory mapping of the WAL index (<c>-shm</c>) can outlive it briefly (longer under
    /// load). Opening alone, SQLite resets that file to zero length, which Windows refuses while it is mapped
    /// (<c>SQLITE_IOERR_TRUNCATE</c>, not retried by SQLite). Seen on Windows CI after killing a child process; it
    /// must be waited out, not reported as an unreadable database (which offers restoring a backup).
    /// </summary>
    [Fact]
    public async Task A_wal_index_still_mapped_by_a_dead_process_is_waited_for_not_reported_as_unreadable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to resize a mapped file.");
        await SeedCurrentAsync("kept");
        var release = HoldWalIndexMappedAsync(_dir.Paths.DatabasePath + "-shm", TimeSpan.FromMilliseconds(700));

        await using var database = _dir.Database();
        var result = await database.OpenAsync(Ct);
        await release;

        Assert.True(result.IsReady, $"{result.Status}: {result.ErrorDetail}");
    }

    /// <summary>Maps the WAL index as a crashed writer's lingering mapping would, and unmaps it later.</summary>
    private static Task HoldWalIndexMappedAsync(string path, TimeSpan duration)
    {
        using (var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            file.SetLength(32 * 1024);
        }

        var mapping = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(
            new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete), null, 0,
            System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
        var view = mapping.CreateViewAccessor();
        return ReleaseAsync();

        async Task ReleaseAsync()
        {
            await Task.Delay(duration, Ct);
            view.Dispose();
            mapping.Dispose();
        }
    }

    [Fact]
    public async Task Killing_the_process_inside_a_migration_leaves_the_previous_version()
    {
        await SeedCurrentAsync("original");
        using var child = ChildProcess.Start("hang-in-migration", _dir.Paths.DataDirectory);
        Assert.Equal("in-migration", await child.WaitForLineAsync(TimeSpan.FromSeconds(30)));

        await child.KillAsync();

        // Opened through the application first: right after a kill on Windows, the file can briefly refuse a fresh
        // connection (see the mapped WAL index test), which only the application's open waits out.
        await using var database = _dir.Database();
        var result = await database.OpenAsync(Ct);
        Assert.True(result.IsReady, $"{result.Status}: {result.ErrorDetail}");
        AssertUnchangedAtCurrent("original");
        using var raw = _dir.Raw();
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM app_state WHERE key = 'half-migrated';"));
    }

    [Fact]
    public async Task A_committed_write_survives_killing_the_process()
    {
        using var child = ChildProcess.Start("commit-then-hang", _dir.Paths.DataDirectory);
        Assert.Equal("committed", await child.WaitForLineAsync(TimeSpan.FromSeconds(30)));

        await child.KillAsync();

        await using var database = _dir.Database();
        Assert.True((await database.OpenAsync(Ct)).IsReady);
        var value = await database.ReadAsync(c => Strings(c, "SELECT value FROM app_state WHERE key = 'committed';"), Ct);
        Assert.Equal(["yes"], value);
    }

    private static MigrationCatalog WithNext(params Migration[] extra) =>
        new([.. MigrationCatalog.Default.Migrations, .. extra]);

    private async Task SeedCurrentAsync(string workspaceName)
    {
        await using var database = _dir.Database();
        Assert.True((await database.OpenAsync(Ct)).IsReady);
        await database.WriteAsync(
            (c, t) =>
            {
                using var command = c.CreateCommand();
                command.Transaction = t;
                command.CommandText = "INSERT INTO workspace VALUES ($id, $name, 1);";
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$name", workspaceName);
                return command.ExecuteNonQuery();
            },
            Ct);
    }

    private void AssertUnchangedAtCurrent(string workspaceName)
    {
        using var raw = _dir.Raw();
        Assert.Equal(Current, Scalar<long>(raw, "PRAGMA user_version;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE name = 'note';"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM app_state;"));
        Assert.Equal(workspaceName, Scalar<string>(raw, "SELECT name FROM workspace;"));
    }

    private static int Exec(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql) => SqliteConnections.Execute(connection, sql);

    private static T Scalar<T>(SqliteConnection connection, string sql) => SqliteConnections.Scalar<T>(connection, sql);

    private static List<string> Strings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    private static async Task<string> HashAsync(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)));

    /// <summary>
    /// Moves one second forward on every read, so each backup gets its own time stamp without the test moving it; kept
    /// apart from <see cref="Mindweave.Testing.ManualTime"/>, which only moves when told.
    /// </summary>
    private sealed class SteppingTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
    }
}
