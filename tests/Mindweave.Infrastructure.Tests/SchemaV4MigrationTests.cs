using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application;
using Mindweave.Application.Ai;
using Mindweave.Application.Credentials;
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

        // The application's own services over the upgraded file, with no network and no keychain.
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IChatClient>(new FakeChatClient());
        services.AddSingleton<ICredentialStore>(new MemoryCredentialStore());
        services.AddSingleton<Application.Share.IShareMarkdown>(new PlainMarkdown());
        services.AddMindweaveInfrastructure(_dir.Paths);
        services.AddMindweaveApplication();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var database = provider.GetRequiredService<SqliteDatabase>();
        Assert.True((await database.OpenAsync(Ct)).IsReady);
        var conversations = provider.GetRequiredService<ConversationService>();

        var branch = await conversations.CreateBranchAsync(
            ConversationId.Parse(Conversation), new BranchSelection(MessageId.Parse(Answer), 0, "蒸汽机", "{}", 1, "p"), Ct);

        Assert.True(branch.Accepted);
        var reading = new SqliteReadingStateStore(database);
        await reading.SavePositionAsync(ConversationId.Parse(Conversation), ViewerSlotKey.Primary, new ReadingPosition(MessageId.Parse(Answer), 0, null, 1), DateTimeOffset.UtcNow, Ct);
        Assert.NotNull(await reading.GetPositionAsync(ConversationId.Parse(Conversation), ViewerSlotKey.Primary, Ct));
    }
}
