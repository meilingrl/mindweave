using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Mindweave.Application.Languages;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// 设置 › 诊断 and its dialog (D2 §2.2), for looking at (MINDWEAVE_SCREENSHOT_DIR, run with
/// <c>--filter FullyQualifiedName~DiagnosticsGallery</c>): diagnostics-*.png.
/// </summary>
[Trait("Category", "Gallery")]
[Collection(Text.LanguageTests.Name)]
public class DiagnosticsGalleryTests
{
    [AvaloniaTheory]
    [InlineData("Light", UiLanguage.English)]
    [InlineData("Dark", UiLanguage.English)]
    [InlineData("Light", UiLanguage.Chinese)]
    [InlineData("Dark", UiLanguage.Chinese)]
    public async Task Diagnostics_as_it_looks(string theme, UiLanguage language)
    {
        var shots = Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(shots), "Screenshots only.");
        var before = CurrentLanguage.Value;
        CurrentLanguage.Set(language);
        try
        {
            var english = language == UiLanguage.English;
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1100, 760, theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light);
            var look = $"{theme.ToLowerInvariant()}-{(english ? "en" : "zh")}";
            await app.StartWithWorkspaceAsync(english ? "History of railways" : "铁路史");
            await app.ConfigureModelAsync();
            app.Client.PromptTokens = _ => 1234;
            app.Client.CompletionTokens = _ => 210;
            app.Client.CacheReadTokens = _ => 1024;

            // Two answers that work, one refused, one cut off by the network.
            app.ShellPage.Conversations.NewCommand.Execute(null);
            var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
            draft.Composer.Text = english ? "How did trains come to be?" : "火车是怎么来的？";
            await draft.Composer.SendCommand.ExecuteAsync(null);
            var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
            await UiWait.StoredAsync(page, 1, MessageState.Complete);
            app.Client.FailWithStatus(GenerationErrorKind.Unauthorized, 401, detail: "HTTP 401: Incorrect API key provided.", requestId: "req_8f2c41d9a7e0");
            page.Composer.Text = english ? "And the first railway?" : "第一条铁路呢？";
            await page.Composer.SendCommand.ExecuteAsync(null);
            await UiWait.StoredAsync(page, 3, MessageState.Failed);
            app.Client.Fail(GenerationErrorKind.Network, "The connection was reset.");
            await page.RetryCommand.ExecuteAsync(page.Messages[3]);
            await UiWait.StoredAsync(page, 4, MessageState.Failed);

            app.Workbench.Navigate(new Navigation.NavigationRequest(Navigation.MainSection.Settings));
            Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage).ShowDiagnosticsCommand.Execute(null);
            var diagnostics = Assert.IsType<DiagnosticsViewModel>(app.Workbench.CurrentPage);
            await diagnostics.Loaded;
            await Task.Delay(300, CancellationToken.None);
            window.UpdateLayout();
            Screenshots.Capture(window, $"diagnostics-1-failures-{look}");

            diagnostics.ToggleRowCommand.Execute(diagnostics.Rows[^1]);
            await Task.Delay(300, CancellationToken.None);
            window.UpdateLayout();
            Screenshots.Capture(window, $"diagnostics-2-failure-open-{look}");

            diagnostics.ShowsAll = true;
            await diagnostics.Loaded;
            diagnostics.ToggleRowCommand.Execute(diagnostics.Rows.First(r => !r.IsFailed && r.Row.Purpose == "answer"));
            await Task.Delay(300, CancellationToken.None);
            window.UpdateLayout();
            Screenshots.Capture(window, $"diagnostics-3-all-open-{look}");

            diagnostics.ExportCommand.Execute(null);
            await app.Root.DiagnosticPackage.Loading;
            await Task.Delay(400, CancellationToken.None);
            window.UpdateLayout();
            Screenshots.Capture(window, $"diagnostics-4-dialog-{look}");
            app.Root.DiagnosticPackage.CloseCommand.Execute(null);
        }
        finally
        {
            CurrentLanguage.Set(before);
        }
    }
}
