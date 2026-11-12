using Microsoft.Data.Sqlite;
using Mindweave.Application.Conversations;
using Mindweave.Application.Diagnostics;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests;

/// <summary>
/// Upgrading real version-9 data to requests without a task (2026-11-11): every request keeps its task, timings and token
/// counts; a request may now instead belong to a workspace and a kind, with its model, and goes with the workspace. A
/// failing upgrade leaves version 9 as it was.
/// </summary>
public sealed class SchemaV10MigrationTests : IDisposable
{
    private const string Workspace = "0190f000-0000-7000-8000-0000000000aa";
    private const string Conversation = "0190f000-0000-7000-8000-000000000001";
    private const string Task = "0190f000-0000-7000-8000-000000000002";
    private const string Request = "0190f000-0000-7000-8000-000000000003";
    private const string Other = "0190f000-0000-7000-8000-000000000004";

    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Version_9_requests_keep_their_task_and_a_request_may_belong_to_a_workspace_instead()
    {
        await SeedVersion9Async();

        await using var database = _dir.Database();
        var result = await database.OpenAsync(Ct);
        Assert.True(result.IsReady, $"{result.Status}: {result.ErrorDetail}");
        Assert.Equal(9, Assert.Single(database.ListBackups()).SchemaVersion);

        using (var raw = _dir.Raw())
        {
            Assert.Equal(MigrationCatalog.Default.LatestVersion, SqliteConnections.Scalar<long>(raw, "PRAGMA user_version;"));
            Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM pragma_foreign_key_check;"));
            Assert.Equal(1, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE name = 'ai_request_by_task';"));
            Assert.Equal(1, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE name = 'ai_request_by_workspace';"));
            Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT \"notnull\" FROM pragma_table_info('ai_request') WHERE name = 'task_id';"));
        }

        var store = new SqliteAiRequestStore(database);
        var kept = Assert.Single(await store.ForTaskAsync(GenerationTaskId.Parse(Task), Ct));
        Assert.Equal((AiRequestOutcome.Completed, 700, 30, 650, 4, 120L), (kept.Outcome, kept.PromptTokens, kept.CompletionTokens, kept.EstimatedTokens, kept.OutputCharacters, (long)kept.TimeToResponse!.Value.TotalMilliseconds));
        Assert.Null(kept.Attribution);
        Assert.Equal([new TokenSample(650, 700)], await store.RecentTokenSamplesAsync("https://x.example/v1", "m", 50, Ct));

        // A request for a workspace: recorded with its model, read back by workspace.
        var workspace = WorkspaceId.Parse(Workspace);
        var owner = new RequestAttribution(workspace, RequestAttribution.WorkspaceNames);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(5);
        await store.AddAsync(new AiRequestRecord(Guid.CreateVersion7(), null, at, at, null, null, AiRequestOutcome.Completed, 200, null, null, null, 12, 300, 20, 280, owner, "m", "https://x.example/v1"), Ct);
        var named = Assert.Single(await store.ForWorkspaceAsync(workspace, Ct));
        Assert.Equal((owner, "m", "https://x.example/v1", 300, 20), (named.Attribution, named.Model, named.BaseUrl, named.PromptTokens, named.CompletionTokens));

        // Neither or both owners, or a workspace request without its kind or model, are refused by the schema itself.
        using (var raw = _dir.Raw())
        {
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, $"INSERT INTO ai_request (id, started_at, finished_at, outcome, output_chars) VALUES ('{Other}', 1, 2, 'completed', 0);"));
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, $"INSERT INTO ai_request (id, task_id, workspace_id, kind, model, base_url, started_at, finished_at, outcome, output_chars) VALUES ('{Other}', '{Task}', '{Workspace}', 'workspace-name', 'm', 'u', 1, 2, 'completed', 0);"));
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, $"INSERT INTO ai_request (id, workspace_id, model, base_url, started_at, finished_at, outcome, output_chars) VALUES ('{Other}', '{Workspace}', 'm', 'u', 1, 2, 'completed', 0);"));
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, $"INSERT INTO ai_request (id, workspace_id, kind, started_at, finished_at, outcome, output_chars) VALUES ('{Other}', '{Workspace}', 'workspace-name', 1, 2, 'completed', 0);"));
            Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, $"INSERT INTO ai_request (id, task_id, kind, started_at, finished_at, outcome, output_chars) VALUES ('{Other}', '{Task}', 'workspace-name', 1, 2, 'completed', 0);"));

            // A workspace's requests go with it, as a task's go with the task.
            SqliteConnections.Execute(raw, "PRAGMA foreign_keys = ON;");
            SqliteConnections.Execute(raw, $"DELETE FROM generation_task; DELETE FROM message; DELETE FROM conversation; DELETE FROM workspace WHERE id = '{Workspace}';");
            Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM ai_request;"));
        }

        // The store refuses what the schema would: no owner, both, or a kind it does not know.
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(new AiRequestRecord(Guid.CreateVersion7(), null, at, at, null, null, AiRequestOutcome.Completed, null, null, null, null, 0), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(new AiRequestRecord(Guid.CreateVersion7(), null, at, at, null, null, AiRequestOutcome.Completed, null, null, null, null, 0, Attribution: owner with { Kind = "other" }, Model: "m", BaseUrl: "u"), Ct));
    }

    [Fact]
    public async Task A_failing_upgrade_after_version_10_leaves_version_9_requests_as_they_were()
    {
        await SeedVersion9Async();

        await using (var failing = _dir.Database(new MigrationCatalog([.. MigrationCatalog.Default.Migrations, new Migration(MigrationCatalog.Default.LatestVersion + 1, "broken", (_, _) => throw new InvalidOperationException("simulated failure"))])))
        {
            Assert.Equal(Application.Startup.DatabaseOpenStatus.MigrationFailed, (await failing.OpenAsync(Ct)).Status);
        }

        using var raw = _dir.Raw();
        Assert.Equal(9, SqliteConnections.Scalar<long>(raw, "PRAGMA user_version;"));
        Assert.Equal(1, SqliteConnections.Scalar<long>(raw, "SELECT \"notnull\" FROM pragma_table_info('ai_request') WHERE name = 'task_id';"));
        Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM pragma_table_info('ai_request') WHERE name = 'workspace_id';"));
        Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE name LIKE 'ai_request_v10%';"));
        Assert.Equal(700, SqliteConnections.Scalar<long>(raw, $"SELECT prompt_tokens FROM ai_request WHERE id = '{Request}';"));
    }

    private async Task SeedVersion9Async()
    {
        await using (var old = _dir.Database(new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version <= 9))))
        {
            Assert.True((await old.OpenAsync(Ct)).IsReady);
        }

        using var seed = _dir.Raw();
        SqliteConnections.Execute(seed, $"""
            INSERT INTO workspace VALUES ('{Workspace}', '测试', 1);
            INSERT INTO conversation VALUES ('{Conversation}', '{Workspace}', NULL, 'none', 1, 1);
            INSERT INTO generation_task (id, kind, conversation_id, state, model_config_id, base_url, protocol, model, credential_ref, input_message_ids, created_at)
                VALUES ('{Task}', 'title', '{Conversation}', 'completed', 'cfg', 'https://x.example/v1', 'chat_completions', 'm', 'cred', '[]', 1);
            INSERT INTO ai_request (id, task_id, started_at, finished_at, response_ms, outcome, http_status, output_chars, prompt_tokens, completion_tokens, estimated_tokens)
                VALUES ('{Request}', '{Task}', 1, 2, 120, 'completed', 200, 4, 700, 30, 650);
            """);
    }
}
