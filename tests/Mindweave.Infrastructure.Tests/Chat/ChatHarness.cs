using Mindweave.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Reading;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>Real SQLite stores and the real coordinator and services, with a fake AI and in-memory credentials.</summary>
internal sealed class ChatHarness : IAsyncDisposable
{
    private readonly TempDataDirectory _dir;
    private readonly bool _ownsDirectory;

    private ChatHarness(TempDataDirectory dir, bool ownsDirectory, Func<IConversationStore, IConversationStore>? wrapStore, IChatClient? client, SqliteDatabaseOptions? databaseOptions)
    {
        ActiveClient = client ?? Client;
        _dir = dir;
        _ownsDirectory = ownsDirectory;
        Database = dir.Database(options: databaseOptions);
        Store = new SqliteConversationStore(Database);
        Settings = new SqliteModelSettingsStore(Database);
        Requests = new SqliteAiRequestStore(Database);
        Branches = new SqliteBranchStore(Database);
        ReadingStore = new SqliteReadingStateStore(Database);
        Summaries = new SqliteContextSummaryStore(Database);
        var storeOverride = wrapStore?.Invoke(Store);
        Health = new StorageHealth(Database, new StorageHealthOptions { RetryInterval = TimeSpan.FromMilliseconds(50) }, NullLogger<StorageHealth>.Instance);
        Generation = new GenerationCoordinator(
            storeOverride ?? Store,
            ActiveClient,
            Credentials,
            Requests,
            Health,
            new SystemClock(),
            new GenerationOptions { CheckpointInterval = TimeSpan.FromMilliseconds(50) },
            NullLogger<GenerationCoordinator>.Instance,
            new ContextCompressor(Summaries, ActiveClient, new SystemClock(), NullLogger<ContextCompressor>.Instance));
        Models = new ModelSettingsService(Settings, Store, Credentials, ActiveClient, new SystemClock(), NullLogger<ModelSettingsService>.Instance);
        Archive = new SqliteArchiveStore(Database);
        Conversations = new ConversationService(storeOverride ?? Store, Branches, Generation, Health, Models, new SystemClock(), NullLogger<ConversationService>.Instance, Summaries, Archive);
        Reading = new ReadingStateService(ReadingStore, new SystemClock(), NullLogger<ReadingStateService>.Instance, TimeSpan.FromMilliseconds(20), Health);
        SummaryNodes = new SqliteSummaryStore(Database);
        Drafts = new Application.Summaries.SummaryDrafts(SummaryNodes, new SystemClock(), Health, NullLogger<Application.Summaries.SummaryDrafts>.Instance, TimeSpan.FromMilliseconds(20));
        SummaryService = new Application.Summaries.SummaryService(
            SummaryNodes, storeOverride ?? Store, Branches, Generation, Health, Drafts, Models, new SystemClock(), NullLogger<Application.Summaries.SummaryService>.Instance, Summaries);
        Network = new Application.Network.NetworkService(SummaryNodes, new SqliteNetworkStore(Database), new SystemClock());
        Workspaces = new SqliteWorkspaceStore(Database);
        WorkspaceDeletion = new Application.Workspaces.WorkspaceDeletionService(
            Workspaces, Database, Health, Generation, NullLogger<Application.Workspaces.WorkspaceDeletionService>.Instance);
    }

    public SqliteDatabase Database { get; }

    public StorageHealth Health { get; }

    public SqliteConversationStore Store { get; }

    public SqliteModelSettingsStore Settings { get; }

    public SqliteAiRequestStore Requests { get; }

    public SqliteBranchStore Branches { get; }

    public SqliteArchiveStore Archive { get; }

    public SqliteReadingStateStore ReadingStore { get; }

    public SqliteContextSummaryStore Summaries { get; }

    public ReadingStateService Reading { get; }

    public MemoryCredentialStore Credentials { get; } = new();

    public FakeChatClient Client { get; } = new();

    /// <summary>The client the coordinator uses: the fake unless a real one was supplied.</summary>
    public IChatClient ActiveClient { get; }

    public GenerationCoordinator Generation { get; }

    public ModelSettingsService Models { get; }

    public ConversationService Conversations { get; }

    public SqliteSummaryStore SummaryNodes { get; }

    public Application.Summaries.SummaryService SummaryService { get; }

    public Application.Summaries.SummaryDrafts Drafts { get; }

    public Application.Network.NetworkService Network { get; }

    public WorkspaceId Workspace { get; private set; }

    public SqliteWorkspaceStore Workspaces { get; }

    public Application.Workspaces.WorkspaceDeletionService WorkspaceDeletion { get; }

    public string BackupDirectory => _dir.Paths.BackupDirectory;

    public string DataDirectory => _dir.Paths.DataDirectory;

    public string DatabasePath => _dir.Paths.DatabasePath;

    public static async Task<ChatHarness> StartAsync(
        bool configure = true,
        TempDataDirectory? directory = null,
        Func<IConversationStore, IConversationStore>? wrapStore = null,
        IChatClient? client = null,
        ModelSettingsInput? model = null,
        SqliteDatabaseOptions? databaseOptions = null)
    {
        var dir = directory ?? new TempDataDirectory();
        var harness = new ChatHarness(dir, directory is null, wrapStore, client, databaseOptions);
        Assert.True((await harness.Database.OpenAsync(CancellationToken.None)).IsReady);
        var workspace = new Workspace(WorkspaceId.New(), WorkspaceName.FromStored("测试"), DateTimeOffset.UtcNow);
        await new SqliteWorkspaceStore(harness.Database).AddAsync(workspace, CancellationToken.None);
        harness.Workspace = workspace.Id;
        if (configure)
        {
            await harness.Models.SaveAsync(model ?? new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test"), CancellationToken.None);
        }

        return harness;
    }

    public async Task<Message> WaitForAnswerAsync(MessageId answer, Func<Message, bool>? until = null, int timeoutMs = 5000)
    {
        until ??= m => m.IsFinished;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var message = await GetMessageAsync(answer);
            if (message is not null && until(message))
            {
                return message;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Answer {answer} is {message?.State}: '{message?.Text}'");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Waits until the conversation's automatic title is stored and every started task's request record is written
    /// (records follow their task's finish, TRACK.md findings), so later row counts are not racing background work.
    /// </summary>
    public async Task SettleAsync(ConversationId conversation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var title = (await Store.GetAsync(conversation, CancellationToken.None))?.Title;
            var (started, traced) = await Database.ReadAsync(
                c => (c.Scalar<long>(null, "SELECT count(*) FROM generation_task WHERE started_at IS NOT NULL;"), c.Scalar<long>(null, "SELECT count(*) FROM ai_request;")),
                CancellationToken.None);
            if (title is not null && Generation.UnfinishedCount == 0 && traced >= started)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Not settled: title {title}, {Generation.UnfinishedCount} unfinished, {traced}/{started} traced.");
            }

            await Task.Delay(20);
        }
    }

    public async Task<Message?> GetMessageAsync(MessageId id)
    {
        var all = await Database.ReadAsync(
            c =>
            {
                using var cmd = c.Command(null, "SELECT conversation_id FROM message WHERE id = $id;", ("$id", id.ToString()));
                return cmd.ExecuteScalar() as string;
            },
            CancellationToken.None);
        return all is null ? null : (await Store.GetMessagesAsync(ConversationId.Parse(all), CancellationToken.None)).Single(m => m.Id == id);
    }

    public async Task<string?> TaskStateAsync(MessageId answer) =>
        await Database.ReadAsync(
            c =>
            {
                using var cmd = c.Command(null, "SELECT state FROM generation_task WHERE target_message_id = $id;", ("$id", answer.ToString()));
                return cmd.ExecuteScalar() as string;
            },
            CancellationToken.None);

    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not reached.");
            }

            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Generation.DisposeAsync();
        await Health.StopAsync();
        await Reading.FlushAsync();
        Reading.Dispose();
        await Drafts.FlushAsync();
        await Database.DisposeAsync();
        await Health.DisposeAsync();
        Drafts.Dispose();
        if (_ownsDirectory)
        {
            _dir.Dispose();
        }
    }
}
