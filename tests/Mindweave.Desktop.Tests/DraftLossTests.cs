using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Summary edit drafts whose page was left while their save failed (architecture review H1, H2): the newest input must
/// survive reopening the summary, committing it, and switching versions.
/// </summary>
public class DraftLossTests
{
    [AvaloniaFact]
    public async Task A_reopened_editor_shows_and_commits_the_newest_draft_left_unsaved()
    {
        var failing = new FailingSummaryDrafts();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<ISummaryStore>(sp => failing.Wrap(sp.GetRequiredService<SqliteDatabase>())));
        app.ShowWindow(1100, 720);
        var page = await app.StartWithConversationAsync(waitForTitle: false);
        await page.Summary.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.Summary.HasSummary && page.Summary.RunningSummary is null);
        page.Summary.OpenSummaryCommand.Execute(null);
        var first = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await first.Loaded;
        await first.BeginEditCommand.ExecuteAsync(null);
        failing.Fail = true;
        first.EditBody = "离开前的修改";
        app.Workbench.GoBack();

        // The left page's save has finished failing before storage works again.
        Assert.False(await app.Services.GetRequiredService<SummaryDrafts>().FlushAsync());
        failing.Fail = false;

        page.Summary.OpenSummaryCommand.Execute(null);
        var second = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await second.Loaded;
        await second.BeginEditCommand.ExecuteAsync(null);
        Assert.Equal("离开前的修改", second.EditBody);

        await second.SaveEditCommand.ExecuteAsync(null);
        var detail = await app.Services.GetRequiredService<Application.Summaries.SummaryService>().GetAsync(second.Node, CancellationToken.None);
        Assert.Equal("离开前的修改", detail!.Current.Body);
    }

    [AvaloniaFact]
    public async Task Saving_a_version_while_storage_refuses_keeps_the_edit_and_says_why()
    {
        var failing = new FailingSummaryDrafts();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<ISummaryStore>(sp => failing.Wrap(sp.GetRequiredService<SqliteDatabase>())));
        app.ShowWindow(1100, 720);
        var page = await app.StartWithConversationAsync(waitForTitle: false);
        await page.Summary.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.Summary.HasSummary && page.Summary.RunningSummary is null);
        page.Summary.OpenSummaryCommand.Execute(null);
        var summary = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await summary.Loaded;
        await summary.BeginEditCommand.ExecuteAsync(null);
        summary.EditBody = "要保存的修改";
        failing.FailEdits = true;

        await summary.SaveEditCommand.ExecuteAsync(null);

        Assert.True(summary.IsEditing);
        Assert.Equal("要保存的修改", summary.EditBody);
        Assert.Equal(UiText.StoragePaused, summary.Notice);
    }

    [AvaloniaFact]
    public async Task A_draft_left_unsaved_keeps_versions_from_being_switched_and_survives()
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
        var original = first.Shown!.Id;

        // A second saved version, so there is an older one to switch back to.
        await first.BeginEditCommand.ExecuteAsync(null);
        first.EditBody = "第二版";
        await first.SaveEditCommand.ExecuteAsync(null);

        await first.BeginEditCommand.ExecuteAsync(null);
        failing.Fail = true;
        first.EditBody = "未保存的修改";
        app.Workbench.GoBack();
        Assert.False(await app.Services.GetRequiredService<SummaryDrafts>().FlushAsync());
        failing.Fail = false;

        page.Summary.OpenSummaryCommand.Execute(null);
        var second = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await second.Loaded;
        await second.SetCurrentCommand.ExecuteAsync(original);

        window.Close();
        await app.Root.Exit.Running;
        var detail = await app.Services.GetRequiredService<Application.Summaries.SummaryService>().GetAsync(second.Node, CancellationToken.None);
        Assert.NotEqual(original, detail!.Node.Current);
        Assert.Equal("未保存的修改", detail.Draft?.Body);
    }
}
