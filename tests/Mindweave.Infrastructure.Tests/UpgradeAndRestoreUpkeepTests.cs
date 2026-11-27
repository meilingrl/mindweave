using Microsoft.Data.Sqlite;
using Mindweave.Application.Startup;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests;

/// <summary>
/// The upkeep rows about upgrades and restores (D-042, docs/design/diagnostics-and-upkeep.md §3.1; architecture §12.2):
/// an upgrade says when it starts and what it did (gap-schema-4); a database a restore replaced can be restored like a
/// backup (gap-schema-5); no upgrade backup is made of data restored from a backup that still exists (gap-schema-6); a
/// dangling row that was there before an upgrade no longer stops it, while one the upgrade makes still does
/// (gap-schema-7).
/// </summary>
public sealed class UpgradeAndRestoreUpkeepTests : IDisposable
{
    private static readonly int Current = MigrationCatalog.Default.LatestVersion;
    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task An_upgrade_says_it_starts_with_a_backup_and_reports_where_the_backup_is()
    {
        await SeedCurrentAsync("before");
        await using var database = _dir.Database(WithNext(AddNote));
        var steps = new List<DataUpgradeStarting>();
        database.UpgradeStarting += (_, step) => steps.Add(step);

        var result = await database.OpenAsync(Ct);

        Assert.True(result.IsReady, $"{result.Status}: {result.ErrorDetail}");
        Assert.Equal([new DataUpgradeStarting(Current, Current + 1, BackupFirst: true)], steps);
        var upgrade = Assert.IsType<DataUpgrade>(result.Upgrade);
        Assert.Equal((Current, Current + 1, true), (upgrade.From, upgrade.To, upgrade.BackupMadeNow));
        Assert.Equal(Assert.Single(database.ListBackups()).Path, upgrade.BackupPath);
    }

    [Fact]
    public async Task Opening_data_that_needs_no_upgrade_says_nothing()
    {
        await SeedCurrentAsync("before");
        await using var database = _dir.Database();
        var steps = 0;
        database.UpgradeStarting += (_, _) => steps++;

        var result = await database.OpenAsync(Ct);

        Assert.True(result.IsReady);
        Assert.Null(result.Upgrade);
        Assert.Equal(0, steps);
    }

    [Fact]
    public async Task Restoring_an_older_backup_upgrades_it_without_a_second_copy()
    {
        await SeedCurrentAsync("in backup");
        var catalog = WithNext(AddNote);
        await using (var upgraded = _dir.Database(catalog))
        {
            Assert.True((await upgraded.OpenAsync(Ct)).IsReady);
        }

        await using var database = _dir.Database(catalog);
        var backup = Assert.Single(database.ListBackups());
        var steps = new List<DataUpgradeStarting>();
        database.UpgradeStarting += (_, step) => steps.Add(step);

        var result = await database.RestoreAsync(backup, Ct);

        Assert.True(result.IsReady, $"{result.Status}: {result.ErrorDetail}");
        Assert.Equal([new DataUpgradeStarting(Current, Current + 1, BackupFirst: false)], steps);
        Assert.Equal(new DataUpgrade(Current, Current + 1, backup.Path, BackupMadeNow: false), result.Upgrade);
        Assert.Equal([backup.Path], database.ListBackups().Select(b => b.Path));
    }

    [Fact]
    public async Task Restoring_in_place_an_older_backup_upgrades_it_without_a_second_copy()
    {
        await SeedCurrentAsync("in backup");
        var catalog = WithNext(AddNote);
        await using var database = _dir.Database(catalog);
        Assert.True((await database.OpenAsync(Ct)).IsReady);
        var backup = Assert.Single(database.ListBackups());
        var kept = await database.BackupAsync(BackupReason.BeforeRestore, Ct);

        var result = await database.RestoreInPlaceAsync(backup, kept, Ct);

        Assert.True(result.Restored, result.Failure.ToString());
        Assert.Equal(new DataUpgrade(Current, Current + 1, backup.Path, BackupMadeNow: false), result.Open.Upgrade);
        Assert.Equal(2, database.ListBackups().Count);
        Assert.Single(database.ListBackups(), b => b.Reason == BackupReason.Migration);
    }

    /// <summary>
    /// The second defect of §1.7: going back to an older build and restoring there put the newer data out of reach. It
    /// stays beside the database under a "replaced" name, and the build that can read it restores it like a backup, with
    /// what was still in its write-ahead log.
    /// </summary>
    [Fact]
    public async Task A_replaced_database_is_listed_with_its_version_and_restores_like_a_backup()
    {
        await SeedCurrentAsync("newer data");
        var replaced = _dir.Paths.DatabasePath + ".replaced-20261127T101500000Z";
        using (var source = _dir.Raw())
        {
            SqliteConnections.Execute(source, "PRAGMA wal_autocheckpoint = 0;");
            SqliteConnections.Execute(source, "INSERT INTO workspace VALUES ('w-wal', 'only in the log', 2);");

            // Copied while the connection holds the log open, as a restore's rename would find it.
            File.Copy(_dir.Paths.DatabasePath, replaced);
            File.Copy(_dir.Paths.DatabasePath + "-wal", replaced + "-wal");
        }

        await using var database = _dir.Database();
        var leftover = Assert.Single(database.ListLeftovers());
        Assert.Equal(Current, leftover.SchemaVersion);
        var restorable = Assert.Single(database.ListRestorable(), b => b.Reason == BackupReason.Replaced);
        Assert.Equal(replaced, restorable.Path);
        Assert.Equal(BackupProblem.None, database.Check(restorable));

        File.Delete(_dir.Paths.DatabasePath);
        File.Delete(_dir.Paths.DatabasePath + "-wal");
        File.Delete(_dir.Paths.DatabasePath + "-shm");
        await SeedCurrentAsync("older data");
        var result = await database.RestoreAsync(restorable, Ct);

        Assert.True(result.IsReady, $"{result.Status}: {result.ErrorDetail}");
        Assert.Equal(["newer data", "only in the log"], await database.ReadAsync(c => Strings(c, "SELECT name FROM workspace ORDER BY name;"), Ct));
        Assert.True(File.Exists(replaced), "The replaced database stays after it is restored, as a backup does.");
        Assert.Equal(2, database.ListLeftovers().Count);
    }

    [Fact]
    public async Task A_replaced_database_from_a_newer_build_is_listed_but_refused()
    {
        await SeedCurrentAsync("newer data");
        await using (var newer = _dir.Database(WithNext(AddNote)))
        {
            Assert.True((await newer.OpenAsync(Ct)).IsReady);
        }

        File.Move(_dir.Paths.DatabasePath, _dir.Paths.DatabasePath + ".replaced-20261127T101500000Z");
        await using var database = _dir.Database();

        var restorable = Assert.Single(database.ListRestorable(), b => b.Reason == BackupReason.Replaced);

        Assert.Equal(Current + 1, restorable.SchemaVersion);
        Assert.Equal(BackupProblem.TooNew, database.Check(restorable));
    }

    [Fact]
    public async Task Deleting_a_replaced_database_as_a_backup_removes_its_files()
    {
        await SeedCurrentAsync("data");
        var replaced = _dir.Paths.DatabasePath + ".replaced-20261127T101500000Z";
        File.Copy(_dir.Paths.DatabasePath, replaced);
        await File.WriteAllTextAsync(replaced + "-wal", string.Empty, Ct);
        await using var database = _dir.Database();

        database.Delete(Assert.Single(database.ListRestorable(), b => b.Reason == BackupReason.Replaced));

        Assert.Empty(database.ListLeftovers());
        Assert.False(File.Exists(replaced + "-wal"));
    }

    [Fact]
    public async Task A_path_outside_the_data_directory_is_not_taken_for_a_replaced_database()
    {
        await SeedCurrentAsync("data");
        await using var database = _dir.Database();
        var elsewhere = new BackupInfo(Path.Combine(Path.GetTempPath(), "mindweave.db.replaced-20261127T101500000Z"), Current, DateTimeOffset.UnixEpoch, 1, BackupReason.Replaced);

        Assert.Throws<ArgumentException>(() => database.Check(elsewhere));
    }

    [Fact]
    public async Task A_dangling_row_from_before_the_upgrade_does_not_stop_it()
    {
        await SeedCurrentAsync("original");
        using (var raw = _dir.Raw())
        {
            SqliteConnections.Execute(raw, "PRAGMA foreign_keys = OFF;");
            SqliteConnections.Execute(raw, "INSERT INTO workspace_location VALUES ('no-such-workspace', 'explore', NULL, 0);");
        }

        await using var database = _dir.Database(WithNext(AddNote));

        var result = await database.OpenAsync(Ct);

        Assert.True(result.IsReady, $"{result.Status}: {result.ErrorDetail}");
        await database.CloseAsync();
        using var after = _dir.Raw();
        Assert.Equal(Current + 1, SqliteConnections.Scalar<long>(after, "PRAGMA user_version;"));
        Assert.Equal(1, SqliteConnections.Scalar<long>(after, "SELECT count(*) FROM workspace_location WHERE workspace_id = 'no-such-workspace';"));
    }

    [Fact]
    public async Task A_dangling_row_the_upgrade_adds_beside_an_old_one_still_rolls_it_back()
    {
        await SeedCurrentAsync("original");
        using (var raw = _dir.Raw())
        {
            SqliteConnections.Execute(raw, "PRAGMA foreign_keys = OFF;");
            SqliteConnections.Execute(raw, "INSERT INTO workspace_location VALUES ('no-such-workspace', 'explore', NULL, 0);");
        }

        await using var database = _dir.Database(WithNext(Migration.Sql(
            Current + 1,
            "orphan",
            "INSERT INTO workspace_location VALUES ('another-missing-workspace', 'explore', NULL, 0);")));

        var result = await database.OpenAsync(Ct);

        Assert.Equal(DatabaseOpenStatus.MigrationFailed, result.Status);
        Assert.Contains("1 new in workspace_location", result.ErrorDetail, StringComparison.Ordinal);
        await database.CloseAsync();
        using var after = _dir.Raw();
        Assert.Equal(Current, SqliteConnections.Scalar<long>(after, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task No_backup_progress_is_reported_while_none_is_written()
    {
        await SeedCurrentAsync("data");
        await using var database = _dir.Database();
        Assert.True((await database.OpenAsync(Ct)).IsReady);

        Assert.Null(database.Progress);
        database.Discard(await database.BackupAsync(BackupReason.BeforeConversationDelete, Ct));
        Assert.Null(database.Progress);
    }

    private static Migration AddNote => Migration.Sql(Current + 1, "add note", "CREATE TABLE note (id TEXT PRIMARY KEY) STRICT;");

    private static MigrationCatalog WithNext(params Migration[] extra) => new([.. MigrationCatalog.Default.Migrations, .. extra]);

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
}
