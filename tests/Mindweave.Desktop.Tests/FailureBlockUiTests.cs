using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Text;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>The one failure block (D2 §2.1, requirements §10.8), acceptance scenarios 1 and 2 of the design (§2.7).</summary>
[Collection(Text.LanguageTests.Name)]
public class FailureBlockUiTests
{
    /// <summary>Scenario 1: a wrong key says what failed, what to do, links to the connection; copying gives status and ID, not the key.</summary>
    [AvaloniaFact]
    public async Task A_wrong_key_says_what_to_do_links_to_the_connection_and_copies_status_and_request_id_without_the_key()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        await app.StartWithWorkspaceAsync("火车");
        const string key = "sk-live-0123456789abcdef";
        await app.Services.GetRequiredService<Application.Models.ModelSettingsService>()
            .SaveAsync(new Application.Models.ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", key), CancellationToken.None);

        // A service that echoes the key, as some do; the client scrubs what it passes on.
        app.Client.FailWithStatus(GenerationErrorKind.Unauthorized, 401, detail: "HTTP 401: Incorrect API key provided: " + key, requestId: "req_wrong_key_1");
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = "蒸汽机车何时发明";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.StoredAsync(page, 1, MessageState.Failed);
        await UiWait.AnswersRenderedAsync(window);
        window.UpdateLayout();

        var failure = page.Messages[1].Failure!;
        Assert.Equal(page.Messages[1].StateLabel, failure.Headline);
        Assert.Equal(UiText.FailureAdvice(GenerationErrorKind.Unauthorized), failure.Advice);
        var view = CurrentView(window);
        Assert.True(Find<Avalonia.Controls.TextBlock>(view, "FailureAdvice").IsEffectivelyVisible);
        Assert.True(Find<Button>(view, "FailureFix").IsEffectivelyVisible);
        Assert.False(Find<SelectableTextBlock>(view, "FailureDetailsText").IsEffectivelyVisible); // Closed at first.

        UiInput.Click(window, Find<Button>(view, "FailureDetailsToggle"));
        await failure.Loaded;
        window.UpdateLayout();
        var details = Find<SelectableTextBlock>(view, "FailureDetailsText");
        Assert.True(details.IsEffectivelyVisible);
        Assert.Contains("401", details.Text, StringComparison.Ordinal);
        Assert.Contains("req_wrong_key_1", details.Text, StringComparison.Ordinal);
        Assert.Contains("fake-model", details.Text, StringComparison.Ordinal);

        var copied = failure.CopyText;
        Assert.Contains("req_wrong_key_1", copied, StringComparison.Ordinal);
        Assert.Contains(UiText.FailureStatusLabel + UiText.FailureLabelSeparator + "401", copied, StringComparison.Ordinal);
        Assert.Contains("Mindweave ", copied, StringComparison.Ordinal); // The app version and the system close it.
        Assert.DoesNotContain(key, copied, StringComparison.Ordinal);
        Assert.DoesNotContain("蒸汽机车", copied, StringComparison.Ordinal); // No conversation text.

        // The link opens 设置 at the model connection.
        UiInput.Click(window, Find<Button>(view, "FailureFix"));
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        await UiWait.UntilAsync(() => settings.PendingSection is null);
        window.UpdateLayout();
        var model = window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "ModelSection");
        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "Scroller");
        Assert.InRange(model.TranslatePoint(default, scroller)!.Value.Y, -1, 60);
    }

    /// <summary>Scenario 2: a failed connection test shows the same block in 设置, its details selectable.</summary>
    [AvaloniaFact]
    public async Task A_failed_connection_test_shows_the_same_block_in_settings()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 900);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.Workbench.Navigate(new NavigationRequest(MainSection.Settings));
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        app.Client.FailWithStatus(GenerationErrorKind.QuotaOrRateLimited, 429, detail: "HTTP 429: slow down", requestId: "req_quota_7");

        await settings.Model.TestCommand.ExecuteAsync(null);
        window.UpdateLayout();

        var failure = settings.Model.Failure!;
        Assert.Equal(UiText.ConnectionFailed(GenerationErrorKind.QuotaOrRateLimited), failure.Headline);
        Assert.Null(settings.Model.Status);
        var block = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ModelFailure");
        Assert.True(block.IsEffectivelyVisible);
        failure.IsOpen = true;
        window.UpdateLayout();
        var details = Find<SelectableTextBlock>(block, "FailureDetailsText");
        Assert.True(details.IsEffectivelyVisible);
        Assert.Contains("req_quota_7", details.Text, StringComparison.Ordinal);
        Assert.Contains("HTTP 429: slow down", details.Text, StringComparison.Ordinal);

        // A test that works again clears the block.
        app.Client.Reply("OK");
        await settings.Model.TestCommand.ExecuteAsync(null);
        Assert.Null(settings.Model.Failure);
        Assert.Equal(UiText.TestSucceeded, settings.Model.Status);
    }

    [Fact]
    public void A_failed_tool_step_is_the_failure_block_and_a_finished_one_its_plain_outcome()
    {
        var at = new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
        var task = new GenerationTaskId(Guid.NewGuid());
        ContentBlock[] blocks =
        [
            new ToolCallBlock("c1", "web_search", "{}", Core.Runs.ToolRisk.LocalRead, "Searched: trains"),
            new ToolResultBlock("c1", Core.Runs.ToolResultStatus.Failed, "The search service could not be reached.", null, "Failed"),
            new ToolCallBlock("c2", "web_search", "{}", Core.Runs.ToolRisk.LocalRead, "Searched: rails"),
            new ToolResultBlock("c2", Core.Runs.ToolResultStatus.Succeeded, "3 results", null, "3 results"),
        ];
        var run = new Application.Storage.RunRecord(
            new MessageId(Guid.NewGuid()),
            [new Application.Storage.RunStepRecord(task, 1, Core.Runs.RunStepKind.Tool, Core.Runs.RunStepState.Failed, at, at.AddSeconds(2), CallId: "c1", Tool: "web_search") { Target = "api.search.example" }],
            null);

        var steps = RunStepItem.From(blocks, run, null, null, null);

        var failure = steps[0].Failure!;
        Assert.False(steps[0].ShowsOutcome);
        Assert.Equal("The search service could not be reached.", failure.Headline);
        Assert.Contains(UiText.FailureHostLabel + UiText.FailureLabelSeparator + "api.search.example", failure.CopyText, StringComparison.Ordinal);
        Assert.Contains(UiText.FailureDurationLabel + UiText.FailureLabelSeparator + UiText.FailureDuration(TimeSpan.FromSeconds(2)), failure.CopyText, StringComparison.Ordinal);
        Assert.Null(steps[1].Failure);
        Assert.True(steps[1].ShowsOutcome);
    }

    [Fact]
    public void Copied_details_hold_the_block_its_lines_and_the_installation()
    {
        var failure = new FailureViewModel(
            "Failed: timed out",
            "Retry.",
            details: new Application.Diagnostics.FailureDetails(null, "m1", 504, "r-1", TimeSpan.FromSeconds(1.5), "HTTP 504: gateway"));

        var lines = failure.CopyText.Split(Environment.NewLine);
        Assert.Equal("Failed: timed out", lines[0]);
        Assert.Equal("Retry.", lines[1]);
        Assert.Contains(UiText.FailureModelLabel + UiText.FailureLabelSeparator + "m1", lines);
        Assert.Contains(UiText.FailureRequestIdLabel + UiText.FailureLabelSeparator + "r-1", lines);
        Assert.StartsWith("Mindweave ", lines[^1], StringComparison.Ordinal);
        Assert.Contains(Application.Diagnostics.Installation.System(), lines[^1], StringComparison.Ordinal);
    }
}
