using System.IO.Compression;
using Avalonia.Headless.XUnit;
using Mindweave.Application.Export;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Text;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>设置 › 诊断 (D2 §2.2): the request list, the link from a failure block, and the package from the page and the recovery page.</summary>
[Collection(Text.LanguageTests.Name)]
public sealed class DiagnosticsUiTests : IDisposable
{
    private readonly string _out = Path.Combine(Path.GetTempPath(), "mindweave-desktop-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_out))
        {
            Directory.Delete(_out, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task The_list_opens_on_failures_shows_all_on_the_switch_and_a_row_says_cache_not_reported()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow();
        var page = await FailedAnswerAsync(app);
        app.Client.Reply("好的。");
        await page.RetryCommand.ExecuteAsync(page.Messages[1]);
        await UiWait.StoredAsync(page, 2, MessageState.Complete);

        app.Workbench.Navigate(new Navigation.NavigationRequest(Navigation.MainSection.Settings));
        Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage).ShowDiagnosticsCommand.Execute(null);
        var diagnostics = Assert.IsType<DiagnosticsViewModel>(app.Workbench.CurrentPage);
        await diagnostics.Loaded;

        var failed = Assert.Single(diagnostics.Rows);
        Assert.True(failed.IsFailed);
        Assert.Equal(UiText.RequestPurpose("answer"), failed.PurposeText);
        Assert.Equal("fake-model", failed.ModelText);

        diagnostics.ShowsAll = true;
        await diagnostics.Loaded;
        Assert.True(diagnostics.Rows.Count >= 2);
        var completed = diagnostics.Rows.First(r => !r.IsFailed && r.Row.Purpose == "answer");
        diagnostics.ToggleRowCommand.Execute(completed);
        Assert.True(completed.IsOpen);
        Assert.Contains(UiText.RequestCacheLabel + UiText.FailureLabelSeparator + UiText.RequestCache(null, null), completed.Details.DetailsText, StringComparison.Ordinal);
        Assert.Contains(UiText.NotReported, completed.Details.DetailsText, StringComparison.Ordinal);
        Assert.DoesNotContain(UiText.RequestCacheLabel + UiText.FailureLabelSeparator + "0", completed.Details.DetailsText, StringComparison.Ordinal);

        diagnostics.OpenLogFolderCommand.Execute(null);
        Assert.Equal([diagnostics.Environment.LogDirectory], app.Shell.OpenedFolders);
    }

    [AvaloniaFact]
    public async Task A_failure_block_opens_its_row_on_the_page()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow();
        var page = await FailedAnswerAsync(app);
        var failure = page.Messages[1].Failure!;
        Assert.True(failure.CanShowInList);

        failure.ShowInListCommand.Execute(null);

        var diagnostics = Assert.IsType<DiagnosticsViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => diagnostics.Rows.Any(r => r.IsOpen));
        var open = Assert.Single(diagnostics.Rows, r => r.IsOpen);
        Assert.Equal(page.Messages[1].Id, open.Row.Answer);
        Assert.Contains("req_link_1", open.Details.DetailsText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_package_is_saved_from_the_page_with_what_the_dialog_listed()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow();
        await FailedAnswerAsync(app);
        app.Workbench.Navigate(new Navigation.NavigationRequest(Navigation.MainSection.Settings));
        Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage).ShowDiagnosticsCommand.Execute(null);
        var diagnostics = Assert.IsType<DiagnosticsViewModel>(app.Workbench.CurrentPage);

        diagnostics.ExportCommand.Execute(null);
        var dialog = app.Root.DiagnosticPackage;
        Assert.True(dialog.IsOpen);
        await dialog.Loading;
        Assert.True(dialog.DataAvailable);
        Assert.NotNull(dialog.RequestsLine);
        Directory.CreateDirectory(_out);
        var file = Path.Combine(_out, "d.zip");
        app.Shell.NextDiagnostic = _ => new ExportDestination("d.zip", file, null);
        dialog.Logs = false;
        await dialog.SaveCommand.ExecuteAsync(null);

        Assert.StartsWith("mindweave-diagnostics-", Assert.Single(app.Shell.DiagnosticRequests), StringComparison.Ordinal);
        Assert.False(dialog.NoticeIsError);
        using var zip = ZipFile.OpenRead(file);
        Assert.Equal(["requests.csv", "settings.txt", "summary.txt"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        dialog.CloseCommand.Execute(null);
        Assert.False(dialog.IsOpen);
    }

    /// <summary>Scenario 5 of design §2.7: the data cannot be opened; the recovery page saves the summary and the logs.</summary>
    [AvaloniaFact]
    public async Task The_recovery_page_saves_a_package_of_the_summary_and_the_logs()
    {
        await using var app = DesktopHarness.Create();
        Directory.CreateDirectory(app.Paths.DataDirectory);
        await File.WriteAllTextAsync(app.Paths.DatabasePath, "definitely not sqlite, but long enough to be read as a header....................................................", TestContext.Current.CancellationToken);
        await app.StartAsync();
        var recovery = Assert.IsType<RecoveryViewModel>(app.Root.Current);

        recovery.ExportDiagnosticsCommand.Execute(null);
        var dialog = app.Root.DiagnosticPackage;
        await dialog.Loading;
        Assert.True(dialog.WithoutData);
        Assert.Null(dialog.RequestsLine);
        Directory.CreateDirectory(_out);
        var file = Path.Combine(_out, "r.zip");
        app.Shell.NextDiagnostic = _ => new ExportDestination("r.zip", file, null);
        await dialog.SaveCommand.ExecuteAsync(null);

        using var zip = ZipFile.OpenRead(file);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("summary.txt", names);
        Assert.DoesNotContain("requests.csv", names);
        Assert.DoesNotContain("settings.txt", names);
        Assert.All(names.Where(n => n != "summary.txt"), n => Assert.StartsWith("logs/", n, StringComparison.Ordinal));
    }

    private static async Task<ConversationViewModel> FailedAnswerAsync(DesktopHarness app)
    {
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.Client.FailWithStatus(GenerationErrorKind.ServerError, 503, detail: "HTTP 503: overloaded", requestId: "req_link_1");
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = "问";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.StoredAsync(page, 1, MessageState.Failed);
        await UiWait.UntilAsync(() => page.Messages[1].Failure is not null);
        return page;
    }
}
