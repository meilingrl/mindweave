using Microsoft.Data.Sqlite;
using Mindweave.Application.Startup;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests;

/// <summary>
/// Upgrading real version-14 data to resources (evolution D5 phase 1, docs/design/resources.md). It is v15: the
/// recommendation-association step took v14 first (renumbered at merge, 2026-11-17). What
/// was there is untouched, every existing block keeps no resource, and the two tables start empty; the schema itself holds
/// a resource to its workspace and conversation, accepts only a SHA-256 hex hash, takes resources along with their
/// conversation or workspace, and keeps a resource a message refers to. A failing later step leaves version 14 as it was.
/// </summary>
public sealed class SchemaV15MigrationTests : IDisposable
{
    private const int Version = 15;
    private const string Workspace = "0190f000-0000-7000-8000-0000000000aa";
    private const string OtherWorkspace = "0190f000-0000-7000-8000-0000000000bb";
    private const string Conversation = "0190f000-0000-7000-8000-000000000101";
    private const string OtherConversation = "0190f000-0000-7000-8000-000000000102";
    private const string Question = "0190f000-0000-7000-8000-000000000201";
    private static readonly string Hash = new('a', 64);

    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Version_14_data_is_kept_and_the_schema_holds_the_resource_rules_and_cascades()
    {
        await SeedPreviousAsync();

        // Opened at this step's version: later steps change these tables (v16 replaces trusted with provenance).
        await using var database = _dir.Database(new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version <= Version)));
        var result = await database.OpenAsync(Ct);
        Assert.True(result.IsReady, $"{result.Status}: {result.ErrorDetail}");
        Assert.Equal(Version - 1, Assert.Single(database.ListBackups()).SchemaVersion);
        await database.CloseAsync();

        using var raw = _dir.Raw();
        Assert.True(MigrationCatalog.Default.LatestVersion >= Version);
        Assert.Equal(Version, Scalar<long>(raw, "PRAGMA user_version;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM resource;") + Scalar<long>(raw, "SELECT count(*) FROM resource_derivative;"));
        Assert.Equal(("text", "问题", 0L), (Scalar<string>(raw, "SELECT kind FROM message_block;"), Scalar<string>(raw, "SELECT text FROM message_block;"), Scalar<long>(raw, "SELECT count(*) FROM message_block WHERE resource_id IS NOT NULL;")));
        SqliteConnections.Execute(raw, "PRAGMA foreign_keys = ON;");

        // A resource of the workspace, unsent or in one of its conversations; never in another workspace's conversation.
        SqliteConnections.Execute(raw, Resource("r1", Workspace, $"'{Conversation}'", Hash));
        SqliteConnections.Execute(raw, Resource("r2", Workspace, "NULL", Hash));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Resource("r3", OtherWorkspace, $"'{Conversation}'", Hash)));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Resource("r4", Workspace, "NULL", new string('A', 64)))); // upper case
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Resource("r5", Workspace, "NULL", new string('a', 63))));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Resource("r6", Workspace, "NULL", "../" + new string('a', 61))));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Resource("r7", "0190f000-0000-7000-8000-0000000000cc", "NULL", Hash)));
        SqliteConnections.Execute(raw, "INSERT INTO resource_derivative (resource_id, kind, text, producer, created_at) VALUES ('r1', 'text', '全文', 'plain-text/1', 1);");
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, "INSERT INTO resource_derivative (resource_id, kind, text, producer, created_at) VALUES ('r1', 'text', '又一份', 'plain-text/1', 1);"));

        // A block refers to r1: r1 cannot go on its own, and a block cannot name a resource that does not exist.
        SqliteConnections.Execute(raw, $"INSERT INTO message_block (message_id, ordinal, kind, text, resource_id) VALUES ('{Question}', 1, 'attachment', '', 'r1');");
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, "DELETE FROM resource WHERE id = 'r1';"));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, $"INSERT INTO message_block (message_id, ordinal, kind, text, resource_id) VALUES ('{Question}', 2, 'attachment', '', 'missing');"));

        // Deleting the conversation as the app does (messages first) takes r1 and its text along; the unsent r2 stays with
        // the workspace and goes with it.
        SqliteConnections.Execute(raw, $"BEGIN; DELETE FROM message WHERE conversation_id = '{Conversation}'; DELETE FROM conversation WHERE id = '{Conversation}'; COMMIT;");
        Assert.Equal("r2", Scalar<string>(raw, "SELECT group_concat(id) FROM resource;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM resource_derivative;"));
        SqliteConnections.Execute(raw, $"BEGIN; DELETE FROM conversation WHERE workspace_id = '{Workspace}'; DELETE FROM workspace WHERE id = '{Workspace}'; COMMIT;");
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM resource;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task A_failing_upgrade_after_this_step_leaves_version_14_as_it_was()
    {
        await SeedPreviousAsync();

        await using (var failing = _dir.Database(new MigrationCatalog([.. MigrationCatalog.Default.Migrations.Where(m => m.Version <= Version), new Migration(Version + 1, "broken", (_, _) => throw new InvalidOperationException("simulated failure"))])))
        {
            Assert.Equal(DatabaseOpenStatus.MigrationFailed, (await failing.OpenAsync(Ct)).Status);
        }

        using var raw = _dir.Raw();
        Assert.Equal(Version - 1, Scalar<long>(raw, "PRAGMA user_version;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE name IN ('resource', 'resource_derivative', 'conversation_by_workspace_id', 'message_block_by_resource');"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM pragma_table_info('message_block') WHERE name = 'resource_id';"));
        Assert.Equal(1, Scalar<long>(raw, "SELECT count(*) FROM message_block;"));
    }

    private static string Resource(string id, string workspace, string conversation, string hash) =>
        $"INSERT INTO resource (id, workspace_id, conversation_id, sha256, media_type, byte_size, origin, name, source_url, trusted, created_at) VALUES ('{id}', '{workspace}', {conversation}, '{hash}', 'text/plain', 6, 'paste', NULL, NULL, 1, 1);";

    private static T? Scalar<T>(SqliteConnection connection, string sql) => SqliteConnections.Scalar<T>(connection, sql);

    /// <summary>Two workspaces with a conversation each; the first has a question with one text block.</summary>
    private async Task SeedPreviousAsync()
    {
        await using (var old = _dir.Database(new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version < Version))))
        {
            Assert.True((await old.OpenAsync(Ct)).IsReady);
        }

        using var seed = _dir.Raw();
        SqliteConnections.Execute(seed, $"""
            BEGIN;
            INSERT INTO workspace VALUES ('{Workspace}', '测试', 1);
            INSERT INTO workspace VALUES ('{OtherWorkspace}', '其他', 2);
            INSERT INTO conversation VALUES ('{Conversation}', '{Workspace}', NULL, 'none', 1, 1);
            INSERT INTO conversation VALUES ('{OtherConversation}', '{OtherWorkspace}', NULL, 'none', 1, 1);
            INSERT INTO message (id, conversation_id, seq, role, state, revision, created_at, completed_at) VALUES ('{Question}', '{Conversation}', 0, 'user', 'complete', 0, 1, 1);
            INSERT INTO message_block (message_id, ordinal, kind, text) VALUES ('{Question}', 0, 'text', '问题');
            COMMIT;
            """);
    }
}
