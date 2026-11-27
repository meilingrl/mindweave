using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Languages;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The failure block (D2 §2.1), for looking at (MINDWEAVE_SCREENSHOT_DIR, run with
/// <c>--filter FullyQualifiedName~FailureGallery</c>): failure-*.png, an answer closed and open, the connection test in 设置,
/// and the log folder's row on the storage page.
/// </summary>
[Trait("Category", "Gallery")]
[Collection(Text.LanguageTests.Name)]
public class FailureGalleryTests
{
    [AvaloniaTheory]
    [InlineData("Light", UiLanguage.English)]
    [InlineData("Dark", UiLanguage.English)]
    [InlineData("Light", UiLanguage.Chinese)]
    [InlineData("Dark", UiLanguage.Chinese)]
    public async Task Failures_as_they_look(string theme, UiLanguage language)
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

            // An answer refused for a wrong key.
            app.Client.FailWithStatus(
                GenerationErrorKind.Unauthorized,
                401,
                detail: "HTTP 401: Incorrect API key provided. You can find your API key in your account settings.",
                requestId: "req_8f2c41d9a7e0");
            app.ShellPage.Conversations.NewCommand.Execute(null);
            var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
            draft.Composer.Text = english ? "How did trains come to be?" : "火车是怎么来的？";
            await draft.Composer.SendCommand.ExecuteAsync(null);
            var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
            await UiWait.StoredAsync(page, 1, MessageState.Failed);
            await UiWait.AnswersRenderedAsync(window);
            window.UpdateLayout();
            Screenshots.Capture(window, $"failure-1-answer-{look}");
            var failure = page.Messages[1].Failure!;
            failure.IsOpen = true;
            await failure.Loaded;
            await Task.Delay(300, CancellationToken.None);
            window.UpdateLayout();
            Screenshots.Capture(window, $"failure-2-answer-details-{look}");

            // The connection test in 设置, its details open.
            app.Workbench.Navigate(new NavigationRequest(MainSection.Settings));
            var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
            await settings.Loaded;
            app.Client.FailWithStatus(GenerationErrorKind.QuotaOrRateLimited, 429, detail: "HTTP 429: Rate limit reached for requests.", requestId: "req_51b0e7c2");
            await settings.Model.TestCommand.ExecuteAsync(null);
            settings.Model.Failure!.IsOpen = true;
            window.UpdateLayout();
            var block = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ModelFailure");
            block.BringIntoView();
            await Task.Delay(300, CancellationToken.None);
            window.UpdateLayout();
            Screenshots.Capture(window, $"failure-3-connection-test-{look}");

            // The log folder on the storage page.
            settings.ShowStorageDetailsCommand.Execute(null);
            await Task.Delay(300, CancellationToken.None);
            window.UpdateLayout();
            Screenshots.Capture(window, $"failure-4-log-folder-{look}");
            Assert.NotNull(app.Services.GetRequiredService<Application.AppEnvironment>().LogDirectory);
        }
        finally
        {
            CurrentLanguage.Set(before);
        }
    }
}
