using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Reading;
using Mindweave.Application.Storage;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Reading;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests;

/// <summary>Upgrading real version-3 data: existing answers can be branched from and read positions stored.</summary>
public sealed class SchemaV4MigrationTests : IDisposable
{
    private const string Conversation = "0190f000-0000-7000-8000-000000000001";
    private const string Question = "0190f000-0000-7000-8000-000000000002";
    private const string Answer = "0190f000-0000-7000-8000-000000000003";

    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Version_3_answers_can_be_branched_from_after_the_upgrade()
    {
        await using (var old = _dir.Database(new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version <= 3))))
        {
            Assert.True((await old.OpenAsync(Ct)).IsReady);
        }

        using (var raw = _dir.Raw())
        {
            SqliteConnections.Execute(raw, $"""
                INSERT INTO workspace VALUES ('0190f000-0000-7000-8000-0000000000aa', '测试', 1);
                INSERT INTO conversation VALUES ('{Conversation}', '0190f000-0000-7000-8000-0000000000aa', NULL, 'none', 1, 1);
                INSERT INTO message (id, conversation_id, seq, role, state, created_at, completed_at)
                    VALUES ('{Question}', '{Conversation}', 0, 'user', 'complete', 1, 1),
                           ('{Answer}', '{Conversation}', 1, 'assistant', 'complete', 2, 3);
                INSERT INTO message_block (message_id, ordinal, kind, text)
                    VALUES ('{Question}', 0, 'text', '火车？'), ('{Answer}', 0, 'text', '蒸汽机车');
                """);
        }

        await using var database = _dir.Database();
        Assert.True((await database.OpenAsync(Ct)).IsReady);
        var store = new SqliteConversationStore(database);
        var branches = new SqliteBranchStore(database);
        var client = new FakeChatClient();
        var credentials = new MemoryCredentialStore();
        await using var health = new StorageHealth(database, new StorageHealthOptions(), NullLogger<StorageHealth>.Instance);
        await using var generation = new GenerationCoordinator(store, client, credentials, new SqliteAiRequestStore(database), health, new SystemClock(), new GenerationOptions(), NullLogger<GenerationCoordinator>.Instance);
        var models = new ModelSettingsService(new SqliteModelSettingsStore(database), store, credentials, client, new SystemClock(), NullLogger<ModelSettingsService>.Instance);
        var conversations = new ConversationService(store, branches, generation, health, models, new SystemClock(), NullLogger<ConversationService>.Instance);

        var branch = await conversations.CreateBranchAsync(
            ConversationId.Parse(Conversation), new BranchSelection(MessageId.Parse(Answer), 0, "蒸汽机", "{}", 1, "p"), Ct);

        Assert.True(branch.Accepted);
        var reading = new SqliteReadingStateStore(database);
        await reading.SavePositionAsync(ConversationId.Parse(Conversation), ViewerSlotKey.Primary, new ReadingPosition(MessageId.Parse(Answer), 0, null, 1), DateTimeOffset.UtcNow, Ct);
        Assert.NotNull(await reading.GetPositionAsync(ConversationId.Parse(Conversation), ViewerSlotKey.Primary, Ct));
    }
}
