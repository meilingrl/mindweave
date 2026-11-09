using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Export;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Reading;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Infrastructure.Sqlite;
using static Mindweave.Desktop.Tests.ExitScenario;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Saving before the window closes (architecture §12.3, requirement §9.3): drafts and exports are finished first, and a
/// failed save keeps the window open for a retry or a return to work.
/// </summary>
public class ExitSavingTests
{
    [AvaloniaFact]
    public async Task Drafts_typed_just_before_quitting_are_saved_before_the_window_closes()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var page = await app.StartWithConversationAsync(waitForTitle: false);
        await page.Summary.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.Summary.HasSummary && page.Summary.RunningSummary is null);

        // A message draft (saved 0.5 s after typing) and a summary edit draft (1 s throttle), both still pending.
        page.Composer.Text = "还没发出去的问题";
        page.Summary.OpenSummaryCommand.Execute(null);
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
        var window = app.ShowWindow(1100, 720);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        var page = await app.StartWithConversationAsync(waitForTitle: false);
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
        var window = app.ShowWindow(1100, 720);
        var page = await app.StartWithConversationAsync(waitForTitle: false);
        await page.Summary.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.Summary.HasSummary && page.Summary.RunningSummary is null);
        page.Summary.OpenSummaryCommand.Execute(null);
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
        var window = app.ShowWindow(1100, 720);
        var page = await app.StartWithConversationAsync(waitForTitle: false);
        await page.Summary.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.Summary.HasSummary && page.Summary.RunningSummary is null);
        page.Summary.OpenSummaryCommand.Execute(null);
        var first = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await first.Loaded;
        await first.BeginEditCommand.ExecuteAsync(null);
        failing.Fail = true;
        first.EditBody = "旧的草稿";
        app.Workbench.GoBack();

        // Storage works again; the same summary is opened and its draft settled anew.
        failing.Fail = false;
        page.Summary.OpenSummaryCommand.Execute(null);
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
        var window = app.ShowWindow(1100, 720);
        var page = await app.StartWithConversationAsync(waitForTitle: false);
        await page.Summary.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.Summary.HasSummary && page.Summary.RunningSummary is null);
        page.Summary.OpenSummaryCommand.Execute(null);
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
    public async Task Returning_after_a_failed_save_accepts_new_questions_and_does_not_revive_stopped_answers()
    {
        var failing = new FailingDrafts();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<IReadingStateStore>(sp => failing.Wrap(sp.GetRequiredService<SqliteDatabase>())));
        var page = await app.StartWithConversationAsync(waitForTitle: false);
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
}
