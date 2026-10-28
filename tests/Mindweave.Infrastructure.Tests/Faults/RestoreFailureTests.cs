using System.Globalization;
using Mindweave.Application.Startup;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests.Faults;

/// <summary>
/// Restoring a backup fails part way (requirements §9.3, architecture §12.2): the result is an I/O error and the
/// current database stays under its own name, intact — the next start must never find it missing and begin empty.
/// Repeated in the stress workflow (filter <c>Category=FaultInjection</c>); two cases need Windows file locking.
/// </summary>
[Trait("Category", "FaultInjection")]
public sealed class RestoreFailureTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);
    private static readonly string Stamp = Now.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    /// <summary>
    /// The current file is renamed away, then renaming its WAL index fails (here: the target name is taken; on Windows
    /// also a file still mapped by a process that just died). Everything already moved must go back.
    /// </summary>
    [Fact]
    public async Task A_restore_failing_after_the_current_file_was_moved_puts_it_back_and_reports_an_io_error()
    {
        var backup = await SeedWithBackupAsync();
        var shm = _dir.Paths.DatabasePath + "-shm";
        await File.WriteAllBytesAsync(shm, [], Ct);
        Directory.CreateDirectory($"{_dir.Paths.DatabasePath}.replaced-{Stamp}-shm");
        var before = await HashAsync(_dir.Paths.DatabasePath);

        await using var database = _dir.Database(time: new FixedTime(Now));
        var result = await database.RestoreAsync(backup, Ct);

        Assert.Equal(DatabaseOpenStatus.IoError, result.Status);
        Assert.False(database.IsOpen);
        Assert.Equal(before, await HashAsync(_dir.Paths.DatabasePath));
        Assert.True(File.Exists(shm));
        Assert.False(File.Exists($"{_dir.Paths.DatabasePath}.replaced-{Stamp}"));
        Assert.False(File.Exists(_dir.Paths.DatabasePath + ".restoring"));
        await AssertCurrentAsync(database);
    }

    [Fact]
    public async Task A_restore_while_another_program_holds_the_database_open_reports_an_io_error_and_changes_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to rename a file another process holds open.");
        var backup = await SeedWithBackupAsync();
        var before = await HashAsync(_dir.Paths.DatabasePath);

        await using var database = _dir.Database(time: new FixedTime(Now));
        DatabaseOpenResult result;
        using (new FileStream(_dir.Paths.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await database.RestoreAsync(backup, Ct);
        }

        Assert.Equal(DatabaseOpenStatus.IoError, result.Status);
        Assert.Equal(before, await HashAsync(_dir.Paths.DatabasePath));
        Assert.Empty(Directory.GetFiles(_dir.Paths.DataDirectory, "mindweave.db.replaced-*"));
        Assert.False(File.Exists(_dir.Paths.DatabasePath + ".restoring"));
        await AssertCurrentAsync(database);
    }

    [Fact]
    public async Task A_restore_blocked_on_the_wal_index_by_another_program_puts_the_database_back()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to rename a file another process holds open.");
        var backup = await SeedWithBackupAsync();
        var shm = _dir.Paths.DatabasePath + "-shm";
        var before = await HashAsync(_dir.Paths.DatabasePath);

        await using var database = _dir.Database(time: new FixedTime(Now));
        DatabaseOpenResult result;
        using (new FileStream(shm, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            result = await database.RestoreAsync(backup, Ct);
        }

        Assert.Equal(DatabaseOpenStatus.IoError, result.Status);
        Assert.Equal(before, await HashAsync(_dir.Paths.DatabasePath));
        Assert.False(File.Exists($"{_dir.Paths.DatabasePath}.replaced-{Stamp}"));
        await AssertCurrentAsync(database);
    }

    /// <summary>A database whose workspace is "current", and a valid backup of it from when it was "in backup".</summary>
    private async Task<Application.Startup.BackupInfo> SeedWithBackupAsync()
    {
        await using (var database = _dir.Database())
        {
            Assert.True((await database.OpenAsync(Ct)).IsReady);
            await database.WriteAsync((c, t) => c.Execute(t, "INSERT INTO workspace VALUES ('w', 'in backup', 1);"), Ct);
        }

        Directory.CreateDirectory(_dir.Paths.BackupDirectory);
        using (var raw = _dir.Raw())
        {
            raw.Execute(null, "VACUUM INTO $path;", ("$path", Path.Combine(_dir.Paths.BackupDirectory, $"mindweave-v{MigrationCatalog.Default.LatestVersion}-20261027T000000000Z.db")));
        }

        await using (var database = _dir.Database())
        {
            Assert.True((await database.OpenAsync(Ct)).IsReady);
            await database.WriteAsync((c, t) => c.Execute(t, "UPDATE workspace SET name = 'current';"), Ct);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await using var probe = _dir.Database();
        return Assert.Single(probe.ListBackups());
    }

    private static async Task AssertCurrentAsync(SqliteDatabase database)
    {
        var reopened = await database.OpenAsync(Ct);
        Assert.True(reopened.IsReady, $"{reopened.Status}: {reopened.ErrorDetail}");
        Assert.Equal("current", await database.ReadAsync(c => c.Scalar<string>(null, "SELECT name FROM workspace;"), Ct));
        await StorageFaults.AssertIntactAsync(database);
    }

    private static async Task<string> HashAsync(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
