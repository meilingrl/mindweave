using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Testing;
using Mindweave.Application;
using Mindweave.Application.Ai;
using Mindweave.Application.Credentials;
using Mindweave.Application.Diagnostics;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Reading;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>Real SQLite stores and the real coordinator and services, with a fake AI and in-memory credentials.</summary>
internal sealed class ChatHarness : IAsyncDisposable
{
    private readonly TempDataDirectory _dir;
    private readonly bool _ownsDirectory;

    // Not disposed itself: DisposeAsync tears the services down in the order the stores need (flushes before the database).
    private readonly ServiceProvider _services;

    private ChatHarness(TempDataDirectory dir, bool ownsDirectory, Func<IConversationStore, IConversationStore>? wrapStore, IChatClient? client, SqliteDatabaseOptions? databaseOptions, int? slots)
    {
        ActiveClient = client ?? Client;
        _dir = dir;
        _ownsDirectory = ownsDirectory;
        _services = BuildServices(dir.Paths, ActiveClient, Credentials, wrapStore, databaseOptions, slots);
        Database = _services.GetRequiredService<SqliteDatabase>();
        Store = _services.GetRequiredService<SqliteConversationStore>();
        Settings = (SqliteModelSettingsStore)_services.GetRequiredService<IModelSettingsStore>();
        Requests = (SqliteAiRequestStore)_services.GetRequiredService<IAiRequestStore>();
        Branches = (SqliteBranchStore)_services.GetRequiredService<IBranchStore>();
        ReadingStore = (SqliteReadingStateStore)_services.GetRequiredService<IReadingStateStore>();
        Summaries = (SqliteContextSummaryStore)_services.GetRequiredService<IContextSummaryStore>();
        Health = _services.GetRequiredService<StorageHealth>();
        Generation = _services.GetRequiredService<GenerationCoordinator>();
        Models = _services.GetRequiredService<ModelSettingsService>();
        Archive = (SqliteArchiveStore)_services.GetRequiredService<IConversationArchiveStore>();
        Conversations = _services.GetRequiredService<ConversationService>();
        Reading = _services.GetRequiredService<ReadingStateService>();
        SummaryNodes = (SqliteSummaryStore)_services.GetRequiredService<ISummaryStore>();
        Drafts = _services.GetRequiredService<Application.Summaries.SummaryDrafts>();
        SummaryService = _services.GetRequiredService<Application.Summaries.SummaryService>();
        Network = _services.GetRequiredService<Application.Network.NetworkService>();
        Workspaces = (SqliteWorkspaceStore)_services.GetRequiredService<IWorkspaceStore>();
        WorkspaceDeletion = _services.GetRequiredService<Application.Workspaces.WorkspaceDeletionService>();
        ConversationDeletion = _services.GetRequiredService<ConversationDeletionService>();
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

    /// <summary>Creating and renaming workspaces, as the shell uses them.</summary>
    public Application.Workspaces.WorkspaceService WorkspaceService => _services.GetRequiredService<Application.Workspaces.WorkspaceService>();

    public ConversationDeletionService ConversationDeletion { get; }

    /// <summary>Names suggested for a workspace while renaming it (2026-11-11).</summary>
    public Application.Workspaces.WorkspaceNameSuggester NameSuggester => _services.GetRequiredService<Application.Workspaces.WorkspaceNameSuggester>();

    public string BackupDirectory => _dir.Paths.BackupDirectory;

    public string DataDirectory => _dir.Paths.DataDirectory;

    public string DatabasePath => _dir.Paths.DatabasePath;

    public static async Task<ChatHarness> StartAsync(
        bool configure = true,
        TempDataDirectory? directory = null,
        Func<IConversationStore, IConversationStore>? wrapStore = null,
        IChatClient? client = null,
        ModelSettingsInput? model = null,
        SqliteDatabaseOptions? databaseOptions = null,
        int? slots = null)
    {
        var dir = directory ?? new TempDataDirectory();
        var harness = new ChatHarness(dir, directory is null, wrapStore, client, databaseOptions, slots);
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

    /// <summary>Deletes the scope of <paramref name="selected"/> as it is now, the way the confirmation does: inspect, then delete that.</summary>
    public async Task<ConversationDeleteOutcome> DeleteAsync(params ConversationId[] selected) =>
        await ConversationDeletion.DeleteAsync(await ConversationDeletion.InspectAsync(selected, CancellationToken.None), CancellationToken.None);

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

    /// <summary>Polls <paramref name="condition"/> until it holds; fails after a timeout naming the condition and its caller.</summary>
    public static async Task UntilAsync(
        Func<bool> condition,
        int timeoutMs = 5000,
        [CallerArgumentExpression(nameof(condition))] string? expression = null,
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int line = 0)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw NotReached(timeoutMs, expression, file, line);
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Like <see cref="UntilAsync(Func{bool}, int, string?, string?, int)"/>, for a condition that reads storage asynchronously.</summary>
    public static async Task UntilAsync(
        Func<Task<bool>> condition,
        int timeoutMs = 5000,
        [CallerArgumentExpression(nameof(condition))] string? expression = null,
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int line = 0)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw NotReached(timeoutMs, expression, file, line);
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Checks <paramref name="condition"/> repeatedly for <paramref name="durationMs"/> and fails as soon as it breaks:
    /// a "nothing happens" check, in place of a fixed delay followed by one assertion.
    /// </summary>
    public static async Task StaysAsync(
        Func<bool> condition,
        int durationMs,
        [CallerArgumentExpression(nameof(condition))] string? expression = null,
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int line = 0)
    {
        var until = DateTime.UtcNow.AddMilliseconds(durationMs);
        do
        {
            Assert.True(condition(), $"Condition broke within {durationMs} ms: {expression} ({Path.GetFileName(file)}:{line}).");
            await Task.Delay(10);
        }
        while (DateTime.UtcNow < until);
        Assert.True(condition(), $"Condition broke within {durationMs} ms: {expression} ({Path.GetFileName(file)}:{line}).");
    }

    /// <summary>Like <see cref="StaysAsync(Func{bool}, int, string?, string?, int)"/>, for a condition that reads storage asynchronously.</summary>
    public static async Task StaysAsync(
        Func<Task<bool>> condition,
        int durationMs,
        [CallerArgumentExpression(nameof(condition))] string? expression = null,
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int line = 0)
    {
        var until = DateTime.UtcNow.AddMilliseconds(durationMs);
        do
        {
            Assert.True(await condition(), $"Condition broke within {durationMs} ms: {expression} ({Path.GetFileName(file)}:{line}).");
            await Task.Delay(10);
        }
        while (DateTime.UtcNow < until);
        Assert.True(await condition(), $"Condition broke within {durationMs} ms: {expression} ({Path.GetFileName(file)}:{line}).");
    }

    private static TimeoutException NotReached(int timeoutMs, string? expression, string? file, int line) =>
        new($"Condition not reached within {timeoutMs} ms: {expression} ({Path.GetFileName(file)}:{line}).");

    /// <summary>
    /// The app's own wiring (<see cref="InfrastructureServices.AddMindweaveInfrastructure"/> and
    /// <see cref="ApplicationServices.AddMindweaveApplication"/>) with the test doubles and short timings in place.
    /// </summary>
    private static ServiceProvider BuildServices(
        AppPaths paths,
        IChatClient client,
        ICredentialStore credentials,
        Func<IConversationStore, IConversationStore>? wrapStore,
        SqliteDatabaseOptions? databaseOptions,
        int? slots)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // Added first: the app's defaults for these are only registered where nothing is yet.
        services.AddSingleton(client);
        services.AddSingleton(credentials);
        services.AddSingleton(new StorageHealthOptions { RetryInterval = TimeSpan.FromMilliseconds(50) });
        services.AddSingleton(slots is { } count
            ? new GenerationOptions { CheckpointInterval = TimeSpan.FromMilliseconds(50), Slots = count }
            : new GenerationOptions { CheckpointInterval = TimeSpan.FromMilliseconds(50) });
        if (databaseOptions is not null)
        {
            services.AddSingleton(databaseOptions);
        }

        services.AddMindweaveInfrastructure(paths);
        services.AddMindweaveApplication();

        // Replaced after: the services write through the (possibly wrapped) store, while Store stays the real one; reading
        // positions and summary drafts save after a short delay.
        services.AddSingleton<SqliteConversationStore>();
        services.Replace(ServiceDescriptor.Singleton<IConversationStore>(sp =>
        {
            var store = sp.GetRequiredService<SqliteConversationStore>();
            return wrapStore?.Invoke(store) ?? store;
        }));
        services.Replace(ServiceDescriptor.Singleton(sp => new ReadingStateService(
            sp.GetRequiredService<IReadingStateStore>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<ReadingStateService>>(),
            TimeSpan.FromMilliseconds(20),
            sp.GetRequiredService<StorageHealth>())));
        services.Replace(ServiceDescriptor.Singleton(sp => new Application.Summaries.SummaryDrafts(
            sp.GetRequiredService<ISummaryStore>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<StorageHealth>(),
            sp.GetRequiredService<ILogger<Application.Summaries.SummaryDrafts>>(),
            TimeSpan.FromMilliseconds(20))));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
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
