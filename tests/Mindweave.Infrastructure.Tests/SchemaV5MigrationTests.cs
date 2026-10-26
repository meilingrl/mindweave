using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests;

/// <summary>Upgrading real version-4 data: existing tasks sent no summaries, existing traces have no token counts.</summary>
public sealed class SchemaV5MigrationTests : IDisposable
{
    private const string Conversation = "0190f000-0000-7000-8000-000000000001";
    private const string Answer = "0190f000-0000-7000-8000-000000000003";
    private const string Task = "0190f000-0000-7000-8000-000000000004";

    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async System.Threading.Tasks.Task Version_4_tasks_and_traces_upgrade_with_empty_summary_inputs()
    {
        await using (var old = _dir.Database(new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version <= 4))))
        {
            Assert.True((await old.OpenAsync(Ct)).IsReady);
        }

        using (var raw = _dir.Raw())
        {
            SqliteConnections.Execute(raw, $"""
                INSERT INTO workspace VALUES ('0190f000-0000-7000-8000-0000000000aa', '测试', 1);
                INSERT INTO conversation VALUES ('{Conversation}', '0190f000-0000-7000-8000-0000000000aa', NULL, 'none', 1, 1);
                INSERT INTO message (id, conversation_id, seq, role, state, created_at, completed_at)
                    VALUES ('{Answer}', '{Conversation}', 0, 'user', 'complete', 1, 1);
                INSERT INTO message_block (message_id, ordinal, kind, text) VALUES ('{Answer}', 0, 'text', '问');
                INSERT INTO generation_task (id, kind, conversation_id, target_message_id, state, model_config_id, base_url, protocol,
                                             model, credential_ref, input_message_ids, created_at)
                    VALUES ('{Task}', 'title', '{Conversation}', NULL, 'completed', 'm', 'https://fake.example/v1', 'chat_completions', 'fake', 'c/k', '[]', 2);
                INSERT INTO ai_request (id, task_id, started_at, finished_at, outcome, output_chars)
                    VALUES ('0190f000-0000-7000-8000-000000000005', '{Task}', 2, 3, 'completed', 4);
                """);
        }

        await using var database = _dir.Database();
        Assert.True((await database.OpenAsync(Ct)).IsReady);

        var trace = Assert.Single(await new SqliteAiRequestStore(database).ForTaskAsync(GenerationTaskId.Parse(Task), Ct));
        Assert.Null(trace.PromptTokens);
        Assert.Empty(await new SqliteContextSummaryStore(database).CompressedAnswersAsync(ConversationId.Parse(Conversation), Ct));
        var summaries = new SqliteContextSummaryStore(database);
        var key = new SummaryKey(ConversationId.Parse(Conversation), 0, 0, "h", ContextPlanner.PromptVersion);
        var first = await summaries.AddAsync(new Application.Storage.ContextSummary(Guid.CreateVersion7(), key, "fake", "摘要一", DateTimeOffset.UtcNow), Ct);
        var second = await summaries.AddAsync(new Application.Storage.ContextSummary(Guid.CreateVersion7(), key, "fake", "摘要二", DateTimeOffset.UtcNow), Ct);
        Assert.Equal(first, second);
    }
}
