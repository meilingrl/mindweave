using Mindweave.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
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

    private ChatHarness(TempDataDirectory dir, bool ownsDirectory, Func<IConversationStore, IConversationStore>? wrapStore, IChatClient? client)
    {
        ActiveClient = client ?? Client;
        _dir = dir;
        _ownsDirectory = ownsDirectory;
        Database = dir.Database();
        Store = new SqliteConversationStore(Database);
        Settings = new SqliteModelSettingsStore(Database);
        Requests = new SqliteAiRequestStore(Database);
        var storeOverride = wrapStore?.Invoke(Store);
        Generation = new GenerationCoordinator(
            storeOverride ?? Store,
            ActiveClient,
            Credentials,
            Requests,
            new SystemClock(),
            new GenerationOptions { CheckpointInterval = TimeSpan.FromMilliseconds(50), SaveRetryInterval = TimeSpan.FromMilliseconds(50) },
            NullLogger<GenerationCoordinator>.Instance);
        Models = new ModelSettingsService(Settings, Store, Credentials, ActiveClient, new SystemClock(), NullLogger<ModelSettingsService>.Instance);
        Conversations = new ConversationService(storeOverride ?? Store, Generation, Models, new SystemClock(), NullLogger<ConversationService>.Instance);
    }

    public SqliteDatabase Database { get; }

    public SqliteConversationStore Store { get; }

    public SqliteModelSettingsStore Settings { get; }

    public SqliteAiRequestStore Requests { get; }

    public MemoryCredentialStore Credentials { get; } = new();

    public FakeChatClient Client { get; } = new();

    /// <summary>The client the coordinator uses: the fake unless a real one was supplied.</summary>
    public IChatClient ActiveClient { get; }

    public GenerationCoordinator Generation { get; }

    public ModelSettingsService Models { get; }

    public ConversationService Conversations { get; }

    public WorkspaceId Workspace { get; private set; }

    public string DataDirectory => _dir.Paths.DataDirectory;

    public static async Task<ChatHarness> StartAsync(
        bool configure = true,
        TempDataDirectory? directory = null,
        Func<IConversationStore, IConversationStore>? wrapStore = null,
        IChatClient? client = null,
        ModelSettingsInput? model = null)
    {
        var dir = directory ?? new TempDataDirectory();
        var harness = new ChatHarness(dir, directory is null, wrapStore, client);
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
        await Database.DisposeAsync();
        if (_ownsDirectory)
        {
            _dir.Dispose();
        }
    }
}
