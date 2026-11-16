using Microsoft.Data.Sqlite;
using Mindweave.Application.Startup;
using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests;

/// <summary>
/// Upgrading real version-12 data to summary links and link suggestions (2026-11-16, requirements §10.3.3–10.3.4): what
/// was there is untouched and the two tables start empty; the schema itself keeps a pair in one order, once, never a node
/// with itself, never across workspaces, with a note of at most 16 characters; rows go with either summary and with the
/// workspace. A failing later step leaves version 12 as it was; a fresh database has the tables; a restored backup brings
/// its links back.
/// </summary>
public sealed class SchemaV13MigrationTests : IDisposable
{
    private const string Workspace = "0190f000-0000-7000-8000-0000000000aa";
    private const string OtherWorkspace = "0190f000-0000-7000-8000-0000000000bb";
    private const string NodeA = "0190f000-0000-7000-8000-00000000000a";
    private const string NodeB = "0190f000-0000-7000-8000-00000000000b";
    private const string NodeC = "0190f000-0000-7000-8000-00000000000c";
    private const string NodeOther = "0190f000-0000-7000-8000-00000000000d";

    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Version_12_data_is_kept_and_the_schema_holds_the_link_rules_and_cascades()
    {
        await SeedVersion12Async();

        await using var database = _dir.Database();
        var result = await database.OpenAsync(Ct);
        Assert.True(result.IsReady, $"{result.Status}: {result.ErrorDetail}");
        Assert.Equal(12, Assert.Single(database.ListBackups()).SchemaVersion);

        using (var raw = _dir.Raw())
        {
            Assert.True(MigrationCatalog.Default.LatestVersion >= 13);
            Assert.Equal(MigrationCatalog.Default.LatestVersion, SqliteConnections.Scalar<long>(raw, "PRAGMA user_version;"));
            Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM pragma_foreign_key_check;"));
            Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM summary_link;"));
            Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM link_suggestion;"));
            Assert.Equal(4, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM summary_node;"));
            Assert.Equal(1, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM recommendation;"));
            SqliteConnections.Execute(raw, "PRAGMA foreign_keys = ON;");

            // A link in order is stored; the rules are the schema's own.
            SqliteConnections.Execute(raw, Link("l1", Workspace, NodeA, NodeB, "'对比'"));
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Link("l2", Workspace, NodeB, NodeA, "NULL"))); // reversed order
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Link("l3", Workspace, NodeA, NodeB, "NULL"))); // the same pair again
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Link("l4", Workspace, NodeA, NodeA, "NULL"))); // itself
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Link("l5", Workspace, NodeA, NodeOther, "NULL"))); // another workspace's summary
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Link("l6", OtherWorkspace, NodeA, NodeOther, "NULL"))); // a node of this one
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Link("l7", Workspace, NodeA, NodeC, $"'{new string('长', 17)}'")));
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Link("l8", Workspace, NodeA, NodeC, "NULL", origin: "ai")));
            SqliteConnections.Execute(raw, Link("l9", Workspace, NodeA, NodeC, $"'{new string('例', 16)}'"));
            SqliteConnections.Execute(raw, Link("l10", Workspace, NodeB, NodeC, "NULL", origin: "confirmed"));

            // Suggestions: in order, once per pair, pending ones from a request, ignored ones without a reason.
            SqliteConnections.Execute(raw, Suggestion("s1", NodeA, NodeB, "pending", "'理由'", "'g'"));
            SqliteConnections.Execute(raw, Suggestion("s2", NodeB, NodeC, "ignored", "NULL", "NULL"));
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Suggestion("s3", NodeA, NodeB, "ignored", "NULL", "NULL")));
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Suggestion("s4", NodeC, NodeA, "pending", "NULL", "'g'")));
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Suggestion("s5", NodeA, NodeC, "pending", "NULL", "NULL")));
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Suggestion("s6", NodeA, NodeC, "ignored", "'理由'", "NULL")));
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Suggestion("s7", NodeA, NodeC, "shown", "NULL", "'g'")));

            // Deleting summary A (its last version) takes its links and suggestions along; B–C stays.
            SqliteConnections.Execute(raw, $"BEGIN; DELETE FROM summary_node WHERE id = '{NodeA}'; DELETE FROM summary_version WHERE node_id = '{NodeA}'; COMMIT;");
            Assert.Equal("l10", SqliteConnections.Scalar<string>(raw, "SELECT group_concat(id) FROM summary_link;"));
            Assert.Equal("s2", SqliteConnections.Scalar<string>(raw, "SELECT group_concat(id) FROM link_suggestion;"));

            // Deleting the workspace's summaries and then the workspace leaves nothing.
            SqliteConnections.Execute(raw, $"BEGIN; DELETE FROM summary_node WHERE workspace_id = '{Workspace}'; DELETE FROM summary_version WHERE node_id IN ('{NodeB}', '{NodeC}'); COMMIT;");
            Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM summary_link;") + SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM link_suggestion;"));
            Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM pragma_foreign_key_check;"));
        }
    }

    [Fact]
    public async Task A_failing_upgrade_after_version_13_leaves_version_12_as_it_was()
    {
        await SeedVersion12Async();

        await using (var failing = _dir.Database(new MigrationCatalog([.. MigrationCatalog.Default.Migrations.Where(m => m.Version <= 13), new Migration(14, "broken", (_, _) => throw new InvalidOperationException("simulated failure"))])))
        {
            Assert.Equal(DatabaseOpenStatus.MigrationFailed, (await failing.OpenAsync(Ct)).Status);
        }

        using var raw = _dir.Raw();
        Assert.Equal(12, SqliteConnections.Scalar<long>(raw, "PRAGMA user_version;"));
        Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE name IN ('summary_link', 'link_suggestion', 'summary_node_by_workspace_id');"));
        Assert.Equal(4, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM summary_node;"));
    }

    [Fact]
    public async Task A_new_database_has_the_tables_and_a_restored_backup_brings_its_links_back()
    {
        var now = new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.Zero);
        string backupPath;
        string kept;
        SummaryLinkId linked;
        await using (var database = _dir.Database(time: new ManualTime(now.AddDays(-1))))
        {
            Assert.True((await database.OpenAsync(Ct)).IsReady);
            await database.WriteAsync((c, t) => c.Execute(t, SeedNodes(withRecommendation: false)), Ct);
            var store = new SqliteSummaryLinkStore(database);
            linked = (await store.AddAsync(SummaryNodeId.Parse(NodeB), SummaryNodeId.Parse(NodeA), "对比", SummaryLinkOrigin.User, now, Ct)).Id;
            backupPath = await database.BackupAsync(BackupReason.BeforeWorkspaceDelete, Ct);

            // After the backup the link is deleted (its pair recorded as ignored) and another one made.
            Assert.NotNull(await store.DeleteAsync(linked, now, Ct));
            await store.AddAsync(SummaryNodeId.Parse(NodeA), SummaryNodeId.Parse(NodeC), null, SummaryLinkOrigin.User, now, Ct);
        }

        await using (var database = _dir.Database(time: new ManualTime(now.AddSeconds(-1))))
        {
            Assert.True((await database.OpenAsync(Ct)).IsReady);
            kept = await database.BackupAsync(BackupReason.BeforeRestore, Ct);
        }

        SqliteConnection.ClearAllPools();
        await using (var database = _dir.Database(time: new ManualTime(now)))
        {
            var backup = Assert.Single(database.ListBackups(), b => b.Path == backupPath);
            var restored = await database.RestoreInPlaceAsync(backup, kept, Ct);
            Assert.True(restored.Restored, $"{restored.Failure}: {restored.Detail}");
            var link = Assert.Single(await new SqliteSummaryLinkStore(database).ListAsync(WorkspaceId.Parse(Workspace), Ct));
            Assert.Equal((linked, "对比", SummaryNodeId.Parse(NodeA), SummaryNodeId.Parse(NodeB)), (link.Id, link.Note, link.NodeA, link.NodeB));
            Assert.Equal(0, await database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM link_suggestion;"), Ct));
        }
    }

    private static string Link(string id, string workspace, string a, string b, string note, string origin = "user") =>
        $"INSERT INTO summary_link (id, workspace_id, node_a, node_b, note, origin, created_at) VALUES ('{id}', '{workspace}', '{a}', '{b}', {note}, '{origin}', 1);";

    private static string Suggestion(string id, string a, string b, string state, string reason, string generation) =>
        $"INSERT INTO link_suggestion (id, workspace_id, node_a, node_b, note, reason, state, generation_id, created_at, updated_at) VALUES ('{id}', '{Workspace}', '{a}', '{b}', '对比', {reason}, '{state}', {generation}, 1, 1);";

    /// <summary>Two workspaces; three summaries in the first, one in the second, and a recommendation drawing on the first.</summary>
    private static string SeedNodes(bool withRecommendation)
    {
        var sql = new System.Text.StringBuilder();
        sql.Append(System.Globalization.CultureInfo.InvariantCulture, $"INSERT INTO workspace VALUES ('{Workspace}', '测试', 1); INSERT INTO workspace VALUES ('{OtherWorkspace}', '其他', 2);\n");
        var i = 0;
        foreach (var (node, workspace) in new[] { (NodeA, Workspace), (NodeB, Workspace), (NodeC, Workspace), (NodeOther, OtherWorkspace) })
        {
            i++;
            var conversation = $"0190f000-0000-7000-8000-00000000010{i}";
            var version = $"0190f000-0000-7000-8000-00000000020{i}";
            sql.Append(System.Globalization.CultureInfo.InvariantCulture, $"""
                INSERT INTO conversation VALUES ('{conversation}', '{workspace}', '会话{i}', 'auto', 1, 1);
                INSERT INTO summary_version (id, node_id, seq, title, body, to_seq, origin, created_at) VALUES ('{version}', '{node}', 1, '总结{i}', '正文', 2, 'generated', 5);
                INSERT INTO summary_node (id, workspace_id, conversation_id, current_version_id, base_title, display_name, name_source, created_at, updated_at)
                    VALUES ('{node}', '{workspace}', '{conversation}', '{version}', '总结{i}', '总结{i}', 'base', 5, 5);

                """);
        }

        if (withRecommendation)
        {
            sql.Append(System.Globalization.CultureInfo.InvariantCulture, $"INSERT INTO recommendation (id, workspace_id, kind, question, basis_node_id, generation_id, generated_at, seq, state, updated_at) VALUES ('r', '{Workspace}', 'node', '问', '{NodeA}', 'g', 1, 0, 'pending', 1);");
        }

        return sql.ToString();
    }

    private async Task SeedVersion12Async()
    {
        await using (var old = _dir.Database(new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version <= 12))))
        {
            Assert.True((await old.OpenAsync(Ct)).IsReady);
        }

        using var seed = _dir.Raw();
        SqliteConnections.Execute(seed, "BEGIN;" + SeedNodes(withRecommendation: true) + "COMMIT;");
    }
}
