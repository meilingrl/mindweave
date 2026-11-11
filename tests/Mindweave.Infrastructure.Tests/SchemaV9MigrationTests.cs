using Microsoft.Data.Sqlite;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests;

/// <summary>
/// Upgrading real version-8 data to request token estimates: older requests keep their billed counts but have no
/// estimate, so they are not used to learn how the model counts.
/// </summary>
public sealed class SchemaV9MigrationTests : IDisposable
{
    private const string Workspace = "0190f000-0000-7000-8000-0000000000aa";
    private const string Conversation = "0190f000-0000-7000-8000-000000000001";
    private const string Task = "0190f000-0000-7000-8000-000000000002";
    private const string Request = "0190f000-0000-7000-8000-000000000003";

    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Version_8_requests_upgrade_without_an_estimate_and_are_not_learned_from()
    {
        await using (var old = _dir.Database(new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version <= 8))))
        {
            Assert.True((await old.OpenAsync(Ct)).IsReady);
        }

        using (var seed = _dir.Raw())
        {
            SqliteConnections.Execute(seed, $"""
                INSERT INTO workspace VALUES ('{Workspace}', '测试', 1);
                INSERT INTO conversation VALUES ('{Conversation}', '{Workspace}', NULL, 'none', 1, 1);
                INSERT INTO generation_task (id, kind, conversation_id, state, model_config_id, base_url, protocol, model, credential_ref, input_message_ids, created_at)
                    VALUES ('{Task}', 'title', '{Conversation}', 'completed', 'cfg', 'https://x.example/v1', 'chat_completions', 'm', 'cred', '[]', 1);
                INSERT INTO ai_request (id, task_id, started_at, finished_at, outcome, output_chars, prompt_tokens)
                    VALUES ('{Request}', '{Task}', 1, 2, 'completed', 4, 700);
                """);
        }

        await using var database = _dir.Database();
        var result = await database.OpenAsync(Ct);
        Assert.True(result.IsReady);
        Assert.Equal(8, Assert.Single(database.ListBackups()).SchemaVersion);

        using var raw = _dir.Raw();
        Assert.Equal(MigrationCatalog.Default.LatestVersion, SqliteConnections.Scalar<long>(raw, "PRAGMA user_version;"));
        Assert.Equal(700, SqliteConnections.Scalar<long>(raw, "SELECT prompt_tokens FROM ai_request;"));
        Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM ai_request WHERE estimated_tokens IS NOT NULL;"));
        Assert.Equal(1, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE name = 'generation_task_by_model';"));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, "UPDATE ai_request SET estimated_tokens = -1;"));
        Assert.Empty(await new SqliteAiRequestStore(database).RecentTokenSamplesAsync("https://x.example/v1", "m", 50, Ct));
    }
}
