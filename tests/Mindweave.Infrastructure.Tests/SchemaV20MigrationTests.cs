using Microsoft.Data.Sqlite;
using Mindweave.Application.Startup;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests;

/// <summary>
/// Upgrading version-19 data with the cached input of a request (prompt caching step 1, requirements §10.11.4, architecture
/// §18.5): the requests already recorded keep every count and get NULL for both new ones, which means not reported, never
/// zero; a negative count is refused. A failing later step leaves version 19 as it was.
/// </summary>
public sealed class SchemaV20MigrationTests : IDisposable
{
    private const int Version = 20;
    private const string Workspace = "0190f000-0000-7000-8000-0000000000aa";
    private const string Conversation = "0190f000-0000-7000-8000-000000000101";
    private const string Task = "0190f000-0000-7000-8000-000000000201";
    private const string Request = "0190f000-0000-7000-8000-000000000301";
    private const string Named = "0190f000-0000-7000-8000-000000000302";

    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Version_19_requests_are_kept_and_their_cached_input_is_not_reported()
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
        Assert.Equal(2, Scalar<long>(raw, "SELECT count(*) FROM ai_request;"));
        Assert.Equal(
            "700|70|650|NULL|NULL",
            Scalar<string>(raw, $"""
                SELECT prompt_tokens || '|' || completion_tokens || '|' || estimated_tokens || '|'
                       || coalesce(cache_read_tokens, 'NULL') || '|' || coalesce(cache_write_tokens, 'NULL')
                FROM ai_request WHERE id = '{Request}';
                """));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM ai_request WHERE cache_read_tokens IS NOT NULL OR cache_write_tokens IS NOT NULL;"));
        Assert.Equal("workspace-name", Scalar<string>(raw, $"SELECT kind FROM ai_request WHERE id = '{Named}';"));

        // Zero is a reported count, and is kept; a negative one is refused.
        SqliteConnections.Execute(raw, $"UPDATE ai_request SET cache_read_tokens = 0, cache_write_tokens = 640 WHERE id = '{Request}';");
        Assert.Equal(640, Scalar<long>(raw, $"SELECT cache_read_tokens + cache_write_tokens FROM ai_request WHERE id = '{Request}';"));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, $"UPDATE ai_request SET cache_read_tokens = -1 WHERE id = '{Request}';"));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, $"UPDATE ai_request SET cache_write_tokens = -1 WHERE id = '{Request}';"));
    }

    [Fact]
    public async Task A_failing_upgrade_after_this_step_leaves_version_19_as_it_was()
    {
        await SeedPreviousAsync();

        await using (var failing = _dir.Database(new MigrationCatalog([.. MigrationCatalog.Default.Migrations.Where(m => m.Version <= Version), new Migration(Version + 1, "broken", (_, _) => throw new InvalidOperationException("simulated failure"))])))
        {
            Assert.Equal(DatabaseOpenStatus.MigrationFailed, (await failing.OpenAsync(Ct)).Status);
        }

        using var raw = _dir.Raw();
        Assert.Equal(Version - 1, Scalar<long>(raw, "PRAGMA user_version;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM pragma_table_info('ai_request') WHERE name LIKE 'cache_%';"));
        Assert.Equal(2, Scalar<long>(raw, "SELECT count(*) FROM ai_request;"));
    }

    private static T? Scalar<T>(SqliteConnection connection, string sql) => SqliteConnections.Scalar<T>(connection, sql);

    /// <summary>One answer task with its request, and one request made for the workspace without a task.</summary>
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
            INSERT INTO conversation VALUES ('{Conversation}', '{Workspace}', NULL, 'none', 1, 1);
            INSERT INTO generation_task (id, kind, conversation_id, state, model_config_id, base_url, protocol, model, credential_ref,
                                         input_message_ids, created_at, started_at, finished_at)
            VALUES ('{Task}', 'title', '{Conversation}', 'completed', 'seed', 'https://api.example.com/v1', 'chat_completions', 'm', 'seed/key', '[]', 1, 1, 2);
            INSERT INTO ai_request (id, task_id, started_at, finished_at, outcome, output_chars, prompt_tokens, completion_tokens, estimated_tokens)
            VALUES ('{Request}', '{Task}', 1, 2, 'completed', 10, 700, 70, 650);
            INSERT INTO ai_request (id, workspace_id, kind, model, base_url, started_at, finished_at, outcome, output_chars, prompt_tokens, completion_tokens)
            VALUES ('{Named}', '{Workspace}', 'workspace-name', 'm', 'https://api.example.com/v1', 3, 4, 'completed', 5, 80, 30);
            COMMIT;
            """);
    }
}
