// Child process for fault-injection tests: lets a test kill a real process at a known point.
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Credentials;
using Mindweave.Application.Models;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: Mindweave.TestHost <hold-lock|hang-in-migration|commit-then-hang|stream-then-hang> <data-dir>");
    return 64;
}

var paths = new AppPaths("test", args[1]);
switch (args[0])
{
    case "hold-lock":
    {
        await using var instance = SingleInstance.TryAcquire(paths);
        if (instance is null)
        {
            Console.WriteLine("busy");
            return 1;
        }

        instance.StartListening(() => Console.WriteLine("activated"));
        Console.WriteLine("ready");
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    case "hang-in-migration":
    {
        // Schema 2 inserts a row, then blocks forever inside the still-open upgrade transaction.
        var catalog = new MigrationCatalog(
        [
            .. MigrationCatalog.Default.Migrations,
            new Migration(MigrationCatalog.Default.LatestVersion + 1, "hang", (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO app_state (key, value) VALUES ('half-migrated', 'yes');";
                command.ExecuteNonQuery();
                Console.WriteLine("in-migration");
                Thread.Sleep(Timeout.Infinite);
            }),
        ]);
        await using var database = new SqliteDatabase(paths, catalog, TimeProvider.System, NullLogger<SqliteDatabase>.Instance);
        await database.OpenAsync(CancellationToken.None);
        return 2;
    }

    case "commit-then-hang":
    {
        var database = new SqliteDatabase(paths, MigrationCatalog.Default, TimeProvider.System, NullLogger<SqliteDatabase>.Instance);
        var result = await database.OpenAsync(CancellationToken.None);
        if (!result.IsReady)
        {
            Console.WriteLine($"not-ready {result.Status}");
            return 3;
        }

        await database.WriteAsync(
            (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO app_state (key, value) VALUES ('committed', 'yes');";
                return command.ExecuteNonQuery();
            },
            CancellationToken.None);
        Console.WriteLine("committed");
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    case "stream-then-hang":
    {
        // A real coordinator streams an answer until its partial text is checkpointed, then the process hangs
        // mid-answer so the test can kill it.
        var database = new SqliteDatabase(paths, MigrationCatalog.Default, TimeProvider.System, NullLogger<SqliteDatabase>.Instance);
        await database.OpenAsync(CancellationToken.None);
        var store = new SqliteConversationStore(database);
        var credentials = new OneKey();
        var client = new EndlessClient();
        var clock = new SystemClock();
        var generation = new GenerationCoordinator(store, client, credentials, clock, new GenerationOptions { CheckpointInterval = TimeSpan.FromMilliseconds(50) }, NullLogger<GenerationCoordinator>.Instance);
        var models = new ModelSettingsService(new SqliteModelSettingsStore(database), store, credentials, client, clock, NullLogger<ModelSettingsService>.Instance);
        await models.SaveAsync(new ModelSettingsInput("fake", "https://fake.example/v1", "m", "sk"), CancellationToken.None);
        var workspace = new Workspace(WorkspaceId.New(), WorkspaceName.FromStored("w"), DateTimeOffset.UtcNow);
        await new SqliteWorkspaceStore(database).AddAsync(workspace, CancellationToken.None);
        var conversations = new ConversationService(store, generation, models, clock, NullLogger<ConversationService>.Instance);
        var sent = await conversations.StartAsync(workspace.Id, "问", CancellationToken.None);
        while ((await store.GetMessagesAsync(sent.Conversation, CancellationToken.None))[1].Body.Length == 0)
        {
            await Task.Delay(20);
        }

        Console.WriteLine($"streaming {sent.Answer}");
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    default:
        Console.Error.WriteLine($"unknown command {args[0]}");
        return 64;
}

internal sealed class OneKey : ICredentialStore
{
    public Task<string?> ReadAsync(string reference, CancellationToken cancellationToken) => Task.FromResult<string?>("sk");

    public Task WriteAsync(string reference, string secret, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteAsync(string reference, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Emits a piece of text every 20 ms and never finishes.</summary>
internal sealed class EndlessClient : IChatClient
{
    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(20, cancellationToken);
            yield return new ChatStreamEvent.TextDelta("字");
        }
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
