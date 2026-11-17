using Microsoft.Data.Sqlite;
using Mindweave.Application.Startup;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests;

/// <summary>
/// Upgrading real version-13 data for conversations started from a recommendation (2026-11-17, requirements §10.3.5):
/// every existing link is kept as it was (the table is rebuilt only to allow the origin 'recommended'), no conversation
/// gains a starting recommendation, and the new table goes with its conversation and only forgets a deleted basis. A
/// failing later step leaves version 13 as it was.
/// </summary>
public sealed class SchemaV14MigrationTests : IDisposable
{
    private const string Workspace = "0190f000-0000-7000-8000-0000000000aa";
    private const string NodeA = "0190f000-0000-7000-8000-00000000000a";
    private const string NodeB = "0190f000-0000-7000-8000-00000000000b";
    private const string NodeC = "0190f000-0000-7000-8000-00000000000c";
    private const string ConversationC = "0190f000-0000-7000-8000-000000000103";
    private const string Started = "0190f000-0000-7000-8000-000000000109";

    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Version_13_links_are_kept_and_the_new_table_follows_its_conversation_and_basis()
    {
        await SeedVersion13Async();

        await using var database = _dir.Database();
        var result = await database.OpenAsync(Ct);
        Assert.True(result.IsReady, $"{result.Status}: {result.ErrorDetail}");
        Assert.Equal(13, Assert.Single(database.ListBackups()).SchemaVersion);

        using var raw = _dir.Raw();
        Assert.True(MigrationCatalog.Default.LatestVersion >= 14);
        Assert.Equal(MigrationCatalog.Default.LatestVersion, SqliteConnections.Scalar<long>(raw, "PRAGMA user_version;"));
        Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(
            "l1|a|b|对比|user|7;l2|b|c||confirmed|8",
            SqliteConnections.Scalar<string>(raw, $"""
                SELECT group_concat(id || '|' || replace(replace(replace(node_a, '{NodeA}', 'a'), '{NodeB}', 'b'), '{NodeC}', 'c') || '|'
                    || replace(replace(replace(node_b, '{NodeA}', 'a'), '{NodeB}', 'b'), '{NodeC}', 'c') || '|' || ifnull(note, '') || '|'
                    || origin || '|' || created_at, ';')
                FROM (SELECT * FROM summary_link ORDER BY id);
                """));
        Assert.Equal(1, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE type = 'index' AND name = 'summary_link_by_b';"));
        Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE name = 'summary_link_v14';"));
        Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM starting_recommendation;"));
        SqliteConnections.Execute(raw, "PRAGMA foreign_keys = ON;");

        // The new origin is allowed; the old rules still hold.
        SqliteConnections.Execute(raw, Link("l3", NodeA, NodeC, "'来自推荐'", "recommended"));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Link("l4", NodeB, NodeC, "NULL", "ai")));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Link("l5", NodeB, NodeA, "NULL", "user"))); // reversed order
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Link("l6", NodeA, NodeB, "NULL", "user"))); // the same pair

        // A starting recommendation: one per conversation, a known kind, a basis that exists or none.
        SqliteConnections.Execute(raw, Starting(Started, "'node'", $"'{NodeA}'"));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Starting(Started, "'question'", "NULL")));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Starting(ConversationC, "'link'", "NULL")));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Starting(ConversationC, "'question'", "'0190f000-0000-7000-8000-0000000000ff'")));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Starting("0190f000-0000-7000-8000-0000000001ff", "'question'", "NULL")));
        SqliteConnections.Execute(raw, Starting(ConversationC, "'question'", "NULL"));

        // Deleting the basis summary only forgets it; deleting the conversation deletes the row.
        SqliteConnections.Execute(raw, $"BEGIN; DELETE FROM summary_node WHERE id = '{NodeA}'; DELETE FROM summary_version WHERE node_id = '{NodeA}'; COMMIT;");
        Assert.Equal(1, SqliteConnections.Scalar<long>(raw, $"SELECT count(*) FROM starting_recommendation WHERE conversation_id = '{Started}' AND basis_node_id IS NULL;"));
        Assert.Equal("l2", SqliteConnections.Scalar<string>(raw, "SELECT group_concat(id) FROM summary_link;"));
        SqliteConnections.Execute(raw, $"BEGIN; DELETE FROM message WHERE conversation_id = '{Started}'; DELETE FROM conversation WHERE id = '{Started}'; COMMIT;");
        Assert.Equal(ConversationC, SqliteConnections.Scalar<string>(raw, "SELECT group_concat(conversation_id) FROM starting_recommendation;"));
        Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task A_failing_upgrade_after_version_14_leaves_version_13_as_it_was()
    {
        await SeedVersion13Async();

        await using (var failing = _dir.Database(new MigrationCatalog([.. MigrationCatalog.Default.Migrations.Where(m => m.Version <= 14), new Migration(15, "broken", (_, _) => throw new InvalidOperationException("simulated failure"))])))
        {
            Assert.Equal(DatabaseOpenStatus.MigrationFailed, (await failing.OpenAsync(Ct)).Status);
        }

        using var raw = _dir.Raw();
        Assert.Equal(13, SqliteConnections.Scalar<long>(raw, "PRAGMA user_version;"));
        Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE name IN ('starting_recommendation', 'summary_link_v14');"));
        Assert.Equal(2, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM summary_link;"));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Link("l3", NodeA, NodeC, "NULL", "recommended")));
    }

    private static string Link(string id, string a, string b, string note, string origin) =>
        $"INSERT INTO summary_link (id, workspace_id, node_a, node_b, note, origin, created_at) VALUES ('{id}', '{Workspace}', '{a}', '{b}', {note}, '{origin}', 9);";

    private static string Starting(string conversation, string kind, string basis) =>
        $"INSERT INTO starting_recommendation (conversation_id, recommendation_id, kind, basis_node_id, applied_at, created_at) VALUES ('{conversation}', 'r', {kind}, {basis}, NULL, 9);";

    /// <summary>Three summaries, two links between them, and a fourth conversation without a summary.</summary>
    private async Task SeedVersion13Async()
    {
        await using (var old = _dir.Database(new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version <= 13))))
        {
            Assert.True((await old.OpenAsync(Ct)).IsReady);
        }

        var sql = new System.Text.StringBuilder();
        sql.Append(System.Globalization.CultureInfo.InvariantCulture, $"BEGIN; INSERT INTO workspace VALUES ('{Workspace}', '测试', 1);\n");
        var i = 0;
        foreach (var node in new[] { NodeA, NodeB, NodeC })
        {
            i++;
            var conversation = $"0190f000-0000-7000-8000-00000000010{i}";
            var version = $"0190f000-0000-7000-8000-00000000020{i}";
            sql.Append(System.Globalization.CultureInfo.InvariantCulture, $"""
                INSERT INTO conversation VALUES ('{conversation}', '{Workspace}', '会话{i}', 'auto', 1, 1);
                INSERT INTO summary_version (id, node_id, seq, title, body, to_seq, origin, created_at) VALUES ('{version}', '{node}', 1, '总结{i}', '正文', 2, 'generated', 5);
                INSERT INTO summary_node (id, workspace_id, conversation_id, current_version_id, base_title, display_name, name_source, created_at, updated_at)
                    VALUES ('{node}', '{Workspace}', '{conversation}', '{version}', '总结{i}', '总结{i}', 'base', 5, 5);

                """);
        }

        sql.Append(System.Globalization.CultureInfo.InvariantCulture, $"""
            INSERT INTO conversation VALUES ('{Started}', '{Workspace}', '推荐的会话', 'auto', 1, 1);
            INSERT INTO summary_link (id, workspace_id, node_a, node_b, note, origin, created_at) VALUES ('l1', '{Workspace}', '{NodeA}', '{NodeB}', '对比', 'user', 7);
            INSERT INTO summary_link (id, workspace_id, node_a, node_b, note, origin, created_at) VALUES ('l2', '{Workspace}', '{NodeB}', '{NodeC}', NULL, 'confirmed', 8);
            COMMIT;
            """);
        using var seed = _dir.Raw();
        SqliteConnections.Execute(seed, sql.ToString());
    }
}
