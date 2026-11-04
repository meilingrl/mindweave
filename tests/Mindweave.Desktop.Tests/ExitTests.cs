using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Export;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Reading;
using Mindweave.Core.Summaries;
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
    public async Task A_summary_draft_whose_page_was_left_is_kept_and_retried_until_it_is_saved()
    {
        var failing = new FailingSummaryDrafts();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<ISummaryStore>(sp => failing.Wrap(sp.GetRequiredService<SqliteDatabase>())));
        var window = NetworkUiTests.Show(app);
        var page = await ConversationAsync(app);
        await page.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.HasSummary && page.RunningSummary is null);
        page.OpenSummaryCommand.Execute(null);
        var summary = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await summary.Loaded;
        await summary.BeginEditCommand.ExecuteAsync(null);
        failing.Fail = true;
        summary.EditBody = "我自己的笔记";

        // Leaving the page tries to save the draft (and fails); the page is disposed.
        app.Workbench.GoBack();
        Assert.IsNotType<SummaryViewModel>(app.Workbench.CurrentPage);

        window.Close();
        await app.Root.Exit.Running;
        Assert.Equal(ExitStage.Failed, app.Root.Exit.Stage);

        // Still failing: retrying must not quit with the draft lost.
        app.Root.Exit.RetryCommand.Execute(null);
        await app.Root.Exit.Running;
        Assert.True(app.Root.Exit.IsFailed);
        Assert.Equal(0, app.Shell.ExitRequests);

        failing.Fail = false;
        app.Root.Exit.RetryCommand.Execute(null);
        await app.Root.Exit.Running;

        Assert.Equal(1, app.Shell.ExitRequests);
        var detail = await app.Services.GetRequiredService<Application.Summaries.SummaryService>().GetAsync(summary.Node, CancellationToken.None);
        Assert.Equal("我自己的笔记", detail!.Draft?.Body);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_left_draft_that_failed_to_save_is_not_written_back_over_a_newer_draft_or_a_discard(bool discard)
    {
        var failing = new FailingSummaryDrafts();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<ISummaryStore>(sp => failing.Wrap(sp.GetRequiredService<SqliteDatabase>())));
        var window = NetworkUiTests.Show(app);
        var page = await ConversationAsync(app);
        await page.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.HasSummary && page.RunningSummary is null);
        page.OpenSummaryCommand.Execute(null);
        var first = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await first.Loaded;
        await first.BeginEditCommand.ExecuteAsync(null);
        failing.Fail = true;
        first.EditBody = "旧的草稿";
        app.Workbench.GoBack();

        // Storage works again; the same summary is opened and its draft settled anew.
        failing.Fail = false;
        page.OpenSummaryCommand.Execute(null);
        var second = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await second.Loaded;
        await second.BeginEditCommand.ExecuteAsync(null);
        if (discard)
        {
            await second.DiscardEditCommand.ExecuteAsync(null);
        }
        else
        {
            second.EditBody = "新的草稿";
            Assert.True(await app.Services.GetRequiredService<Application.Summaries.SummaryDrafts>().FlushAsync());
        }

        window.Close();
        await app.Root.Exit.Running;

        Assert.Equal(1, app.Shell.ExitRequests);
        var detail = await app.Services.GetRequiredService<Application.Summaries.SummaryService>().GetAsync(second.Node, CancellationToken.None);
        Assert.Equal(discard ? null : "新的草稿", detail!.Draft?.Body);
    }

    [AvaloniaFact]
    public async Task An_export_still_writing_its_file_is_finished_before_the_window_closes()
    {
        var writer = new GatedExportWriter();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<IExportFileWriter>(writer));
        var window = NetworkUiTests.Show(app);
        var page = await ConversationAsync(app);
        await page.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.HasSummary && page.RunningSummary is null);
        page.OpenSummaryCommand.Execute(null);
        var summary = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await summary.Loaded;
        app.Shell.NextExport = _ => new ExportDestination("导出.md", "/unused/导出.md", null);
        var export = summary.ExportCommand.ExecuteAsync(null);
        await writer.Started.Task;

        window.Close();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(0, app.Shell.ExitRequests);

        writer.Release.SetResult();
        await export;
        await app.Root.Exit.Running;
        Assert.Equal(1, writer.Writes);
        Assert.Equal(1, app.Shell.ExitRequests);
    }

    /// <summary>An export writer that holds the write until released.</summary>
    private sealed class GatedExportWriter : IExportFileWriter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Writes { get; private set; }

        public async Task<ExportWriteMode> WriteAsync(ExportDestination destination, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task;
            Writes++;
            return ExportWriteMode.Atomic;
        }
    }

    [AvaloniaFact]
    public async Task A_step_that_throws_while_stopping_shows_the_failure_instead_of_hanging()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        await app.StartWithWorkspaceAsync("火车");
        var generation = app.Services.GetRequiredService<GenerationCoordinator>();
        void Throwing(object? sender, EventArgs e) => throw new InvalidOperationException("simulated listener failure");
        generation.Changed += Throwing;
        try
        {
            window.Close();
            await app.Root.Exit.Running;
        }
        finally
        {
            generation.Changed -= Throwing;
        }

        Assert.Equal(ExitStage.Failed, app.Root.Exit.Stage);
        Assert.Equal(0, app.Shell.ExitRequests);

        app.Root.Exit.RetryCommand.Execute(null);
        await app.Root.Exit.Running;
        Assert.Equal(1, app.Shell.ExitRequests);
    }

    [AvaloniaFact]
    public async Task A_second_launch_during_the_final_release_does_not_touch_the_closed_window()
    {
        await using var app = DesktopHarness.Create();
        var window = NetworkUiTests.Show(app);
        await app.StartWithWorkspaceAsync("火车");

        window.Close();
        await app.Root.Exit.Running;
        Assert.Equal(ExitStage.Releasing, app.Root.Exit.Stage);
        window.Close();

        window.BringToFront();
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task The_host_does_not_wait_again_for_a_task_the_exit_already_gave_up_on()
    {
        var stubborn = new StubbornClient();
        await using var app = DesktopHarness.Create(s =>
        {
            s.AddSingleton<IChatClient>(stubborn);
            s.AddSingleton(new ExitOptions { StopWait = TimeSpan.FromSeconds(2) });
        });
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        await app.Services.GetRequiredService<ConversationService>().StartAsync(app.Workbench.CurrentWorkspace!.Id, "问", CancellationToken.None);
        await stubborn.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(app.Root.Exit.InterceptClose());
        app.Root.Exit.StopAndExitCommand.Execute(null);
        await app.Root.Exit.Running.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.Equal(ExitStage.Releasing, app.Root.Exit.Stage);

        var instance = SingleInstance.TryAcquire(app.Paths);
        Assert.NotNull(instance);
        var host = app.HandOverToHost(instance);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await host.DisposeAsync();
        }
        finally
        {
            watch.Stop();
            stubborn.Release.TrySetResult();
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"The host waited {watch.Elapsed}.");
    }

    /// <summary>
    /// TRACK 2026-11-02: a command flips its flag and then refreshes the page. On macOS the lifetime still ran one queued UI
    /// job after its Exit event in about half of the exits (probe, 2026-11-05), so the host closed the database too early when
    /// it did so in that event. Here that last job is such a command; it must find the database open, which closes once the
    /// UI loop has returned.
    /// </summary>
    [AvaloniaFact]
    public async Task The_database_closes_only_after_the_ui_loop_so_its_last_jobs_still_find_it_open()
    {
        await using var app = DesktopHarness.Create();
        var page = await ConversationAsync(app);
        await UiWait.UntilAsync(() => page.RunningAnswer is null);
        Assert.True(app.Root.Exit.InterceptClose());
        await app.Root.Exit.Running;
        Assert.Equal(ExitStage.Releasing, app.Root.Exit.Stage);
        var database = app.Services.GetRequiredService<SqliteDatabase>();

        var instance = SingleInstance.TryAcquire(app.Paths);
        Assert.NotNull(instance);
        var host = app.HandOverToHost(instance);
        Task? archiving = null;
        var openDuringLoop = false;
        var code = host.Run(() =>
        {
            // The lifetime has raised Exit; one queued job still runs: archive (the flag) and then the page refresh.
            openDuringLoop = database.IsOpen;
            archiving = page.ArchiveCommand.ExecuteAsync(null);
            PumpUntil(archiving);
            return 7;
        });

        Assert.Equal(7, code);
        Assert.True(openDuringLoop);
        Assert.NotNull(archiving);
        await archiving;
        Assert.True(page.IsArchived);
        Assert.False(database.IsOpen);
        await using var again = SingleInstance.TryAcquire(app.Paths);
        Assert.NotNull(again);
    }

    /// <summary>A UI loop that ends by throwing still has the database closed and the single-instance lock released.</summary>
    [AvaloniaFact]
    public async Task A_ui_loop_that_throws_still_closes_the_database_and_releases_the_lock()
    {
        await using var app = DesktopHarness.Create();
        await ConversationAsync(app);
        var database = app.Services.GetRequiredService<SqliteDatabase>();
        var instance = SingleInstance.TryAcquire(app.Paths);
        Assert.NotNull(instance);
        var host = app.HandOverToHost(instance);

        var thrown = Assert.Throws<InvalidOperationException>(() => host.Run(() => throw new InvalidOperationException("loop failed")));

        Assert.Equal("loop failed", thrown.Message);
        Assert.False(database.IsOpen);
        await using var again = SingleInstance.TryAcquire(app.Paths);
        Assert.NotNull(again);
    }

    /// <summary>Runs UI jobs synchronously until <paramref name="task"/> ends, as the lifetime's loop would before returning.</summary>
    private static void PumpUntil(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
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

    internal static async Task<ConversationViewModel> ConversationAsync(DesktopHarness app)
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

    /// <summary>The real summary store, whose edit-draft writes fail while <see cref="Fail"/> is set.</summary>
    internal sealed class FailingSummaryDrafts
    {
        public volatile bool Fail;

        /// <summary>Saving an edit as a version fails as storage refusing the write.</summary>
        public volatile bool FailEdits;

        public ISummaryStore Wrap(SqliteDatabase database)
        {
            var proxy = System.Reflection.DispatchProxy.Create<ISummaryStore, Proxy>();
            ((Proxy)(object)proxy).Configure(new SqliteSummaryStore(database), this);
            return proxy;
        }

#pragma warning disable CA1852 // DispatchProxy derives a runtime type from it, so it cannot be sealed.
        public class Proxy : System.Reflection.DispatchProxy
#pragma warning restore CA1852
        {
            private ISummaryStore _inner = null!;
            private FailingSummaryDrafts _gate = null!;

            public void Configure(ISummaryStore inner, FailingSummaryDrafts gate)
            {
                _inner = inner;
                _gate = gate;
            }

            protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
            {
                ArgumentNullException.ThrowIfNull(targetMethod);
                if (targetMethod.Name == nameof(ISummaryStore.SaveEditAsync) && _gate.FailEdits)
                {
                    return Task.FromException<SummaryVersionId?>(new StorageWriteException(StorageFailureKind.Full, "simulated full disk", null));
                }

                return targetMethod.Name == nameof(ISummaryStore.SaveDraftAsync) && _gate.Fail
                    ? Task.FromException(new IOException("simulated summary draft write failure"))
                    : targetMethod.Invoke(_inner, args);
            }
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
