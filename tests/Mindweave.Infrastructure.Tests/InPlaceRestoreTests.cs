using System.Globalization;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Startup;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests;

/// <summary>
/// Restoring a backup from inside the running application, and removing backups (2026-11-12; architecture §12.2,
/// requirements §9.3): the backup is checked before anything changes, the current data is kept as a verified
/// "before restore" backup, a failure puts the previous database back, and only after success are the previous files
/// removed. Removing touches only files of this data directory that match their name scheme.
/// </summary>
public sealed class InPlaceRestoreTests : IDisposable
{
    private static readonly int Current = MigrationCatalog.Default.LatestVersion;
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 30, 0, TimeSpan.Zero);
    private static readonly string Stamp = Now.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task A_before_restore_backup_is_named_and_listed_with_its_reason_and_never_rotated_out()
    {
        await using var database = _dir.Database(time: new ManualTime(Now));
        Assert.True((await database.OpenAsync(Ct)).IsReady);

        var path = await database.BackupAsync(BackupReason.BeforeRestore, Ct);

        Assert.Equal($"mindweave-v{Current}-{Stamp}-before-restore.db", Path.GetFileName(path));
        var listed = Assert.Single(database.ListBackups());
        Assert.Equal((path, BackupReason.BeforeRestore, Current), (listed.Path, listed.Reason, listed.SchemaVersion));

        // Upgrade backups rotate (keep 5); one made before a restore is never among them.
        for (var i = 0; i < SqliteDatabase.MigrationBackupsKept + 2; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_dir.Paths.BackupDirectory, $"mindweave-v{Current}-2026111{i}T000000000Z.db"), "copy", Ct);
        }

        await database.CloseAsync();
        new SqliteBackupFiles(_dir.Paths, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).Prune(SqliteDatabase.MigrationBackupsKept);
        var backups = database.ListBackups();
        Assert.Equal(SqliteDatabase.MigrationBackupsKept, backups.Count(b => b.Reason == BackupReason.Migration));
        Assert.Contains(backups, b => b.Path == path);
    }

    [Fact]
    public async Task Restoring_puts_the_backup_in_place_removes_the_previous_files_and_the_kept_copy_restores_back()
    {
        var (backup, kept) = await SeedAsync();

        await using (var database = _dir.Database(time: new ManualTime(Now)))
        {
            var result = await database.RestoreInPlaceAsync(backup, kept, Ct);

            Assert.True(result.Restored, $"{result.Failure}: {result.Detail}");
            Assert.True(result.Open.IsReady);
            Assert.Equal(["in backup"], await NamesAsync(database));
            Assert.Empty(Directory.GetFiles(_dir.Paths.DataDirectory, "mindweave.db.replaced-*"));
            Assert.Empty(database.ListLeftovers());
            Assert.False(File.Exists(_dir.Paths.DatabasePath + ".restoring"));
            await database.CloseAsync();
        }

        // The data before the restore is one restore away, from the same list.
        await using (var database = _dir.Database(time: new ManualTime(Now.AddMinutes(1))))
        {
            var keptInfo = Assert.Single(database.ListBackups(), b => b.Reason == BackupReason.BeforeRestore);
            Assert.Equal(BackupProblem.None, database.Check(keptInfo));
            var back = await database.RestoreInPlaceAsync(keptInfo, kept, Ct);

            Assert.True(back.Restored);
            Assert.Equal(["after backup", "in backup"], await NamesAsync(database));
        }
    }

    [Fact]
    public async Task Without_the_kept_copy_the_previous_files_stay_under_their_replaced_name()
    {
        var (backup, kept) = await SeedAsync();
        File.Delete(kept);

        await using var database = _dir.Database(time: new ManualTime(Now));
        var result = await database.RestoreInPlaceAsync(backup, kept, Ct);

        Assert.True(result.Restored);
        var leftover = Assert.Single(database.ListLeftovers());
        Assert.Equal(Now, leftover.ReplacedAt);
        Assert.Contains($"{_dir.Paths.DatabasePath}.replaced-{Stamp}", leftover.Files);
        Assert.Equal(leftover.Files.Sum(f => new FileInfo(f).Length), leftover.SizeBytes);
    }

    [Fact]
    public async Task An_older_schema_backup_is_restored_and_then_upgraded_with_its_own_upgrade_backup()
    {
        var older = new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version < Current));
        await using (var old = _dir.Database(older))
        {
            Assert.True((await old.OpenAsync(Ct)).IsReady);
            await old.WriteAsync((c, t) => c.Execute(t, "INSERT INTO workspace VALUES ('w', 'from older', 1);"), Ct);
        }

        var olderBackup = WriteBackupOfCurrentFile(Current - 1, "20261101T000000000Z");
        File.Delete(_dir.Paths.DatabasePath);
        var (_, kept) = await SeedAsync();

        await using var database = _dir.Database(time: new ManualTime(Now));
        var backup = Assert.Single(database.ListBackups(), b => b.Path == olderBackup);
        Assert.Equal(BackupProblem.None, database.Check(backup));
        var result = await database.RestoreInPlaceAsync(backup, kept, Ct);

        Assert.True(result.Restored, $"{result.Failure}: {result.Detail}");
        Assert.Equal(Current - 1, result.Open.FoundVersion);
        Assert.NotNull(result.Open.BackupPath);
        Assert.Equal(["from older"], await NamesAsync(database));
        Assert.Equal(Current, await database.ReadAsync(c => c.Scalar<long>(null, "PRAGMA user_version;"), Ct));
        Assert.Contains(database.ListBackups(), b => b.Reason == BackupReason.Migration && b.SchemaVersion == Current - 1 && b.Path != olderBackup);
    }

    [Theory]
    [InlineData(BackupProblem.Unreadable)]
    [InlineData(BackupProblem.TooNew)]
    [InlineData(BackupProblem.NotMindweave)]
    [InlineData(BackupProblem.Missing)]
    public async Task A_bad_backup_is_refused_before_anything_is_touched(BackupProblem problem)
    {
        var (good, kept) = await SeedAsync();
        var bad = Path.Combine(_dir.Paths.BackupDirectory, $"mindweave-v{Current}-20261111T000000000Z.db");
        switch (problem)
        {
            case BackupProblem.Unreadable:
                await File.WriteAllTextAsync(bad, "not a database", Ct);
                break;
            case BackupProblem.TooNew:
                File.Copy(good.Path, bad);
                using (var raw = SqliteConnections.OpenReadWrite(bad))
                {
                    raw.Execute(null, $"PRAGMA user_version = {Current + 1};");
                }

                break;
            case BackupProblem.NotMindweave:
                File.Copy(good.Path, bad);
                using (var raw = SqliteConnections.OpenReadWrite(bad))
                {
                    raw.Execute(null, "PRAGMA application_id = 42;");
                }

                break;
            default:
                break;
        }

        var before = await HashAsync(_dir.Paths.DatabasePath);
        var info = new BackupInfo(bad, Current, Now, 0);

        await using var database = _dir.Database(time: new ManualTime(Now));
        Assert.Equal(problem, database.Check(info));
        var result = await database.RestoreInPlaceAsync(info, kept, Ct);

        Assert.False(result.Restored);
        Assert.Equal(RestoreFailure.BackupUnusable, result.Failure);
        Assert.True(result.Open.IsReady);
        Assert.Equal(["after backup", "in backup"], await NamesAsync(database));
        await database.CloseAsync();
        Assert.Equal(before, await HashAsync(_dir.Paths.DatabasePath));
        Assert.Empty(Directory.GetFiles(_dir.Paths.DataDirectory, "mindweave.db.replaced-*"));
        Assert.False(File.Exists(_dir.Paths.DatabasePath + ".restoring"));
    }

    /// <summary>Checking reads the backup only: its bytes and the files beside it are as they were.</summary>
    [Fact]
    public async Task Checking_a_backup_changes_nothing()
    {
        var (backup, _) = await SeedAsync();
        var before = await HashAsync(backup.Path);
        var files = Directory.GetFiles(_dir.Paths.BackupDirectory);

        await using var database = _dir.Database();
        Assert.Equal(BackupProblem.None, database.Check(backup));

        Assert.Equal(before, await HashAsync(backup.Path));
        Assert.Equal(files, Directory.GetFiles(_dir.Paths.BackupDirectory));
    }

    [Fact]
    public async Task Deleting_refuses_anything_that_is_not_a_backup_or_leftover_of_this_data_directory()
    {
        var (backup, _) = await SeedAsync();
        var outside = Path.Combine(_dir.Paths.DataDirectory, Path.GetFileName(backup.Path));
        File.Copy(backup.Path, outside);
        var wrongName = Path.Combine(_dir.Paths.BackupDirectory, "notes.db");
        await File.WriteAllTextAsync(wrongName, "x", Ct);

        await using var database = _dir.Database();
        Assert.Throws<ArgumentException>(() => database.Delete(backup with { Path = outside }));
        Assert.Throws<ArgumentException>(() => database.Delete(backup with { Path = wrongName }));
        Assert.Throws<ArgumentException>(() => database.Delete(backup with { Path = Path.Combine(_dir.Paths.BackupDirectory, "..", Path.GetFileName(backup.Path)) }));
        Assert.Throws<ArgumentException>(() => database.Delete(new LeftoverDatabase(Now, [$"{_dir.Paths.DatabasePath}.replaced-{Stamp}", _dir.Paths.DatabasePath], 0)));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(wrongName));
        Assert.True(File.Exists(_dir.Paths.DatabasePath));

        database.Delete(backup);
        Assert.False(File.Exists(backup.Path));
    }

    [Fact]
    public async Task Leftover_replaced_files_are_listed_per_restore_and_deleted_together()
    {
        await File.WriteAllBytesAsync($"{_dir.Paths.DatabasePath}.replaced-20261101T000000000Z", new byte[3000], Ct);
        await File.WriteAllBytesAsync($"{_dir.Paths.DatabasePath}.replaced-20261101T000000000Z-wal", new byte[1000], Ct);
        await File.WriteAllBytesAsync($"{_dir.Paths.DatabasePath}.replaced-20261102T000000000Z", new byte[500], Ct);
        await File.WriteAllBytesAsync($"{_dir.Paths.DatabasePath}.replaced-junk", new byte[10], Ct);

        await using var database = _dir.Database();
        var leftovers = database.ListLeftovers();

        Assert.Equal([500L, 4000L], leftovers.Select(l => l.SizeBytes));
        Assert.Equal(2, leftovers[1].Files.Count);
        database.Delete(leftovers[1]);
        Assert.Equal([500L], database.ListLeftovers().Select(l => l.SizeBytes));
        Assert.True(File.Exists($"{_dir.Paths.DatabasePath}.replaced-junk"));
    }

    [Fact]
    public async Task A_delete_that_the_file_system_refuses_is_thrown()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Uses a read-only directory, as POSIX permissions make one.");
            return;
        }

        var (backup, _) = await SeedAsync();
        await using var database = _dir.Database();
        File.SetUnixFileMode(_dir.Paths.BackupDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var thrown = Record.Exception(() => database.Delete(backup));
            Assert.True(thrown is IOException or UnauthorizedAccessException, thrown?.ToString());
            Assert.True(File.Exists(backup.Path));
        }
        finally
        {
            File.SetUnixFileMode(_dir.Paths.BackupDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// A database with workspaces "in backup" and "after backup", a verified backup made between the two (as before a
    /// workspace delete) and the kept copy of the latest state (as made before a restore).
    /// </summary>
    private async Task<(BackupInfo Backup, string Kept)> SeedAsync()
    {
        string backupPath;
        string kept;
        await using (var database = _dir.Database(time: new ManualTime(Now.AddDays(-1))))
        {
            Assert.True((await database.OpenAsync(Ct)).IsReady);
            await database.WriteAsync((c, t) => c.Execute(t, "INSERT INTO workspace VALUES ('w1', 'in backup', 1);"), Ct);
            backupPath = await database.BackupAsync(BackupReason.BeforeWorkspaceDelete, Ct);
            await database.WriteAsync((c, t) => c.Execute(t, "INSERT INTO workspace VALUES ('w2', 'after backup', 2);"), Ct);
        }

        await using (var database = _dir.Database(time: new ManualTime(Now.AddSeconds(-1))))
        {
            Assert.True((await database.OpenAsync(Ct)).IsReady);
            kept = await database.BackupAsync(BackupReason.BeforeRestore, Ct);
        }

        SqliteConnection.ClearAllPools();
        await using var probe = _dir.Database();
        return (Assert.Single(probe.ListBackups(), b => b.Path == backupPath), kept);
    }

    /// <summary>A verified-looking copy of the database file as it is now, under an upgrade backup's name.</summary>
    private string WriteBackupOfCurrentFile(int version, string stamp)
    {
        Directory.CreateDirectory(_dir.Paths.BackupDirectory);
        var path = Path.Combine(_dir.Paths.BackupDirectory, $"mindweave-v{version}-{stamp}.db");
        using var raw = _dir.Raw();
        raw.Execute(null, "VACUUM INTO $path;", ("$path", path));
        return path;
    }

    private static Task<List<string>> NamesAsync(SqliteDatabase database) =>
        database.ReadAsync(
            c =>
            {
                using var command = c.Command(null, "SELECT name FROM workspace ORDER BY name;");
                using var reader = command.ExecuteReader();
                var names = new List<string>();
                while (reader.Read())
                {
                    names.Add(reader.GetString(0));
                }

                return names;
            },
            Ct);

    private static async Task<string> HashAsync(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)));
}
