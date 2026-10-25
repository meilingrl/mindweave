using Microsoft.Data.Sqlite;
using Mindweave.Application.Navigation;
using Mindweave.Application.Storage;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Xunit;

namespace Mindweave.Tests;

public sealed class StorageTests
{
    [Fact]
    public async Task Workspaces_and_last_selection_survive_reopen()
    {
        using var dir = new TestDirectory();
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        Guid firstId;
        await using (var store = await SqliteWorkspaceStore.OpenAsync(paths))
        {
            Assert.Empty(await store.ListAsync());
            Assert.Null(await store.LoadSelectionAsync());
            var first = await store.CreateAsync("  火车历史  ");
            firstId = first.Id;
            await store.CreateAsync("蒸汽机");
            await store.SaveSelectionAsync(new(first.Id, MainArea.Network));
        }
        await using var reopened = await SqliteWorkspaceStore.OpenAsync(paths);
        var workspaces = await reopened.ListAsync();
        Assert.Equal(2, workspaces.Count);
        Assert.Equal("火车历史", workspaces.Single(w => w.Id == firstId).Name);
        Assert.Equal(new WorkspaceSelection(firstId, MainArea.Network), await reopened.LoadSelectionAsync());
    }

    [Fact]
    public async Task Concurrent_creates_are_committed_and_invalid_selection_cannot_replace_valid_one()
    {
        using var dir = new TestDirectory();
        await using var store = await SqliteWorkspaceStore.OpenAsync(AppPaths.ForTrack("codex-steady", dir.Path));
        var workspaces = await Task.WhenAll(Enumerable.Range(1, 20).Select(i => store.CreateAsync($"主题 {i}")));
        Assert.Equal(20, (await store.ListAsync()).Count);
        await store.SaveSelectionAsync(new(workspaces[0].Id, MainArea.Explore));
        await Assert.ThrowsAsync<SqliteException>(() => store.SaveSelectionAsync(new(Guid.NewGuid(), MainArea.Network)));
        Assert.Equal(workspaces[0].Id, (await store.LoadSelectionAsync())!.WorkspaceId);
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync("   "));
    }

    [Fact]
    public async Task Failed_migration_rolls_back_schema_data_and_version_and_keeps_consistent_backup()
    {
        using var dir = new TestDirectory();
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await SeedLegacyAsync(paths);
        var migration = new SchemaMigration(2, "UPDATE workspace SET name = 'damaged'; CREATE TABLE half_done(id TEXT); SELECT * FROM missing_table;");
        await Assert.ThrowsAsync<SqliteException>(() => SqliteWorkspaceStore.OpenAsync(paths, [new(1, ""), migration]));
        await AssertDatabaseAsync(paths.DatabasePath, 1, "保留原文");
        var backup = Assert.Single(Directory.GetFiles(paths.BackupDirectory, "*.db"));
        await AssertDatabaseAsync(backup, 1, "保留原文");
        using var connection = await OpenReadAsync(paths.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='half_done'";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public async Task Backup_failure_prevents_migration()
    {
        using var dir = new TestDirectory();
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await SeedLegacyAsync(paths);
        await File.WriteAllTextAsync(paths.BackupDirectory, "blocks directory creation");
        await Assert.ThrowsAsync<IOException>(() => SqliteWorkspaceStore.OpenAsync(paths,
            [new(1, ""), new(2, "UPDATE workspace SET name='damaged';")]));
        await AssertDatabaseAsync(paths.DatabasePath, 1, "保留原文");
    }

    [Fact]
    public async Task Successful_upgrade_backs_up_WAL_content_before_applying_changes()
    {
        using var dir = new TestDirectory();
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await SeedLegacyAsync(paths);
        // Keep an active connection so the committed data remains in WAL during backup.
        using var source = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False");
        source.Open();
        using (var command = source.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL; UPDATE workspace SET name='WAL 中的正文';";
            command.ExecuteNonQuery();
        }
        await using var upgraded = await SqliteWorkspaceStore.OpenAsync(paths,
            [new(1, ""), new(2, "ALTER TABLE workspace ADD COLUMN note TEXT;")]);
        var backup = Assert.Single(Directory.GetFiles(paths.BackupDirectory, "*.db"));
        await AssertDatabaseAsync(backup, 1, "WAL 中的正文");
        await AssertDatabaseAsync(paths.DatabasePath, 2, "WAL 中的正文");
    }

    [Fact]
    public async Task Newer_schema_and_corrupt_database_are_rejected_without_replacement()
    {
        using var dir = new TestDirectory();
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await SeedLegacyAsync(paths);
        using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=99";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteWorkspaceStore.OpenAsync(paths));
        await AssertDatabaseAsync(paths.DatabasePath, 99, "保留原文");
        var corrupt = AppPaths.ForTrack("corrupt", dir.Path);
        Directory.CreateDirectory(corrupt.DataDirectory);
        byte[] contents = "not a sqlite database"u8.ToArray();
        await File.WriteAllBytesAsync(corrupt.DatabasePath, contents);
        await Assert.ThrowsAsync<SqliteException>(() => SqliteWorkspaceStore.OpenAsync(corrupt));
        Assert.Equal(contents, await File.ReadAllBytesAsync(corrupt.DatabasePath));
    }

    [Fact]
    public async Task Killing_process_during_migration_preserves_original_committed_database()
    {
        using var dir = new TestDirectory();
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await SeedLegacyAsync(paths);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var child = ProcessProbe.Start("migration", dir.Path);
        try
        {
            Assert.Equal("MIGRATING", await child.StandardOutput.ReadLineAsync(timeout.Token));
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(timeout.Token);
            await AssertDatabaseAsync(paths.DatabasePath, 1, "保留原文");
            await AssertDatabaseAsync(Assert.Single(Directory.GetFiles(paths.BackupDirectory, "*.db")), 1, "保留原文");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
    }

    private static async Task SeedLegacyAsync(AppPaths paths)
    {
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths);
        await store.CreateAsync("保留原文");
    }

    private static async Task<SqliteConnection> OpenReadAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task AssertDatabaseAsync(string path, long version, string name)
    {
        using var connection = await OpenReadAsync(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        Assert.Equal(version, command.ExecuteScalar());
        command.CommandText = "SELECT name FROM workspace LIMIT 1";
        Assert.Equal(name, command.ExecuteScalar());
        command.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", command.ExecuteScalar());
        command.CommandText = "PRAGMA foreign_key_check";
        Assert.Null(command.ExecuteScalar());
    }
}
