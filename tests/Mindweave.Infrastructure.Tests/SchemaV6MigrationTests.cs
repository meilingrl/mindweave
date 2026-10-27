using Microsoft.Data.Sqlite;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests;

/// <summary>
/// Upgrading real version-5 data: the task table is rebuilt for the new kinds without losing tasks or their request
/// traces, and the summary tables enforce their cross references at commit.
/// </summary>
public sealed class SchemaV6MigrationTests : IDisposable
{
    private const string Workspace = "0190f000-0000-7000-8000-0000000000aa";
    private const string Conversation = "0190f000-0000-7000-8000-000000000001";
    private const string Answer = "0190f000-0000-7000-8000-000000000003";
    private const string Task = "0190f000-0000-7000-8000-000000000004";
    private const string Node = "0190f000-0000-7000-8000-000000000010";
    private const string Version = "0190f000-0000-7000-8000-000000000011";

    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async System.Threading.Tasks.Task Version_5_tasks_and_traces_survive_the_task_table_rebuild()
    {
        await SeedVersion5Async();

        await using var database = _dir.Database();
        Assert.True((await database.OpenAsync(Ct)).IsReady);

        var trace = Assert.Single(await new SqliteAiRequestStore(database).ForTaskAsync(GenerationTaskId.Parse(Task), Ct));
        Assert.Equal(12, trace.PromptTokens);
        using var raw = _dir.Raw();
        Assert.Equal("answer", SqliteConnections.Scalar<string>(raw, $"SELECT kind FROM generation_task WHERE id = '{Task}';"));
        Assert.Equal("[\"x\"]", SqliteConnections.Scalar<string>(raw, $"SELECT input_summary_ids FROM generation_task WHERE id = '{Task}';"));
        Assert.Equal(1, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE name = 'generation_task_unfinished';"));
        Assert.Null(SqliteConnections.ForeignKeyViolation(raw));
    }

    [Fact]
    public async System.Threading.Tasks.Task A_summary_task_needs_its_coverage()
    {
        await SeedVersion5Async();
        await using (var database = _dir.Database())
        {
            Assert.True((await database.OpenAsync(Ct)).IsReady);
        }

        using var raw = _dir.Raw();
        var ex = Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, Task2("summary", "NULL")));
        Assert.Contains("CHECK", ex.Message, StringComparison.Ordinal);
        SqliteConnections.Execute(raw, Task2("summary", "3"));
    }

    [Fact]
    public async System.Threading.Tasks.Task A_node_and_its_first_version_are_created_together_or_not_at_all()
    {
        await SeedVersion5Async();
        await using (var database = _dir.Database())
        {
            Assert.True((await database.OpenAsync(Ct)).IsReady);
        }

        using var raw = _dir.Raw();

        // A node whose current version never appears fails at commit, not at the insert.
        using (var transaction = raw.BeginTransaction())
        {
            SqliteConnections.Execute(raw, NodeRow, transaction);
            Assert.Throws<SqliteException>(transaction.Commit);
        }

        Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM summary_node;"));

        using (var transaction = raw.BeginTransaction())
        {
            SqliteConnections.Execute(raw, NodeRow, transaction);
            SqliteConnections.Execute(raw, $"""
                INSERT INTO summary_version (id, node_id, seq, title, body, to_seq, origin, created_at)
                    VALUES ('{Version}', '{Node}', 1, '蒸汽机', '正文', 1, 'generated', 5);
                """, transaction);
            transaction.Commit();
        }

        Assert.Equal(1, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM summary_node;"));
    }

    private const string NodeRow = $"""
        INSERT INTO summary_node (id, workspace_id, conversation_id, current_version_id, base_title, display_name, name_source, created_at, updated_at)
            VALUES ('{Node}', '{Workspace}', '{Conversation}', '{Version}', '蒸汽机', '蒸汽机', 'base', 5, 5);
        """;

    private static string Task2(string kind, string toSeq) => $"""
        INSERT INTO generation_task (id, kind, conversation_id, state, model_config_id, base_url, protocol, model, credential_ref,
                                     input_message_ids, created_at, summary_to_seq)
            VALUES ('{Guid.CreateVersion7()}', '{kind}', '{Conversation}', 'queued', 'm', 'https://fake.example/v1', 'chat_completions',
                    'fake', 'c/k', '[]', 6, {toSeq});
        """;

    private async System.Threading.Tasks.Task SeedVersion5Async()
    {
        await using (var old = _dir.Database(new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version <= 5))))
        {
            Assert.True((await old.OpenAsync(Ct)).IsReady);
        }

        using var raw = _dir.Raw();
        SqliteConnections.Execute(raw, $"""
            INSERT INTO workspace VALUES ('{Workspace}', '测试', 1);
            INSERT INTO conversation VALUES ('{Conversation}', '{Workspace}', NULL, 'none', 1, 1);
            INSERT INTO message (id, conversation_id, seq, role, state, created_at, completed_at)
                VALUES ('{Answer}', '{Conversation}', 0, 'assistant', 'complete', 1, 1);
            INSERT INTO message_block (message_id, ordinal, kind, text) VALUES ('{Answer}', 0, 'text', '答');
            INSERT INTO generation_task (id, kind, conversation_id, target_message_id, state, model_config_id, base_url, protocol,
                                         model, credential_ref, input_message_ids, created_at, input_summary_ids)
                VALUES ('{Task}', 'answer', '{Conversation}', '{Answer}', 'completed', 'm', 'https://fake.example/v1', 'chat_completions',
                        'fake', 'c/k', '[]', 2, '["x"]');
            INSERT INTO ai_request (id, task_id, started_at, finished_at, outcome, output_chars, prompt_tokens)
                VALUES ('0190f000-0000-7000-8000-000000000005', '{Task}', 2, 3, 'completed', 4, 12);
            """);
    }
}
