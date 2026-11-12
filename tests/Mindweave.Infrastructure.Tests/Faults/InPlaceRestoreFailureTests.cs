using System.Globalization;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Startup;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Faults;

/// <summary>
/// An in-app restore failing part way (2026-11-12; architecture §12.2): moving the files fails, or the restored copy
/// cannot be opened, or even putting the previous database back fails. The previous data must come back (or at least
/// stay on disk under its own name), its "replaced" files are only removed after a successful restore, and an empty
/// database is never opened in its place.
/// </summary>
[Trait("Category", "FaultInjection")]
public sealed class InPlaceRestoreFailureTests : IDisposable
{
    private static readonly int Current = MigrationCatalog.Default.LatestVersion;
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 30, 0, TimeSpan.Zero);
    private static readonly string Stamp = Now.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task A_swap_that_fails_part_way_moves_everything_back_and_opens_the_previous_database()
    {
        var (backup, kept) = await SeedAsync(Current);
        var shm = _dir.Paths.DatabasePath + "-shm";
        await File.WriteAllBytesAsync(shm, [], Ct);
        var before = await HashAsync(_dir.Paths.DatabasePath);

        await using var database = _dir.Database(time: new ManualTime(Now));
        // The WAL index's "replaced" name is taken once the database file has moved: the swap fails after its first step.
        database.BeforeSwapForTests = () => Directory.CreateDirectory($"{_dir.Paths.DatabasePath}.replaced-{Stamp}-shm");
        var result = await database.RestoreInPlaceAsync(backup, kept, Ct);

        Assert.False(result.Restored);
        Assert.Equal(RestoreFailure.SwapFailed, result.Failure);
        Assert.True(result.Open.IsReady, $"{result.Open.Status}: {result.Open.ErrorDetail}");
        Assert.Equal("current", await NameAsync(database));
        await database.CloseAsync();
        Assert.Equal(before, await HashAsync(_dir.Paths.DatabasePath));
        Assert.False(File.Exists($"{_dir.Paths.DatabasePath}.replaced-{Stamp}"));
        Assert.False(File.Exists(_dir.Paths.DatabasePath + ".restoring"));
    }

    /// <summary>The restored copy is older and its upgrade fails: the previous database is put back and opened.</summary>
    [Fact]
    public async Task A_restored_copy_that_cannot_be_opened_puts_the_previous_database_back()
    {
        var (backup, kept) = await SeedAsync(Current - 1);
        var before = await HashAsync(_dir.Paths.DatabasePath);
        var broken = new MigrationCatalog(
        [
            .. MigrationCatalog.Default.Migrations.Where(m => m.Version < Current),
            new Migration(Current, "broken", (_, _) => throw new InvalidOperationException("simulated upgrade failure")),
        ]);

        await using var database = _dir.Database(broken, new ManualTime(Now));
        var result = await database.RestoreInPlaceAsync(backup, kept, Ct);

        Assert.False(result.Restored);
        Assert.Equal(RestoreFailure.RestoredUnusable, result.Failure);
        Assert.True(result.Open.IsReady, $"{result.Open.Status}: {result.Open.ErrorDetail}");
        Assert.Equal("current", await NameAsync(database));
        await StorageFaults.AssertIntactAsync(database);
        await database.CloseAsync();
        Assert.Equal(before, await HashAsync(_dir.Paths.DatabasePath));
        Assert.Empty(Directory.GetFiles(_dir.Paths.DataDirectory, "mindweave.db.replaced-*"));
    }

    [Fact]
    public async Task When_the_previous_database_cannot_be_put_back_nothing_is_opened_and_its_file_stays_on_disk()
    {
        var (backup, kept) = await SeedAsync(Current - 1);
        var broken = new MigrationCatalog(
        [
            .. MigrationCatalog.Default.Migrations.Where(m => m.Version < Current),
            new Migration(Current, "broken", (_, _) => throw new InvalidOperationException("simulated upgrade failure")),
        ]);
        var replaced = $"{_dir.Paths.DatabasePath}.replaced-{Stamp}";
        var held = replaced + ".held";

        await using var database = _dir.Database(broken, new ManualTime(Now));
        // Something else takes the previous file away for a moment: moving it back fails.
        database.BeforePutBackForTests = () => File.Move(replaced, held);
        var result = await database.RestoreInPlaceAsync(backup, kept, Ct);

        Assert.False(result.Restored);
        Assert.Equal(RestoreFailure.PutBackFailed, result.Failure);
        Assert.False(result.Open.IsReady);
        Assert.False(database.IsOpen);
        Assert.True(File.Exists(_dir.Paths.DatabasePath));
        Assert.True(File.Exists(held));

        // Nothing was opened or created: the file in place is still the unopened copy of the backup.
        using var raw = SqliteConnections.OpenReadOnly(_dir.Paths.DatabasePath);
        Assert.Equal(Current - 1, raw.Scalar<long>(null, "PRAGMA user_version;"));
    }

    /// <summary>
    /// A database whose workspace is "current", and a valid backup of schema <paramref name="backupVersion"/> from when
    /// it was "in backup"; plus the kept copy made before the restore.
    /// </summary>
    private async Task<(BackupInfo Backup, string Kept)> SeedAsync(int backupVersion)
    {
        var catalog = new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version <= backupVersion));
        await using (var database = _dir.Database(catalog))
        {
            Assert.True((await database.OpenAsync(Ct)).IsReady);
            await database.WriteAsync((c, t) => c.Execute(t, "INSERT INTO workspace VALUES ('w', 'in backup', 1);"), Ct);
        }

        Directory.CreateDirectory(_dir.Paths.BackupDirectory);
        using (var raw = _dir.Raw())
        {
            raw.Execute(null, "VACUUM INTO $path;", ("$path", Path.Combine(_dir.Paths.BackupDirectory, $"mindweave-v{backupVersion}-20261027T000000000Z.db")));
        }

        string kept;
        await using (var database = _dir.Database(time: new ManualTime(Now.AddSeconds(-1))))
        {
            var opened = await database.OpenAsync(Ct);
            Assert.True(opened.IsReady);
            await database.WriteAsync((c, t) => c.Execute(t, "UPDATE workspace SET name = 'current';"), Ct);
            kept = await database.BackupAsync(BackupReason.BeforeRestore, Ct);
        }

        SqliteConnection.ClearAllPools();
        await using var probe = _dir.Database();
        return (Assert.Single(probe.ListBackups(), b => b.SchemaVersion == backupVersion && b.Reason == BackupReason.Migration && b.Path.Contains("20261027", StringComparison.Ordinal)), kept);
    }

    private static Task<string> NameAsync(SqliteDatabase database) =>
        database.ReadAsync(c => c.Scalar<string>(null, "SELECT name FROM workspace;")!, Ct);

    private static async Task<string> HashAsync(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)));
}
