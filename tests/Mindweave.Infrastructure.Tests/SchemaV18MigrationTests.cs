using Microsoft.Data.Sqlite;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests;

/// <summary>
/// Upgrading version-17 data with exploration places (design session-network §16, D-035): the table starts empty and
/// nothing else changes; a place goes with its root conversation or its workspace and needs a size of at least one; the
/// store reads and saves places, never for a conversation of another workspace or one that is gone. A failing later step
/// leaves version 17 as it was.
/// </summary>
public sealed class SchemaV18MigrationTests : IDisposable
{
    private const int Version = 18;
    private const string Workspace = "0190f000-0000-7000-8000-0000000000aa";
    private const string Other = "0190f000-0000-7000-8000-0000000000bb";
    private const string Trains = "0190f000-0000-7000-8000-000000000101";
    private const string Stars = "0190f000-0000-7000-8000-000000000102";
    private const string Elsewhere = "0190f000-0000-7000-8000-000000000201";

    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Version_17_data_is_kept_and_places_go_with_their_root_conversation_or_workspace()
    {
        await SeedPreviousAsync();

        await using var database = _dir.Database(new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version <= Version)));
        var result = await database.OpenAsync(Ct);
        Assert.True(result.IsReady, $"{result.Status}: {result.ErrorDetail}");
        Assert.Equal(Version - 1, Assert.Single(database.ListBackups()).SchemaVersion);
        await database.CloseAsync();

        using var raw = _dir.Raw();
        Assert.Equal(Version, Scalar<long>(raw, "PRAGMA user_version;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(3, Scalar<long>(raw, "SELECT count(*) FROM conversation;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM exploration_place;"));
        SqliteConnections.Execute(raw, "PRAGMA foreign_keys = ON;");

        SqliteConnections.Execute(raw, $"""
            INSERT INTO exploration_place VALUES ('{Trains}', '{Workspace}', 10.5, -20, 1.57, 4, 1);
            INSERT INTO exploration_place VALUES ('{Stars}', '{Workspace}', 300, 40, 0, 1, 1);
            INSERT INTO exploration_place VALUES ('{Elsewhere}', '{Other}', 0, 0, 0, 1, 1);
            """);
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, $"UPDATE exploration_place SET size = 0 WHERE root_conversation_id = '{Stars}';"));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, $"INSERT INTO exploration_place VALUES ('nope', '{Workspace}', 0, 0, 0, 1, 1);"));

        // A place goes with its root conversation, and the rest with their workspace.
        SqliteConnections.Execute(raw, $"DELETE FROM conversation WHERE id = '{Trains}';");
        Assert.Equal(0, Scalar<long>(raw, $"SELECT count(*) FROM exploration_place WHERE root_conversation_id = '{Trains}';"));
        SqliteConnections.Execute(raw, $"DELETE FROM conversation WHERE workspace_id = '{Other}'; DELETE FROM workspace WHERE id = '{Other}';");
        Assert.Equal(1, Scalar<long>(raw, "SELECT count(*) FROM exploration_place;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task The_store_saves_and_reads_places_only_for_its_workspaces_conversations()
    {
        await SeedPreviousAsync();
        await using var database = _dir.Database();
        Assert.True((await database.OpenAsync(Ct)).IsReady);
        var store = new SqliteSummaryNetworkStore(database);
        var workspace = WorkspaceId.Parse(Workspace);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(5);

        var written = await store.SaveExplorationPlacesAsync(workspace, new Dictionary<ConversationId, ExplorationPlace>
        {
            [ConversationId.Parse(Trains)] = new(10, 20, 1.5, 3),
            [ConversationId.Parse(Elsewhere)] = new(0, 0, 0, 1), // another workspace's: not written
            [ConversationId.Parse("0190f000-0000-7000-8000-000000000999")] = new(0, 0, 0, 1), // gone: not written
        }, at, Ct);
        Assert.Equal(1, written);

        // Saving again changes the place.
        Assert.Equal(1, await store.SaveExplorationPlacesAsync(workspace, new Dictionary<ConversationId, ExplorationPlace> { [ConversationId.Parse(Trains)] = new(12, 20, 1.5, 4) }, at, Ct));
        var places = await store.GetExplorationPlacesAsync(workspace, Ct);
        Assert.Equal(new ExplorationPlace(12, 20, 1.5, 4), Assert.Single(places).Value);
        Assert.Empty(await store.GetExplorationPlacesAsync(WorkspaceId.Parse(Other), Ct));
    }

    [Fact]
    public async Task A_failing_upgrade_after_this_step_leaves_version_17_as_it_was()
    {
        await SeedPreviousAsync();

        await using (var failing = _dir.Database(new MigrationCatalog([.. MigrationCatalog.Default.Migrations.Where(m => m.Version <= Version), new Migration(Version + 1, "broken", (_, _) => throw new InvalidOperationException("simulated failure"))])))
        {
            Assert.Equal(DatabaseOpenStatus.MigrationFailed, (await failing.OpenAsync(Ct)).Status);
        }

        using var raw = _dir.Raw();
        Assert.Equal(Version - 1, Scalar<long>(raw, "PRAGMA user_version;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE name = 'exploration_place';"));
    }

    private static T? Scalar<T>(SqliteConnection connection, string sql) => SqliteConnections.Scalar<T>(connection, sql);

    /// <summary>Two workspaces: one with two root conversations, the other with one.</summary>
    private async Task SeedPreviousAsync()
    {
        await using (var old = _dir.Database(new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version < Version))))
        {
            Assert.True((await old.OpenAsync(Ct)).IsReady);
        }

        using var seed = _dir.Raw();
        SqliteConnections.Execute(seed, $"""
            BEGIN;
            INSERT INTO workspace VALUES ('{Workspace}', '火车', 1);
            INSERT INTO workspace VALUES ('{Other}', '天文', 1);
            INSERT INTO conversation VALUES ('{Trains}', '{Workspace}', NULL, 'none', 1, 1);
            INSERT INTO conversation VALUES ('{Stars}', '{Workspace}', NULL, 'none', 2, 2);
            INSERT INTO conversation VALUES ('{Elsewhere}', '{Other}', NULL, 'none', 3, 3);
            COMMIT;
            """);
    }
}
