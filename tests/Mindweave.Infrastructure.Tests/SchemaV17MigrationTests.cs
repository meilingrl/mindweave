using Microsoft.Data.Sqlite;
using Mindweave.Application.Startup;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests;

/// <summary>
/// Upgrading version-16 data to the run schema (architecture §15.3, §15.12, docs/design/runs-and-tools.md): existing tasks
/// keep their rows with no tools, limits or limit reached (they were single requests); resources gain empty producer
/// columns; run steps start empty. A step goes with its task, its result resource may go first (the step keeps NULL), a tool
/// step must name its tool and a model step must not. A failing later step leaves version 16 as it was.
/// </summary>
public sealed class SchemaV17MigrationTests : IDisposable
{
    private const int Version = 17;
    private const string Workspace = "0190f000-0000-7000-8000-0000000000aa";
    private const string Conversation = "0190f000-0000-7000-8000-000000000101";
    private static readonly string Hash = new('a', 64);

    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Version_16_tasks_and_resources_are_kept_and_run_steps_hold_their_rules()
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
        Assert.Equal(1, Scalar<long>(raw, "SELECT count(*) FROM generation_task WHERE tools IS NULL AND run_limits IS NULL AND run_limit_reached IS NULL;"));
        Assert.Equal(1, Scalar<long>(raw, "SELECT count(*) FROM resource WHERE produced_by_task IS NULL AND produced_by_step IS NULL;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM run_step;"));
        SqliteConnections.Execute(raw, "PRAGMA foreign_keys = ON;");

        // A model step and a tool step whose result is the resource.
        SqliteConnections.Execute(raw, """
            INSERT INTO run_step (task_id, ordinal, kind, state, ai_request_id, started_at) VALUES ('t1', 0, 'model', 'completed', 'r1', 1);
            INSERT INTO run_step (task_id, ordinal, kind, state, call_id, tool, risk, approval, input, result_resource_id, started_at, finished_at)
            VALUES ('t1', 1, 'tool', 'completed', 'call_1', 'search_attachments', 'local_read', 'not_needed', '蒸汽机', 'doc', 2, 3);
            UPDATE resource SET produced_by_task = 't1', produced_by_step = 1 WHERE id = 'doc';
            UPDATE generation_task SET run_limit_reached = 'tool_calls', tools = '[]', run_limits = '{}' WHERE id = 't1';
            """);
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, "INSERT INTO run_step (task_id, ordinal, kind, state, started_at) VALUES ('t1', 2, 'tool', 'running', 4);"));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, "INSERT INTO run_step (task_id, ordinal, kind, state, tool, started_at) VALUES ('t1', 2, 'model', 'running', 'x', 4);"));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, "INSERT INTO run_step (task_id, ordinal, kind, state, started_at) VALUES ('t1', 2, 'wait', 'running', 4);"));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, "INSERT INTO run_step (task_id, ordinal, kind, state, started_at) VALUES ('nope', 0, 'model', 'running', 4);"));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, "UPDATE generation_task SET run_limit_reached = 'patience' WHERE id = 't1';"));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, "UPDATE resource SET produced_by_step = -1 WHERE id = 'doc';"));

        // The result resource may go (its conversation is deleted with the task in one transaction; here alone): the step stays.
        SqliteConnections.Execute(raw, "DELETE FROM resource WHERE id = 'doc';");
        Assert.Equal(1, Scalar<long>(raw, "SELECT count(*) FROM run_step WHERE ordinal = 1 AND result_resource_id IS NULL;"));

        // Steps go with their task.
        SqliteConnections.Execute(raw, "DELETE FROM generation_task WHERE id = 't1';");
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM run_step;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task A_failing_upgrade_after_this_step_leaves_version_16_as_it_was()
    {
        await SeedPreviousAsync();

        await using (var failing = _dir.Database(new MigrationCatalog([.. MigrationCatalog.Default.Migrations.Where(m => m.Version <= Version), new Migration(Version + 1, "broken", (_, _) => throw new InvalidOperationException("simulated failure"))])))
        {
            Assert.Equal(DatabaseOpenStatus.MigrationFailed, (await failing.OpenAsync(Ct)).Status);
        }

        using var raw = _dir.Raw();
        Assert.Equal(Version - 1, Scalar<long>(raw, "PRAGMA user_version;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE name = 'run_step';"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM pragma_table_info('generation_task') WHERE name IN ('tools', 'run_limits', 'run_limit_reached');"));
    }

    private static T? Scalar<T>(SqliteConnection connection, string sql) => SqliteConnections.Scalar<T>(connection, sql);

    /// <summary>A workspace with a conversation, an answer task and its request, and a pasted resource with text.</summary>
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
            INSERT INTO conversation VALUES ('{Conversation}', '{Workspace}', NULL, 'none', 1, 1);
            INSERT INTO message (id, conversation_id, seq, role, state, created_at) VALUES ('a1', '{Conversation}', 1, 'assistant', 'complete', 1);
            INSERT INTO message_block (message_id, ordinal, kind, text) VALUES ('a1', 0, 'text', '答');
            INSERT INTO generation_task (id, kind, conversation_id, target_message_id, state, model_config_id, base_url, protocol, model, credential_ref, input_message_ids, created_at)
            VALUES ('t1', 'answer', '{Conversation}', 'a1', 'completed', 'c', 'https://example.invalid', 'chat_completions', 'm', 'k', '[]', 1);
            INSERT INTO ai_request (id, task_id, started_at, finished_at, outcome, output_chars, purpose) VALUES ('r1', 't1', 1, 2, 'completed', 3, 'answer');
            INSERT INTO resource (id, workspace_id, conversation_id, origin, name, media_type, byte_size, sha256, provenance, created_at)
            VALUES ('doc', '{Workspace}', '{Conversation}', 'paste', NULL, 'text/plain', 6, '{Hash}', 'user', 1);
            INSERT INTO resource_derivative (resource_id, kind, producer, text, created_at) VALUES ('doc', 'text', 'plain-text/1', '全文', 1);
            COMMIT;
            """);
    }
}
