// Child process for fault-injection tests: lets a test kill a real process at a known point.
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Credentials;
using Mindweave.Application.Models;
using Mindweave.Application.Storage;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

if (args.Length < 2 || (args[0] == "hang-before-commit" ? args.Length < 4 : args.Length > 2))
{
    Console.Error.WriteLine("usage: Mindweave.TestHost <hold-lock|hang-in-migration|hang-in-backup|commit-then-hang|stream-then-hang> <data-dir>");
    Console.Error.WriteLine("       Mindweave.TestHost hang-before-commit <data-dir> <operation> <id>...");
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

    case "hang-in-backup":
    {
        // Opening needs a migration, so it first writes the pre-migration backup with VACUUM INTO; once part of the
        // copy is on disk, the process blocks inside that statement so the test can kill it there.
        var catalog = new MigrationCatalog(
        [
            .. MigrationCatalog.Default.Migrations,
            Migration.Sql(MigrationCatalog.Default.LatestVersion + 1, "add note", "CREATE TABLE note (id TEXT PRIMARY KEY) STRICT;"),
        ]);
        await using var database = new SqliteDatabase(paths, catalog, TimeProvider.System, NullLogger<SqliteDatabase>.Instance)
        {
            BeforeBackupForTests = (connection, partial) => SQLitePCL.raw.sqlite3_progress_handler(
                connection.Handle,
                100,
                _ =>
                {
                    if (File.Exists(partial) && new FileInfo(partial).Length > 0)
                    {
                        Console.WriteLine("in-backup");
                        Thread.Sleep(Timeout.Infinite);
                    }

                    return 0;
                },
                null),
        };
        await database.OpenAsync(CancellationToken.None);
        return 4;
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
        var health = new StorageHealth(database, new StorageHealthOptions(), NullLogger<StorageHealth>.Instance);
        var generation = new GenerationCoordinator(store, client, credentials, new SqliteAiRequestStore(database), health, clock, new GenerationOptions { CheckpointInterval = TimeSpan.FromMilliseconds(50) }, NullLogger<GenerationCoordinator>.Instance);
        var models = new ModelSettingsService(new SqliteModelSettingsStore(database), store, credentials, client, clock, NullLogger<ModelSettingsService>.Instance);
        await models.SaveAsync(new ModelSettingsInput("fake", "https://fake.example/v1", "m", "sk"), CancellationToken.None);
        var workspace = new Workspace(WorkspaceId.New(), WorkspaceName.FromStored("w"), DateTimeOffset.UtcNow);
        await new SqliteWorkspaceStore(database).AddAsync(workspace, CancellationToken.None);
        var conversations = new ConversationService(store, new SqliteBranchStore(database), generation, health, models, clock, NullLogger<ConversationService>.Instance);
        var sent = await conversations.StartAsync(workspace.Id, "问", CancellationToken.None);
        while ((await store.GetMessagesAsync(sent.Conversation, CancellationToken.None))[1].Text.Length == 0)
        {
            await Task.Delay(20);
        }

        Console.WriteLine($"streaming {sent.Answer}");
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    case "hang-before-commit":
    {
        // One runtime write runs through its real store; once its statements have run, the process blocks before
        // COMMIT so the test can kill it inside the transaction. The page cache is kept tiny so a larger write spills
        // uncommitted pages into the WAL first, as a big transaction does once it outgrows the cache.
        await using var database = new SqliteDatabase(paths, MigrationCatalog.Default, TimeProvider.System, NullLogger<SqliteDatabase>.Instance);
        var opened = await database.OpenAsync(CancellationToken.None);
        if (!opened.IsReady)
        {
            Console.WriteLine($"not-ready {opened.Status}");
            return 3;
        }

        var baseline = await database.WriteAsync(
            (connection, transaction) =>
            {
                connection.Execute(transaction, "PRAGMA cache_size = 1;");
                return connection.Scalar<long>(transaction, "SELECT total_changes();");
            },
            CancellationToken.None);
        var wal = paths.DatabasePath + "-wal";
        var walBefore = File.Exists(wal) ? new FileInfo(wal).Length : 0;
        database.BeforeCommitForTests = (connection, transaction) =>
        {
            var changes = connection.Scalar<long>(transaction, "SELECT total_changes();") - baseline;
            var walNow = File.Exists(wal) ? new FileInfo(wal).Length : 0;
            Console.WriteLine($"in-transaction {changes} {walNow - walBefore}");
            Thread.Sleep(Timeout.Infinite);
        };

        await KillPoint.RunAsync(database, args[2], args[3..]);
        Console.WriteLine("committed");
        return 5;
    }

    default:
        Console.Error.WriteLine($"unknown command {args[0]}");
        return 64;
}

/// <summary>The runtime writes the transaction kill tests stop inside, each through the store the application uses.</summary>
internal static class KillPoint
{
    public static async Task RunAsync(SqliteDatabase database, string operation, string[] ids)
    {
        var at = DateTimeOffset.UtcNow;
        var none = CancellationToken.None;
        switch (operation)
        {
            case "branch":
                // parent, source answer, new child
                await new SqliteBranchStore(database).CreateAsync(
                    ConversationId.Parse(ids[0]), ConversationId.Parse(ids[2]), new BranchSelection(MessageId.Parse(ids[1]), 0, "q", "{\"v\":1}", 1, "test-profile"), at, none);
                break;
            case "adopt":
                await new SqliteSummaryStore(database).AdoptAsync(SummaryNodeId.Parse(ids[0]), SummaryVersionId.Parse(ids[1]), at, none);
                break;
            case "delete-versions":
                await new SqliteSummaryStore(database).DeleteVersionsAsync(SummaryNodeId.Parse(ids[0]), ids[1..].Select(SummaryVersionId.Parse).ToHashSet(), at, none);
                break;
            case "archive":
                await new SqliteArchiveStore(database).ArchiveAsync(ConversationId.Parse(ids[0]), at, none);
                break;
            case "restore":
                await new SqliteArchiveStore(database).RestoreAsync(ConversationId.Parse(ids[0]), none);
                break;
            case "delete":
                // The selected conversations: their scope as it is now, deleted in one transaction.
                var archive = new SqliteArchiveStore(database);
                await archive.DeleteAsync(await archive.InspectDeleteAsync([.. ids.Select(ConversationId.Parse)], none), none);
                break;
            case "delete-workspace":
                await new SqliteWorkspaceStore(database).DeleteAsync(WorkspaceId.Parse(ids[0]), none);
                break;
            case "layout":
                var workspace = WorkspaceId.Parse(ids[0]);
                await new SqliteNetworkStore(database).SaveLayoutAsync(workspace, Layout(await NodesAsync(database, workspace)), at, none);
                break;
            default:
                throw new ArgumentException($"unknown operation {operation}", nameof(operation));
        }
    }

    /// <summary>The i-th node by ID goes to (10 i, 20 i); TransactionKillTests expects exactly these.</summary>
    private static Dictionary<SummaryNodeId, NodePosition> Layout(IReadOnlyList<SummaryNodeId> nodes) =>
        nodes.Select((node, i) => (node, i)).ToDictionary(p => p.node, p => new NodePosition(p.i * 10, p.i * 20));

    private static Task<List<SummaryNodeId>> NodesAsync(SqliteDatabase database, WorkspaceId workspace) =>
        database.ReadAsync(
            connection =>
            {
                using var command = connection.Command(null, "SELECT id FROM summary_node WHERE workspace_id = $ws ORDER BY id;", ("$ws", workspace.ToString()));
                using var reader = command.ExecuteReader();
                var nodes = new List<SummaryNodeId>();
                while (reader.Read())
                {
                    nodes.Add(SummaryNodeId.Parse(reader.GetString(0)));
                }

                return nodes;
            },
            CancellationToken.None);
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
