using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Reading;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;

namespace Mindweave.Desktop.Tests;

/// <summary>How the exit is asked for: closing the window, or the application lifetime (⌘Q, the dock's "quit").</summary>
public enum ExitEntry
{
    Window,
    Lifetime,
}

/// <summary>
/// The exit sequence (architecture §12.3, requirement §9.2–9.3) driven through both entry points: every cancelable
/// prompt before anything irreversible, the layout first, a bounded stop, and saving before the window closes, with
/// retry or return when saving fails.
/// </summary>
public class ExitTests
{
    [AvaloniaTheory]
    [InlineData(ExitEntry.Window)]
    [InlineData(ExitEntry.Lifetime)]
    public async Task An_unsaved_layout_alone_is_asked_about_and_dont_save_discards_only_once_the_exit_commits(ExitEntry entry)
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        var network = await DirtyLayoutAsync(app, window);

        Assert.True(Quit(app, window, entry));
        Assert.True(app.Root.LayoutPrompt.IsOpen);
        Assert.Equal(ExitStage.LayoutPrompt, app.Root.Exit.Stage);
        Assert.False(app.Root.Exit.IsTaskPromptOpen);

        app.Root.LayoutPrompt.DontSaveCommand.Execute(null);
        await app.Root.Exit.Running;

        Assert.Equal(1, app.Shell.ExitRequests);
        Assert.False(network.IsDirty);

        // The coordinator's own shutdown passes through either hook.
        Assert.False(Quit(app, window, entry));
    }

    [AvaloniaTheory]
    [InlineData(ExitEntry.Window)]
    [InlineData(ExitEntry.Lifetime)]
    public async Task A_running_answer_alone_is_asked_about_and_stopping_keeps_what_arrived(ExitEntry entry)
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var answer = await RunningAnswerAsync(app);

        Assert.True(Quit(app, window, entry));
        Assert.False(app.Root.LayoutPrompt.IsOpen);
        Assert.True(app.Root.Exit.IsTaskPromptOpen);
        Assert.Equal(UiText.ExitWithUnfinished(1, 0), app.Root.Exit.TaskMessage);

        app.Root.Exit.StopAndExitCommand.Execute(null);
        await app.Root.Exit.Running;

        Assert.Equal(1, app.Shell.ExitRequests);
        var stored = await StoredAsync(app, answer);
        Assert.Equal(MessageState.Stopped, stored.State);
        Assert.Equal("进行中", stored.Text);
        Assert.False(Quit(app, window, entry));
    }

    [AvaloniaTheory]
    [InlineData(ExitEntry.Window)]
    [InlineData(ExitEntry.Lifetime)]
    public async Task With_both_the_layout_is_asked_first_and_cancelling_the_task_prompt_keeps_it_unsaved(ExitEntry entry)
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        var network = await DirtyLayoutAsync(app, window);
        var answer = await RunningAnswerAsync(app);

        Assert.True(Quit(app, window, entry));
        Assert.True(app.Root.LayoutPrompt.IsOpen);
        Assert.False(app.Root.Exit.IsTaskPromptOpen);

        // "Don't save" is only recorded: the task prompt that follows can still be cancelled.
        app.Root.LayoutPrompt.DontSaveCommand.Execute(null);
        Assert.True(app.Root.Exit.IsTaskPromptOpen);
        Assert.True(network.IsDirty);

        app.Root.Exit.KeepRunningCommand.Execute(null);
        Assert.Equal(ExitStage.Idle, app.Root.Exit.Stage);
        Assert.True(network.IsDirty);
        Assert.Equal(0, app.Shell.ExitRequests);
        Assert.Equal(1, app.Services.GetRequiredService<GenerationCoordinator>().UnfinishedUserWork.Answers);

        // Asking again starts over with the layout, which is still unsaved.
        Assert.True(Quit(app, window, entry));
        Assert.True(app.Root.LayoutPrompt.IsOpen);
        app.Root.LayoutPrompt.DontSaveCommand.Execute(null);
        app.Root.Exit.StopAndExitCommand.Execute(null);
        await app.Root.Exit.Running;

        Assert.Equal(1, app.Shell.ExitRequests);
        Assert.False(network.IsDirty);
        Assert.Equal(MessageState.Stopped, (await StoredAsync(app, answer)).State);
    }

    [AvaloniaFact]
    public async Task The_task_prompt_names_answers_and_summaries_separately()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var first = await conversations.StartAsync(app.Workbench.CurrentWorkspace!.Id, "火车是怎么发明的？", CancellationToken.None);
        await UiWait.UntilAsync(() => conversations.LoadAsync(first.Conversation, CancellationToken.None).Result!.Messages.All(m => m.IsFinished));
        app.Client.KnowledgeGate = new TaskCompletionSource();
        Assert.True((await app.Services.GetRequiredService<Application.Summaries.SummaryService>().SummarizeAsync(first.Conversation, CancellationToken.None)).Accepted);
        await RunningAnswerAsync(app);

        Assert.True(app.Root.Exit.InterceptClose());

        Assert.Contains("1 个回答、1 个总结", app.Root.Exit.TaskMessage, StringComparison.Ordinal);
        app.Root.Exit.KeepRunningCommand.Execute(null);
        app.Client.KnowledgeGate.TrySetResult();
    }

    [AvaloniaFact]
    public async Task Drafts_typed_just_before_quitting_are_saved_before_the_window_closes()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        var page = await ConversationAsync(app);
        await page.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.HasSummary && page.RunningSummary is null);

        // A message draft (saved 0.5 s after typing) and a summary edit draft (1 s throttle), both still pending.
        page.Composer.Text = "还没发出去的问题";
        page.OpenSummaryCommand.Execute(null);
        var summary = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await summary.Loaded;
        await summary.BeginEditCommand.ExecuteAsync(null);
        summary.EditBody = "我自己的笔记";

        window.Close();
        await app.Root.Exit.Running;

        Assert.Equal(1, app.Shell.ExitRequests);
        var reading = app.Services.GetRequiredService<IReadingStateStore>();
        Assert.Equal("还没发出去的问题", await reading.GetDraftAsync(page.Id, ViewerSlotKey.Primary, CancellationToken.None));
        var detail = await app.Services.GetRequiredService<Application.Summaries.SummaryService>().GetAsync(summary.Node, CancellationToken.None);
        Assert.Equal("我自己的笔记", detail!.Draft?.Body);
    }

    [AvaloniaFact]
    public async Task A_failed_save_keeps_the_window_open_and_retrying_exits_once_saving_works()
    {
        var failing = new FailingDrafts();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<IReadingStateStore>(sp => failing.Wrap(sp.GetRequiredService<SqliteDatabase>())));
        var window = NetworkUiTests.Show(app);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        var page = await ConversationAsync(app);
        failing.Fail = true;
        page.Composer.Text = "还没发出去的问题";

        window.Close();
        await app.Root.Exit.Running;

        Assert.Equal(ExitStage.Failed, app.Root.Exit.Stage);
        Assert.True(app.Root.Exit.IsFailed);
        Assert.Equal(0, app.Shell.ExitRequests);
        Assert.False(closed);
        Assert.Equal("还没发出去的问题", page.Composer.Text);

        // Asking to quit again while the failure is shown changes nothing.
        window.Close();
        Assert.True(Quit(app, window, ExitEntry.Lifetime));
        Assert.Equal(ExitStage.Failed, app.Root.Exit.Stage);
        Assert.False(closed);

        failing.Fail = false;
        app.Root.Exit.RetryCommand.Execute(null);
        await app.Root.Exit.Running;

        Assert.Equal(1, app.Shell.ExitRequests);
        Assert.Equal("还没发出去的问题", await app.Services.GetRequiredService<IReadingStateStore>().GetDraftAsync(page.Id, ViewerSlotKey.Primary, CancellationToken.None));
        window.Close();
        Assert.True(closed);
    }

    [AvaloniaFact]
    public async Task Returning_after_a_failed_save_accepts_new_questions_and_does_not_revive_stopped_answers()
    {
        var failing = new FailingDrafts();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<IReadingStateStore>(sp => failing.Wrap(sp.GetRequiredService<SqliteDatabase>())));
        var page = await ConversationAsync(app);
        var answer = await RunningAnswerAsync(app);
        failing.Fail = true;
        page.Composer.Text = "草稿";

        Assert.True(app.Root.Exit.InterceptClose());
        app.Root.Exit.StopAndExitCommand.Execute(null);
        await app.Root.Exit.Running;
        Assert.True(app.Root.Exit.IsFailed);
        Assert.Equal(MessageState.Stopped, (await StoredAsync(app, answer)).State);

        failing.Fail = false;
        app.Root.Exit.ReturnCommand.Execute(null);

        Assert.Equal(ExitStage.Idle, app.Root.Exit.Stage);
        Assert.Equal(0, app.Shell.ExitRequests);
        var generation = app.Services.GetRequiredService<GenerationCoordinator>();
        Assert.False(generation.IsClosed);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var fresh = await conversations.StartAsync(app.Workbench.CurrentWorkspace!.Id, "新的问题", CancellationToken.None);
        await UiWait.UntilAsync(() => conversations.LoadAsync(fresh.Conversation, CancellationToken.None).Result!.Messages.All(m => m.IsFinished));
        Assert.Equal(MessageState.Complete, (await StoredAsync(app, fresh.Answer!.Value)).State);
        Assert.Equal(MessageState.Stopped, (await StoredAsync(app, answer)).State);
    }

    [AvaloniaFact]
    public async Task The_stop_step_waits_a_bounded_time_for_a_task_that_does_not_acknowledge()
    {
        var stubborn = new StubbornClient();
        await using var app = DesktopHarness.Create(s =>
        {
            s.AddSingleton<IChatClient>(stubborn);
            s.AddSingleton(new ExitOptions { StopWait = TimeSpan.FromMilliseconds(300) });
        });
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        await app.Services.GetRequiredService<ConversationService>().StartAsync(app.Workbench.CurrentWorkspace!.Id, "问", CancellationToken.None);
        await stubborn.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(app.Root.Exit.InterceptClose());
        app.Root.Exit.StopAndExitCommand.Execute(null);
        await app.Root.Exit.Running.WaitAsync(TimeSpan.FromSeconds(4));

        Assert.Equal(1, app.Shell.ExitRequests);
        stubborn.Release.TrySetResult();
    }

    [Fact]
    public async Task The_host_releases_the_single_instance_lock_even_when_disposal_fails()
    {
        var paths = new AppPaths("test", Path.Combine(Path.GetTempPath(), "mindweave-desktop-tests", Guid.NewGuid().ToString("N")));
        var instance = SingleInstance.TryAcquire(paths);
        Assert.NotNull(instance);
        var services = CompositionRoot.Build(paths, new DesktopHarness.RecordingShell(), NullLoggerProvider.Instance, s =>
        {
            s.AddSingleton<IChatClient>(_ => new FailingDisposal());
            s.AddSingleton<Application.Credentials.ICredentialStore>(new MemoryCredentialStore());
        });
        var host = new AppHost(instance, services);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.DisposeAsync().AsTask());

        await using var again = SingleInstance.TryAcquire(paths);
        Assert.NotNull(again);
    }

    /// <summary>Asks to quit through <paramref name="entry"/>; true when the request was held (cancelled) for the exit sequence.</summary>
    private static bool Quit(DesktopHarness app, MainWindow window, ExitEntry entry)
    {
        if (entry == ExitEntry.Window)
        {
            var closed = false;
            void OnClosed(object? sender, EventArgs e) => closed = true;
            window.Closed += OnClosed;
            window.Close();
            window.Closed -= OnClosed;
            return !closed;
        }

        var request = new ShutdownRequestedEventArgs();
        App.OnShutdownRequested(app.Root, request);
        return request.Cancel;
    }

    private static async Task<NetworkHomeViewModel> DirtyLayoutAsync(DesktopHarness app, MainWindow window)
    {
        await NetworkUiTests.SeedAsync(app);
        var network = await NetworkUiTests.OpenNetworkAsync(app, window);
        await NetworkUiTests.MoveSomethingAsync(app, network);
        network.ArrangeCommand.Execute(null);
        Assert.True(network.IsDirty);
        return network;
    }

    /// <summary>Starts an answer in a new conversation that streams "进行中" and then waits.</summary>
    private static async Task<MessageId> RunningAnswerAsync(DesktopHarness app)
    {
        var held = app.Client.Hold("进行中");
        var sent = await app.Services.GetRequiredService<ConversationService>().StartAsync(app.Workbench.CurrentWorkspace!.Id, "问", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return sent.Answer!.Value;
    }

    private static async Task<ConversationViewModel> ConversationAsync(DesktopHarness app)
    {
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = "火车是怎么发明的？";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].CanBranch);
        return page;
    }

    private static async Task<Message> StoredAsync(DesktopHarness app, MessageId answer)
    {
        var store = app.Services.GetRequiredService<IConversationStore>();
        var conversations = await store.ListAsync(app.Workbench.CurrentWorkspace!.Id, CancellationToken.None);
        foreach (var conversation in conversations)
        {
            if ((await store.GetMessagesAsync(conversation.Conversation.Id, CancellationToken.None)).FirstOrDefault(m => m.Id == answer) is { } found)
            {
                return found;
            }
        }

        throw new InvalidOperationException($"Answer {answer} not found.");
    }

    /// <summary>The real reading-state store, whose draft writes fail while <see cref="Fail"/> is set.</summary>
    private sealed class FailingDrafts
    {
        public volatile bool Fail;

        public IReadingStateStore Wrap(SqliteDatabase database) => new Store(new SqliteReadingStateStore(database), this);

        private sealed class Store(IReadingStateStore inner, FailingDrafts gate) : IReadingStateStore
        {
            public Task<ReadingPosition?> GetPositionAsync(ConversationId conversation, ViewerSlotKey slot, CancellationToken cancellationToken) =>
                inner.GetPositionAsync(conversation, slot, cancellationToken);

            public Task SavePositionAsync(ConversationId conversation, ViewerSlotKey slot, ReadingPosition position, DateTimeOffset at, CancellationToken cancellationToken) =>
                inner.SavePositionAsync(conversation, slot, position, at, cancellationToken);

            public Task<string?> GetDraftAsync(ConversationId conversation, ViewerSlotKey slot, CancellationToken cancellationToken) =>
                inner.GetDraftAsync(conversation, slot, cancellationToken);

            public Task SaveDraftAsync(ConversationId conversation, ViewerSlotKey slot, string text, DateTimeOffset at, CancellationToken cancellationToken) =>
                gate.Fail ? throw new IOException("simulated draft write failure") : inner.SaveDraftAsync(conversation, slot, text, at, cancellationToken);
        }
    }

    /// <summary>Streams one piece of an answer, then waits for <see cref="Release"/> without honouring cancellation.</summary>
    private sealed class StubbornClient : IChatClient
    {
        private readonly FakeChatClient _fallback = new();

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (!FakeChatClient.IsAnswer(request))
            {
                await foreach (var item in _fallback.StreamAsync(request, apiKey, cancellationToken))
                {
                    yield return item;
                }

                yield break;
            }

            yield return new ChatStreamEvent.TextDelta("已收到");
            Started.TrySetResult();
            await Release.Task;
            yield return new ChatStreamEvent.Completed(false);
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken) =>
            _fallback.ListModelsAsync(baseUrl, apiKey, cancellationToken);
    }

    /// <summary>A client whose disposal fails, standing in for any resource that throws while the host shuts down.</summary>
    private sealed class FailingDisposal : IChatClient, IAsyncDisposable
    {
        public IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => throw new InvalidOperationException("simulated disposal failure");
    }
}
