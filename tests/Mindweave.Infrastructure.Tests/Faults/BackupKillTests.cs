using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests.Faults;

/// <summary>
/// The process is killed while the pre-migration backup is being written (architecture §12.2): the half-written copy
/// is never offered as a backup, the database stays at its version, and the next start backs up and migrates normally,
/// removing the leftover. Repeated in the stress workflow (filter <c>Category=FaultInjection</c>).
/// </summary>
[Trait("Category", "FaultInjection")]
public sealed class BackupKillTests : IDisposable
{
    private static readonly int Current = MigrationCatalog.Default.LatestVersion;
    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Killing_the_process_while_the_backup_is_written_leaves_no_listed_backup_and_the_next_start_recovers()
    {
        await SeedAsync();
        using (var child = ChildProcess.Start("hang-in-backup", _dir.Paths.DataDirectory))
        {
            Assert.Equal("in-backup", await child.WaitForLineAsync(TimeSpan.FromSeconds(60)));
            await child.KillAsync();
        }

        var partial = Assert.Single(Directory.GetFiles(_dir.Paths.BackupDirectory, "*.partial"));
        Assert.StartsWith("mindweave-v", Path.GetFileName(partial), StringComparison.Ordinal);
        Assert.True(new FileInfo(partial).Length > 0);

        // Opened through the application (it waits out a WAL index a killed process still maps on Windows).
        await using (var current = _dir.Database())
        {
            Assert.Empty(current.ListBackups());
            var opened = await current.OpenAsync(Ct);
            Assert.True(opened.IsReady, $"{opened.Status}: {opened.ErrorDetail}");
            Assert.Equal(Current, await current.ReadAsync(c => c.Scalar<long>(null, "PRAGMA user_version;"), Ct));
            Assert.Equal(Rows, await current.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM app_state WHERE key LIKE 'bulk-%';"), Ct));
        }

        // On Windows a killed process's handles close a moment after it exits (TRACK.md findings); wait, bounded, until
        // the leftover could be removed, so the cleanup below is what is tested rather than that timing.
        await WaitUntilFreeAsync(partial);
        await using var upgraded = _dir.Database(new MigrationCatalog([.. MigrationCatalog.Default.Migrations, Migration.Sql(Current + 1, "add note", "CREATE TABLE note (id TEXT PRIMARY KEY) STRICT;")]));
        var migrated = await upgraded.OpenAsync(Ct);
        Assert.True(migrated.IsReady, $"{migrated.Status}: {migrated.ErrorDetail}");
        var backup = Assert.Single(upgraded.ListBackups());
        Assert.Equal(Current, backup.SchemaVersion);
        Assert.Equal(migrated.BackupPath, backup.Path);
        Assert.Empty(Directory.GetFiles(_dir.Paths.BackupDirectory, "*.partial*"));
        using var copy = SqliteConnections.OpenReadWrite(backup.Path);
        Assert.Null(SqliteConnections.QuickCheck(copy));
        Assert.Equal(Rows, copy.Scalar<long>(null, "SELECT count(*) FROM app_state WHERE key LIKE 'bulk-%';"));
    }

    private const int Rows = 3000;

    private static async Task WaitUntilFreeAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    return;
                }
            }
            catch (IOException) when (attempt < 50)
            {
                await Task.Delay(100, Ct);
            }
        }
    }

    /// <summary>A database at the current version with about 6 MB of rows, so the copy takes long enough to stop inside.</summary>
    private async Task SeedAsync()
    {
        await using var database = _dir.Database();
        Assert.True((await database.OpenAsync(Ct)).IsReady);
        await database.WriteAsync(
            (c, t) => c.Execute(t, $"""
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {Rows})
                INSERT INTO app_state (key, value) SELECT 'bulk-' || i, hex(randomblob(1024)) FROM n;
                """),
            Ct);
    }
}
