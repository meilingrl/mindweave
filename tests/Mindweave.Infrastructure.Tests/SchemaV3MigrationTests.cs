using Microsoft.Data.Sqlite;
using Mindweave.Application.Diagnostics;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests;

/// <summary>Upgrading real version-2 data: bodies become text blocks, stored details lose any key.</summary>
public sealed class SchemaV3MigrationTests : IDisposable
{
    private const string Conversation = "0190f000-0000-7000-8000-000000000001";
    private const string Question = "0190f000-0000-7000-8000-000000000002";
    private const string Answer = "0190f000-0000-7000-8000-000000000003";
    private const string Leaky = "HTTP 401: Incorrect API key provided: sk-proj-AbCdEf123456";

    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Version_2_data_is_carried_into_blocks_and_scrubbed()
    {
        await SeedVersion2Async();
        await using var database = _dir.Database();

        Assert.True((await database.OpenAsync(Ct)).IsReady);

        var messages = await new SqliteConversationStore(database).GetMessagesAsync(ConversationId.Parse(Conversation), Ct);
        Assert.Equal(["火车是怎么发明的？", "第一段\n\n第二段"], messages.Select(m => m.Text));
        Assert.All(messages, m => Assert.IsType<TextBlock>(Assert.Single(m.Blocks)));
        Assert.DoesNotContain("sk-proj", messages[1].ErrorDetail, StringComparison.Ordinal);
        Assert.Contains(Redaction.Mask, messages[1].ErrorDetail, StringComparison.Ordinal);

        var settings = await new SqliteModelSettingsStore(database).GetActiveAsync(Ct);
        Assert.Equal(ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions), settings!.Capabilities);

        await database.CloseAsync();
        using var raw = _dir.Raw();
        Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM pragma_table_info('message') WHERE name = 'body';"));
        Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM generation_task WHERE error_detail LIKE '%sk-proj%';"));
    }

    private async Task SeedVersion2Async()
    {
        await using (var database = _dir.Database(new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version <= 2))))
        {
            Assert.True((await database.OpenAsync(Ct)).IsReady);
        }

        using var raw = _dir.Raw();
        SqliteConnections.Execute(raw, $"""
            INSERT INTO workspace VALUES ('w', '测试', 1);
            INSERT INTO conversation VALUES ('{Conversation}', 'w', NULL, 'none', 1, 1);
            INSERT INTO message (id, conversation_id, seq, role, body, state, created_at, completed_at)
                VALUES ('{Question}', '{Conversation}', 0, 'user', '火车是怎么发明的？', 'complete', 1, 1);
            INSERT INTO message (id, conversation_id, seq, role, body, state, error_kind, error_detail, created_at, completed_at)
                VALUES ('{Answer}', '{Conversation}', 1, 'assistant', '第一段

            第二段', 'failed', 'unauthorized', '{Leaky}', 2, 3);
            INSERT INTO service_connection VALUES ('c', 'fake', 'https://fake.example/v1', 'chat_completions', 'c/k', 1, 1);
            INSERT INTO model_config (id, connection_id, model, created_at, updated_at) VALUES ('m', 'c', 'fake-model', 1, 1);
            INSERT INTO app_state VALUES ('active_model_config_id', 'm');
            INSERT INTO generation_task (id, kind, conversation_id, target_message_id, state, model_config_id, base_url, protocol,
                                         model, credential_ref, input_message_ids, error_kind, error_detail, created_at)
                VALUES ('0190f000-0000-7000-8000-000000000004', 'answer', '{Conversation}', '{Answer}', 'failed', 'm',
                        'https://fake.example/v1', 'chat_completions', 'fake-model', 'c/k', '[]', 'unauthorized', '{Leaky}', 2);
            """);
    }
}
