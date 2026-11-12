using Microsoft.Data.Sqlite;
using Mindweave.Application.Recommendations;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests;

/// <summary>
/// Upgrading real version-10 data to the recommendation store (2026-11-12, requirements §10.3): everything before is
/// untouched, the new table starts empty and holds what the architecture's Recommendation entity says; its rows go with
/// their workspace and their basis summary, ignored ones included. A failing upgrade leaves version 10 as it was; a fresh
/// database has the table; a backup restored brings its recommendations back as they were.
/// </summary>
public sealed class SchemaV11MigrationTests : IDisposable
{
    private const string Workspace = "0190f000-0000-7000-8000-0000000000aa";
    private const string Conversation = "0190f000-0000-7000-8000-000000000001";
    private const string Node = "0190f000-0000-7000-8000-000000000002";
    private const string Version = "0190f000-0000-7000-8000-000000000003";
    private const string Request = "0190f000-0000-7000-8000-000000000004";

    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Version_10_data_is_kept_and_recommendations_follow_their_workspace_and_basis_summary()
    {
        await SeedVersion10Async();

        await using var database = _dir.Database();
        var result = await database.OpenAsync(Ct);
        Assert.True(result.IsReady, $"{result.Status}: {result.ErrorDetail}");
        Assert.Equal(10, Assert.Single(database.ListBackups()).SchemaVersion);

        using (var raw = _dir.Raw())
        {
            Assert.Equal(11, SqliteConnections.Scalar<long>(raw, "PRAGMA user_version;"));
            Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM pragma_foreign_key_check;"));
            Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM recommendation;"));
            Assert.Equal(1, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM summary_node;"));
            Assert.Equal(700, SqliteConnections.Scalar<long>(raw, $"SELECT prompt_tokens FROM ai_request WHERE id = '{Request}';"));
        }

        var store = new SqliteRecommendationStore(database);
        var workspace = WorkspaceId.Parse(Workspace);
        var node = SummaryNodeId.Parse(Node);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1_000);
        Assert.Equal(3, await store.AddBatchAsync(workspace, RecommendationKind.Question, Batch(at, ("蒸汽机为什么会被淘汰？", "顺着往后", node), ("铁轨用什么做？", null, null), ("刹车怎么工作？", "相邻", null)), false, Ct));
        Assert.Equal(1, await store.AddBatchAsync(workspace, RecommendationKind.Node, Batch(at, ("蒸汽机的效率有多高？", "从原理延伸", node)), false, Ct));

        // Read back in batch order, with the basis's shown name for "延伸自".
        var questions = await store.ListAsync(workspace, RecommendationKind.Question, Ct);
        Assert.Equal(["蒸汽机为什么会被淘汰？", "铁轨用什么做？", "刹车怎么工作？"], questions.Select(q => q.Question));
        Assert.Equal(("蒸汽机", (SummaryNodeId?)node, "顺着往后"), (questions[0].BasisName, questions[0].Basis, questions[0].Reason));
        Assert.All(questions, q => Assert.Equal(RecommendationState.Pending, q.State));

        // Ignored keeps only the normalized question; used stays as it was; neither becomes pending again.
        Assert.True(await store.SetStateAsync(questions[2].Id, RecommendationState.Ignored, RecommendationText.Normalize(questions[2].Question), at, Ct));
        Assert.True(await store.SetStateAsync(questions[1].Id, RecommendationState.Used, "x", at, Ct));
        Assert.False(await store.SetStateAsync(questions[1].Id, RecommendationState.Ignored, "x", at, Ct));
        var after = await store.ListAsync(workspace, RecommendationKind.Question, Ct);
        Assert.Equal(["蒸汽机为什么会被淘汰？"], after.Where(r => r.State == RecommendationState.Pending).Select(r => r.Question));
        var ignored = Assert.Single(after, r => r.State == RecommendationState.Ignored);
        Assert.Equal(("刹车怎么工作", null), (ignored.Question, ignored.Reason));
        Assert.Equal("铁轨用什么做？", Assert.Single(after, r => r.State == RecommendationState.Used).Question);

        using (var raw = _dir.Raw())
        {
            SqliteConnections.Execute(raw, "PRAGMA foreign_keys = ON;");

            // The schema itself refuses a node without its basis, an ignored row with a reason, and an unknown kind or state.
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Insert("n1", "node", "pending", "NULL", "NULL")));
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Insert("n2", "question", "ignored", "'理由'", "NULL")));
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Insert("n3", "other", "pending", "NULL", "NULL")));
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Insert("n4", "question", "shown", "NULL", "NULL")));
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Insert("n5", "question", "pending", "NULL", "'0190f000-0000-7000-8000-0000000000ff'")));

            // Deleting the basis summary removes what drew on it, the ignored ones among them; the others stay.
            SqliteConnections.Execute(raw, $"INSERT INTO recommendation (id, workspace_id, kind, question, basis_node_id, generation_id, generated_at, seq, state, updated_at) VALUES ('ig', '{Workspace}', 'question', '旧问题', '{Node}', 'g', 1, 0, 'ignored', 1);");
            SqliteConnections.Execute(raw, $"BEGIN; DELETE FROM summary_version WHERE node_id = '{Node}'; DELETE FROM summary_node WHERE id = '{Node}'; COMMIT;");
            Assert.Equal(0, SqliteConnections.Scalar<long>(raw, $"SELECT count(*) FROM recommendation WHERE basis_node_id IS NOT NULL;"));
            Assert.Equal(2, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM recommendation;"));

            // Deleting the workspace removes the rest.
            SqliteConnections.Execute(raw, $"DELETE FROM ai_request; DELETE FROM generation_task; DELETE FROM message; DELETE FROM conversation; DELETE FROM workspace WHERE id = '{Workspace}';");
            Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM recommendation;"));
        }

        // A late batch for a workspace that is gone is not stored.
        Assert.Null(await store.AddBatchAsync(workspace, RecommendationKind.Question, Batch(at, ("还在吗？", null, null)), true, Ct));
    }

    [Fact]
    public async Task A_failing_upgrade_after_version_11_leaves_version_10_as_it_was()
    {
        await SeedVersion10Async();

        await using (var failing = _dir.Database(new MigrationCatalog([.. MigrationCatalog.Default.Migrations.Where(m => m.Version <= 11), new Migration(12, "broken", (_, _) => throw new InvalidOperationException("simulated failure"))])))
        {
            Assert.Equal(Application.Startup.DatabaseOpenStatus.MigrationFailed, (await failing.OpenAsync(Ct)).Status);
        }

        using var raw = _dir.Raw();
        Assert.Equal(10, SqliteConnections.Scalar<long>(raw, "PRAGMA user_version;"));
        Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE name = 'recommendation';"));
        Assert.Equal(1, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM summary_node;"));
    }

    [Fact]
    public async Task A_new_database_has_the_store_and_a_restored_backup_brings_its_recommendations_back()
    {
        var now = new DateTimeOffset(2026, 11, 12, 9, 30, 0, TimeSpan.Zero);
        var workspace = WorkspaceId.New();
        string backupPath;
        string kept;
        await using (var database = _dir.Database(time: new ManualTime(now.AddDays(-1))))
        {
            Assert.True((await database.OpenAsync(Ct)).IsReady);
            await database.WriteAsync((c, t) => c.Execute(t, "INSERT INTO workspace VALUES ($id, '火车', 1);", ("$id", workspace.ToString())), Ct);
            var store = new SqliteRecommendationStore(database);
            Assert.Equal(2, await store.AddBatchAsync(workspace, RecommendationKind.Question, Batch(now, ("火车怎么转弯？", null, null), ("车轮为什么是锥形的？", null, null)), true, Ct));
            backupPath = await database.BackupAsync(BackupReason.BeforeWorkspaceDelete, Ct);

            // After the backup: one ignored, then a new batch replaces the pending ones.
            var first = (await store.ListAsync(workspace, RecommendationKind.Question, Ct))[0];
            await store.SetStateAsync(first.Id, RecommendationState.Ignored, RecommendationText.Normalize(first.Question), now, Ct);
            Assert.Equal(1, await store.AddBatchAsync(workspace, RecommendationKind.Question, Batch(now, ("铁路为什么要标准轨距？", null, null)), true, Ct));
            Assert.Equal(["铁路为什么要标准轨距？"], (await store.ListAsync(workspace, RecommendationKind.Question, Ct)).Where(r => r.State == RecommendationState.Pending).Select(r => r.Question));
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
            var rows = await new SqliteRecommendationStore(database).ListAsync(workspace, RecommendationKind.Question, Ct);
            Assert.Equal(["火车怎么转弯？", "车轮为什么是锥形的？"], rows.Select(r => r.Question));
            Assert.All(rows, r => Assert.Equal(RecommendationState.Pending, r.State));
        }
    }

    private static RecommendationBatch Batch(DateTimeOffset at, params (string Question, string? Reason, SummaryNodeId? Basis)[] items) =>
        new(Guid.CreateVersion7(), at, [.. items.Select(i => new NewRecommendation(i.Question, i.Reason, i.Basis))]);

    private static string Insert(string id, string kind, string state, string reason, string basis) =>
        $"INSERT INTO recommendation (id, workspace_id, kind, question, reason, basis_node_id, generation_id, generated_at, seq, state, updated_at) VALUES ('{id}', '{Workspace}', '{kind}', '问', {reason}, {basis}, 'g', 1, 0, '{state}', 1);";

    private async Task SeedVersion10Async()
    {
        await using (var old = _dir.Database(new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version <= 10))))
        {
            Assert.True((await old.OpenAsync(Ct)).IsReady);
        }

        using var seed = _dir.Raw();
        SqliteConnections.Execute(seed, $"""
            INSERT INTO workspace VALUES ('{Workspace}', '测试', 1);
            INSERT INTO conversation VALUES ('{Conversation}', '{Workspace}', '蒸汽机', 'auto', 1, 1);
            BEGIN;
            INSERT INTO summary_version (id, node_id, seq, title, body, to_seq, origin, created_at)
                VALUES ('{Version}', '{Node}', 1, '蒸汽机的原理', '正文', 2, 'generated', 5);
            INSERT INTO summary_node (id, workspace_id, conversation_id, current_version_id, base_title, display_name, name_source, created_at, updated_at)
                VALUES ('{Node}', '{Workspace}', '{Conversation}', '{Version}', '蒸汽机', '蒸汽机', 'base', 5, 5);
            COMMIT;
            INSERT INTO ai_request (id, workspace_id, kind, model, base_url, started_at, finished_at, outcome, output_chars, prompt_tokens, completion_tokens)
                VALUES ('{Request}', '{Workspace}', 'workspace-name', 'm', 'https://x.example/v1', 1, 2, 'completed', 4, 700, 30);
            """);
    }
}
