using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Export;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using Mindweave.Testing;

namespace Mindweave.Desktop.Tests;

/// <summary>Exporting the shown saved version from the summary page (§8.3), with the save dialog recorded.</summary>
public sealed class ExportUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mindweave-export-ui", Guid.NewGuid().ToString("N"));

    public ExportUiTests() => Directory.CreateDirectory(_dir);

    [AvaloniaFact]
    public async Task Exporting_asks_where_with_the_title_as_name_writes_the_file_and_says_so_without_reading_it()
    {
        await using var app = DesktopHarness.Create();
        var window = new MainWindow { DataContext = app.Root, Width = 1000, Height = 700 };
        window.Show();
        var (_, summary) = await SummaryAsync(app);
        var target = Path.Combine(_dir, "导出.md");
        app.Shell.NextExport = _ => new ExportDestination("导出.md", target, null);
        window.UpdateLayout();
        var view = window.GetVisualDescendants().OfType<SummaryView>().Single();
        var button = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ExportButton");
        Assert.Equal(UiText.ExportMarkdown, button.Content);
        Assert.True(button.IsEffectivelyEnabled);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == UiText.ExportDraftNote && t.IsEffectivelyVisible);

        await summary.ExportCommand.ExecuteAsync(null);

        Assert.Equal([FakeChatClient.DefaultSummaryTitle + ".md"], app.Shell.ExportRequests);
        var text = await File.ReadAllTextAsync(target);
        Assert.StartsWith($"# {FakeChatClient.DefaultSummaryTitle}\n\n> 来源会话：{FakeChatClient.DefaultTitle} · ", text, StringComparison.Ordinal);
        Assert.Equal(UiText.Exported("导出.md"), summary.Notice);
        Assert.False(summary.NoticeIsError);

        // Exporting is not reading.
        Assert.False(summary.ShownIsRead);
        Assert.False(Detail(app, summary.Node).ReadState(summary.Shown!.Id).IsRead);
        Assert.Equal(UiText.MarkerUnread, summary.MarkersText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_source_note_can_be_left_out_and_cancelling_the_dialog_writes_nothing()
    {
        await using var app = DesktopHarness.Create();
        var (_, summary) = await SummaryAsync(app);
        var target = Path.Combine(_dir, "导出.md");

        await summary.ExportCommand.ExecuteAsync(null);
        Assert.Single(app.Shell.ExportRequests);
        Assert.Null(summary.Notice);
        Assert.False(File.Exists(target));

        summary.ExportOmitSource = true;
        app.Shell.NextExport = _ => new ExportDestination("导出.md", target, null);
        await summary.ExportCommand.ExecuteAsync(null);

        var shown = summary.Shown!;
        Assert.Equal($"# {shown.Title}\n\n{shown.Body}\n", await File.ReadAllTextAsync(target));
    }

    [AvaloniaFact]
    public async Task The_candidate_cannot_be_exported_and_says_why()
    {
        await using var app = DesktopHarness.Create();
        var (page, summary) = await SummaryAsync(app);
        Assert.True(summary.ExportCommand.CanExecute(null));
        Assert.Null(summary.ExportHint);

        summary.ReturnToConversationCommand.Execute(null);
        page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.SummaryMarkersText?.Contains(UiText.MarkerCandidate, StringComparison.Ordinal) == true);
        summary = await OpenAsync(app, page);
        await UiWait.UntilAsync(() => summary.HasCandidate);

        summary.ShowCandidateCommand.Execute(null);
        Assert.True(summary.ShownIsCandidate);
        Assert.False(summary.ExportCommand.CanExecute(null));
        Assert.Equal(UiText.ExportCandidateHint, summary.ExportHint);

        summary.ShowCurrentCommand.Execute(null);
        Assert.True(summary.ExportCommand.CanExecute(null));
        Assert.Null(summary.ExportHint);
    }

    [AvaloniaFact]
    public async Task A_failed_write_or_a_deleted_version_is_reported_in_the_notice()
    {
        await using var app = DesktopHarness.Create();
        var (_, summary) = await SummaryAsync(app);

        app.Shell.NextExport = _ => new ExportDestination("x.md", Path.Combine(_dir, "不存在", "x.md"), null);
        await summary.ExportCommand.ExecuteAsync(null);
        Assert.StartsWith(UiText.ExportFailed(ExportFailure.Failed, string.Empty)[..6], summary.Notice, StringComparison.Ordinal);
        Assert.True(summary.NoticeIsError);

        // The version goes while the dialog is open: nothing is written.
        var target = Path.Combine(_dir, "导出.md");
        var service = app.Services.GetRequiredService<SummaryService>();
        app.Shell.NextExport = _ =>
        {
            var current = Detail(app, summary.Node);
            service.SaveEditAsync(summary.Node, current.Node.Current, "第二版", "第二版正文", CancellationToken.None).GetAwaiter().GetResult();
            service.DeleteVersionsAsync(summary.Node, new HashSet<SummaryVersionId> { current.Node.Current }, CancellationToken.None).GetAwaiter().GetResult();
            return new ExportDestination("导出.md", target, null);
        };
        await summary.ExportCommand.ExecuteAsync(null);

        Assert.Equal(UiText.ExportRefused(ExportRefusal.VersionMissing), summary.Notice);
        Assert.False(File.Exists(target));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Application.Storage.SummaryDetail Detail(DesktopHarness app, SummaryNodeId node) =>
        app.Services.GetRequiredService<SummaryService>().GetAsync(node, CancellationToken.None).Result!;

    private static async Task<(ConversationViewModel Page, SummaryViewModel Summary)> SummaryAsync(DesktopHarness app)
    {
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = "火车是怎么发明的？";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].CanBranch && page.Title == FakeChatClient.DefaultTitle);
        await page.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.HasSummary && page.RunningSummary is null);
        return (page, await OpenAsync(app, page));
    }

    private static async Task<SummaryViewModel> OpenAsync(DesktopHarness app, ConversationViewModel page)
    {
        await UiWait.UntilAsync(() => page.HasSummary);
        page.OpenSummaryCommand.Execute(null);
        var summary = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await summary.Loaded;
        return summary;
    }
}
