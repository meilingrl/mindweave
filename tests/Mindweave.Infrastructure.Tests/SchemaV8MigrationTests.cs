using Microsoft.Data.Sqlite;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests;

/// <summary>
/// Upgrading real version-7 data to the archive table: a verified backup is taken first, nothing is archived
/// afterwards, and an archive row goes with its conversation.
/// </summary>
public sealed class SchemaV8MigrationTests : IDisposable
{
    private const string Workspace = "0190f000-0000-7000-8000-0000000000aa";
    private const string Root = "0190f000-0000-7000-8000-000000000001";
    private const string Child = "0190f000-0000-7000-8000-000000000002";
    private const string Answer = "0190f000-0000-7000-8000-000000000003";

    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Version_7_data_upgrades_with_a_backup_and_nothing_archived()
    {
        await SeedVersion7Async();

        await using var database = _dir.Database();
        var result = await database.OpenAsync(Ct);
        Assert.True(result.IsReady);
        Assert.True(File.Exists(result.BackupPath));
        Assert.Equal(7, Assert.Single(database.ListBackups()).SchemaVersion);

        var listed = await new SqliteConversationStore(database).ListAsync(Core.Workspaces.WorkspaceId.Parse(Workspace), Ct);
        Assert.Equal(2, listed.Count);
        Assert.All(listed, c => Assert.False(c.IsHidden));

        using var raw = _dir.Raw();
        Assert.Equal(MigrationCatalog.Default.LatestVersion, SqliteConnections.Scalar<long>(raw, "PRAGMA user_version;"));
        Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM conversation_archive;"));
        Assert.Equal(1, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE name = 'branch_context_range_by_ancestor';"));
        Assert.Null(SqliteConnections.ForeignKeyViolation(raw));
    }

    [Fact]
    public async Task An_archive_row_needs_its_conversation_and_goes_with_it()
    {
        await SeedVersion7Async();
        await using (var database = _dir.Database())
        {
            Assert.True((await database.OpenAsync(Ct)).IsReady);
        }

        using var raw = _dir.Raw();
        SqliteConnections.Execute(raw, "PRAGMA foreign_keys = ON;");
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, $"INSERT INTO conversation_archive VALUES ('{ConversationId.New()}', 1);"));
        SqliteConnections.Execute(raw, $"INSERT INTO conversation_archive VALUES ('{Child}', 1);");
        SqliteConnections.Execute(raw, $"DELETE FROM branch_context_range WHERE child_id = '{Child}'; DELETE FROM branch_source WHERE child_id = '{Child}'; DELETE FROM conversation WHERE id = '{Child}';");
        Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM conversation_archive;"));
    }

    private async Task SeedVersion7Async()
    {
        await using (var old = _dir.Database(new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version <= 7))))
        {
            Assert.True((await old.OpenAsync(Ct)).IsReady);
        }

        using var raw = _dir.Raw();
        SqliteConnections.Execute(raw, $"""
            INSERT INTO workspace VALUES ('{Workspace}', '测试', 1);
            INSERT INTO conversation VALUES ('{Root}', '{Workspace}', NULL, 'none', 1, 1);
            INSERT INTO conversation VALUES ('{Child}', '{Workspace}', NULL, 'none', 2, 2);
            INSERT INTO message (id, conversation_id, seq, role, state, created_at, completed_at)
                VALUES ('{Answer}', '{Root}', 0, 'assistant', 'complete', 1, 1);
            INSERT INTO message_block (message_id, ordinal, kind, text) VALUES ('{Answer}', 0, 'text', '答');
            INSERT INTO branch_source (child_id, parent_id, source_message_id, source_block, quote, anchor, anchor_version, render_profile, created_at)
                VALUES ('{Child}', '{Root}', '{Answer}', 0, '答', 'a', 1, 'p', 2);
            INSERT INTO branch_context_range (child_id, ancestor_id, depth, max_seq) VALUES ('{Child}', '{Root}', 0, 0);
            """);
    }
}
