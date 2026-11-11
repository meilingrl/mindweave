using System.Collections.Concurrent;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Storage;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using Mindweave.Testing;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// 设置 › 工作区, renaming (2026-11-11): 建议名称 asks the model for names drawn from the workspace's conversations; a picked
/// name fills the box and nothing is saved until Enter or 保存. Stopping, failures, an empty workspace and late answers.
/// </summary>
public class WorkspaceNameSuggestionUiTests
{
    [AvaloniaFact]
    public async Task Asking_shows_names_to_pick_and_picking_one_fills_the_box_without_saving()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await NetworkScenario.SeedAsync(app);
        var page = await OpenPageAsync(app, window);
        var row = page.Rows[0];

        // Not renaming: the counts, and no 建议名称.
        Assert.True(Rows(window, "WorkspaceRowCounts")[0].IsEffectivelyVisible);
        Assert.False(Rows(window, "SuggestNamesHint")[0].IsEffectivelyVisible);
        Assert.False(Rows(window, "SuggestNamesButton")[0].IsEffectivelyVisible);

        UiInput.Click(window, Rows<Button>(window, "RenameWorkspaceButton")[0]);
        var box = Rows<TextBox>(window, "WorkspaceRenameBox")[0];
        await UiWait.UntilAsync(() => box.IsFocused);
        window.UpdateLayout();

        // While renaming, the line under the box says what the button sends, before anything is sent.
        var button = Rows<Button>(window, "SuggestNamesButton")[0];
        Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
        Assert.Equal(UiText.SuggestNamesName("火车"), AutomationProperties.GetName(button));
        Assert.Equal(UiText.SuggestNamesTip, AutomationProperties.GetHelpText(button));
        Assert.True(Rows(window, "SuggestNamesHint")[0].IsEffectivelyVisible);
        Assert.False(Rows(window, "WorkspaceRowCounts")[0].IsEffectivelyVisible);
        Assert.False(Rows(window, "SuggestionPanel")[0].IsEffectivelyVisible);
        Assert.DoesNotContain(app.Client.Requests, FakeChatClient.IsWorkspaceNaming);

        UiInput.Click(window, button);
        await page.Suggesting;
        window.UpdateLayout();
        Assert.Equal(NameSuggestionState.Shown, row.SuggestionState);
        var chips = Rows<Button>(window, "SuggestionChip");
        Assert.Equal(["蒸汽机车史", "铁路发展", "火车与工业革命"], chips.Select(c => c.Content as string));
        Assert.Equal(UiText.SuggestionChipName("铁路发展"), AutomationProperties.GetName(chips[1]));
        Assert.True(Rows(window, "SuggestionsCaption")[0].IsEffectivelyVisible);
        var sent = Assert.Single(app.Client.Requests, FakeChatClient.IsWorkspaceNaming).Turns[1].Content;
        Assert.Contains("当前名称：火车", sent, StringComparison.Ordinal);

        // Picking fills the box, still editable, and saves nothing; the focus is back in the box after the name.
        UiInput.Click(window, chips[1]);
        Assert.True(row.IsRenaming);
        Assert.Equal("铁路发展", row.RenameText);
        await UiWait.UntilAsync(() => box.IsFocused && box.CaretIndex == "铁路发展".Length && string.IsNullOrEmpty(box.SelectedText));
        Assert.Equal("火车", row.Name);
        Assert.Equal("火车", app.Workbench.CurrentWorkspace!.Name.Value);
        Assert.Equal("火车", (await app.Services.GetRequiredService<WorkspaceService>().ListAsync(CancellationToken.None)).Single().Name.Value);
        Assert.Equal(3, Rows<Button>(window, "SuggestionChip").Count);

        // Typing goes on from the picked name; Enter saves what the box says, and the names go with the rename.
        window.KeyTextInput("史");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await page.Renaming;
        Assert.False(row.IsRenaming);
        Assert.Equal("铁路发展史", app.Workbench.CurrentWorkspace!.Name.Value);
        Assert.Empty(row.Suggestions);
        Assert.Equal(NameSuggestionState.Idle, row.SuggestionState);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_keyboard_goes_from_the_button_to_the_names_and_enter_picks_one()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await NetworkScenario.SeedAsync(app);
        var page = await OpenPageAsync(app, window);
        var row = page.Rows[0];
        UiInput.Click(window, Rows<Button>(window, "RenameWorkspaceButton")[0]);
        var box = Rows<TextBox>(window, "WorkspaceRenameBox")[0];
        await UiWait.UntilAsync(() => box.IsFocused);

        var button = Rows<Button>(window, "SuggestNamesButton")[0];
        button.Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await page.Suggesting;
        window.UpdateLayout();
        await UiWait.UntilAsync(() => Rows<Button>(window, "SuggestionChip") is [{ IsFocused: true }, ..]);

        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        await UiWait.UntilAsync(() => Rows<Button>(window, "SuggestionChip")[1].IsFocused);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal("铁路发展", row.RenameText);
        Assert.True(row.IsRenaming);
        await UiWait.UntilAsync(() => box.IsFocused);
        Assert.Equal("火车", app.Workbench.CurrentWorkspace!.Name.Value);
        window.Close();
    }

    /// <summary>
    /// Review 2026-11-11: the names (and 重试, 前往设置) sit under the box, outside the row's actions; Esc on one of them ends
    /// the rename and must hand the keyboard back to the row's 改名, not drop it when the names disappear.
    /// </summary>
    [AvaloniaFact]
    public async Task Esc_on_a_suggested_name_ends_the_rename_and_the_focus_returns_to_rename()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await NetworkScenario.SeedAsync(app);
        var page = await OpenPageAsync(app, window);
        var row = page.Rows[0];
        UiInput.Click(window, Rows<Button>(window, "RenameWorkspaceButton")[0]);
        var box = Rows<TextBox>(window, "WorkspaceRenameBox")[0];
        await UiWait.UntilAsync(() => box.IsFocused);

        Rows<Button>(window, "SuggestNamesButton")[0].Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await page.Suggesting;
        await UiWait.UntilAsync(() => Rows<Button>(window, "SuggestionChip") is [{ IsFocused: true }, ..]);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(row.IsRenaming);
        await UiWait.UntilAsync(() => Rows<Button>(window, "RenameWorkspaceButton")[0].IsFocused);

        // The same from 重试 after a failure.
        row.BeginRenameCommand.Execute(null);
        await UiWait.UntilAsync(() => box.IsFocused);
        app.Client.WorkspaceNames.FailNext(GenerationErrorKind.Network);
        Rows<Button>(window, "SuggestNamesButton")[0].Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await page.Suggesting;
        await UiWait.UntilAsync(() => Rows<Button>(window, "RetrySuggestButton")[0].IsFocused);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(row.IsRenaming);
        await UiWait.UntilAsync(() => Rows<Button>(window, "RenameWorkspaceButton")[0].IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Esc_or_stop_ends_a_running_request_and_a_second_esc_ends_the_rename()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await NetworkScenario.SeedAsync(app);
        var generation = app.Services.GetRequiredService<GenerationCoordinator>();
        var page = await OpenPageAsync(app, window);
        var row = page.Rows[0];
        var gate = app.Client.WorkspaceNames.Hold();

        UiInput.Click(window, Rows<Button>(window, "RenameWorkspaceButton")[0]);
        var box = Rows<TextBox>(window, "WorkspaceRenameBox")[0];
        await UiWait.UntilAsync(() => box.IsFocused);
        UiInput.Click(window, Rows<Button>(window, "SuggestNamesButton")[0]);
        await UiWait.UntilAsync(() => app.Client.Requests.Any(FakeChatClient.IsWorkspaceNaming));
        window.UpdateLayout();
        Assert.True(row.IsSuggesting);
        Assert.True(Rows(window, "SuggestingLine")[0].IsEffectivelyVisible);
        Assert.False(Rows(window, "SuggestNamesButton")[0].IsEffectivelyEnabled);
        Assert.Equal(UiText.StopSuggestingName, AutomationProperties.GetName(Rows(window, "StopSuggestingButton")[0]));

        // Esc in the box stops the request only; the rename stays, with what was typed.
        box.Focus(NavigationMethod.Pointer);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(row.IsSuggesting);
        Assert.True(row.IsRenaming);
        await UiWait.UntilAsync(() => generation.UnfinishedCount == 0);
        window.UpdateLayout();
        Assert.False(Rows(window, "SuggestionPanel")[0].IsEffectivelyVisible);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(row.IsRenaming);

        // 停止 does the same.
        row.BeginRenameCommand.Execute(null);
        window.UpdateLayout();
        UiInput.Click(window, Rows<Button>(window, "SuggestNamesButton")[0]);
        await UiWait.UntilAsync(() => generation.UnfinishedCount == 1);
        window.UpdateLayout();
        UiInput.Click(window, Rows<Button>(window, "StopSuggestingButton")[0]);
        Assert.Equal(NameSuggestionState.Idle, row.SuggestionState);
        await UiWait.UntilAsync(() => generation.UnfinishedCount == 0);

        // Ending the rename (取消) stops it too, and leaving the page does; the late answer shows nowhere.
        UiInput.Click(window, Rows<Button>(window, "SuggestNamesButton")[0]);
        await UiWait.UntilAsync(() => generation.UnfinishedCount == 1);
        row.CancelRenameCommand.Execute(null);
        await UiWait.UntilAsync(() => generation.UnfinishedCount == 0);
        row.BeginRenameCommand.Execute(null);
        row.SuggestNamesCommand.Execute(null);
        await UiWait.UntilAsync(() => generation.UnfinishedCount == 1);
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
        await UiWait.UntilAsync(() => generation.UnfinishedCount == 0);
        Assert.Equal(NameSuggestionState.Idle, row.SuggestionState);
        gate.TrySetResult();
        await page.Suggesting;
        Assert.Empty(row.Suggestions);
        Assert.Equal(4, app.Client.Requests.Count(FakeChatClient.IsWorkspaceNaming));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_failure_says_why_and_offers_retry_and_a_missing_key_points_to_the_settings()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await NetworkScenario.SeedAsync(app);
        var page = await OpenPageAsync(app, window);
        var row = page.Rows[0];
        row.BeginRenameCommand.Execute(null);
        window.UpdateLayout();

        app.Client.WorkspaceNames.FailNext(GenerationErrorKind.Network);
        UiInput.Click(window, Rows<Button>(window, "SuggestNamesButton")[0]);
        await page.Suggesting;
        window.UpdateLayout();
        Assert.Equal(NameSuggestionState.Failed, row.SuggestionState);
        Assert.Equal(UiText.SuggestionsFailed(GenerationErrorKind.Network), Rows<Avalonia.Controls.TextBlock>(window, "SuggestionProblemText")[0].Text);
        Assert.True(Rows(window, "RetrySuggestButton")[0].IsEffectivelyVisible);
        Assert.False(Rows(window, "SuggestSettingsButton")[0].IsEffectivelyVisible);
        Assert.Empty(Rows(window, "SuggestionChip"));

        UiInput.Click(window, Rows<Button>(window, "RetrySuggestButton")[0]);
        await page.Suggesting;
        window.UpdateLayout();
        Assert.Equal(3, Rows(window, "SuggestionChip").Count);
        Assert.False(Rows(window, "SuggestionProblemLine")[0].IsEffectivelyVisible);

        // Nothing usable in the answer: said, with 重试.
        app.Client.WorkspaceNames.Replies.Enqueue("火车\n“火车”");
        row.SuggestNamesCommand.Execute(null);
        await page.Suggesting;
        Assert.Equal(UiText.SuggestionsNone, row.SuggestionProblem);
        Assert.True(row.SuggestionCanRetry);

        // The key went missing from the system store: the settings are where it is fixed.
        var settings = (await app.Services.GetRequiredService<ModelSettingsService>().GetAsync(CancellationToken.None))!;
        await app.Credentials.DeleteAsync(settings.CredentialRef, CancellationToken.None);
        row.SuggestNamesCommand.Execute(null);
        await page.Suggesting;
        window.UpdateLayout();
        Assert.Equal(UiText.SuggestionsFailed(GenerationErrorKind.NotConfigured), row.SuggestionProblem);
        Assert.True(Rows(window, "SuggestSettingsButton")[0].IsEffectivelyVisible);
        UiInput.Click(window, Rows<Button>(window, "SuggestSettingsButton")[0]);
        Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public async Task An_empty_workspace_cannot_ask_and_says_why()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("空的");
        await app.ConfigureModelAsync();
        var page = await OpenPageAsync(app, window);
        var row = page.Rows[0];
        row.BeginRenameCommand.Execute(null);
        window.UpdateLayout();

        var button = Rows<Button>(window, "SuggestNamesButton")[0];
        Assert.True(button.IsEffectivelyVisible);
        Assert.False(button.IsEffectivelyEnabled);
        Assert.Equal(UiText.SuggestNamesEmptyTip, ToolTip.GetTip(button));
        Assert.True(ToolTip.GetShowOnDisabled(button));
        Assert.Equal(UiText.SuggestNamesEmptyTip, AutomationProperties.GetHelpText(button));

        // The counts say why; the line about sending titles would say nothing true here.
        Assert.True(Rows(window, "WorkspaceRowCounts")[0].IsEffectivelyVisible);
        Assert.False(Rows(window, "SuggestNamesHint")[0].IsEffectivelyVisible);
        Assert.DoesNotContain(app.Client.Requests, FakeChatClient.IsWorkspaceNaming);
        window.Close();
    }

    [AvaloniaFact]
    public async Task While_storage_is_paused_nothing_is_sent_and_it_is_said()
    {
        await using var app = DesktopHarness.Create(s => s.AddSingleton(new StorageHealthOptions { RetryInterval = TimeSpan.FromHours(1) }));
        var window = app.ShowWindow(1100, 720);
        await NetworkScenario.SeedAsync(app);
        var page = await OpenPageAsync(app, window);
        var row = page.Rows[0];
        app.Services.GetRequiredService<StorageHealth>().Report(new StorageWriteException(StorageFailureKind.Full, "disk full", null));

        row.BeginRenameCommand.Execute(null);
        row.SuggestNamesCommand.Execute(null);
        await page.Suggesting;

        Assert.Equal(UiText.SuggestionsStoragePaused, row.SuggestionProblem);
        Assert.True(row.SuggestionCanRetry);
        Assert.DoesNotContain(app.Client.Requests, FakeChatClient.IsWorkspaceNaming);
        window.Close();
    }

    [AvaloniaFact]
    public async Task An_answer_that_comes_late_never_shows_after_its_request_was_stopped_replaced_or_its_rename_ended()
    {
        var late = new LateSuggestions();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<IWorkspaceNameSuggestions>(late));
        var window = app.ShowWindow(1100, 720);
        await NetworkScenario.SeedAsync(app);
        await WorkspaceSwitcherTests.AddAsync(app, "城市交通", "摄影史");
        var page = await OpenPageAsync(app, window);
        var (trains, city) = (page.Rows[0], page.Rows[1]);

        // Stopped, then asked again: only the second answer shows.
        trains.BeginRenameCommand.Execute(null);
        trains.SuggestNamesCommand.Execute(null);
        var first = page.Suggesting;
        trains.StopSuggestingNamesCommand.Execute(null);
        Assert.True(trains.SuggestNamesCommand.CanExecute(null));
        trains.SuggestNamesCommand.Execute(null);
        var second = page.Suggesting;
        late.Answer(0, "第一次的名字");
        await first;
        Assert.True(trains.IsSuggesting);
        Assert.Empty(trains.Suggestions);
        late.Answer(1, "第二次的名字");
        await second;
        Assert.Equal(["第二次的名字"], trains.Suggestions.Select(s => s.Name));

        // Asked, then another row's rename ends this one: the answer shows in neither row.
        trains.SuggestNamesCommand.Execute(null);
        var third = page.Suggesting;
        city.BeginRenameCommand.Execute(null);
        Assert.False(trains.IsRenaming);
        late.Answer(2, "不该出现");
        await third;
        Assert.Empty(trains.Suggestions);
        Assert.Empty(city.Suggestions);
        Assert.Equal(NameSuggestionState.Idle, trains.SuggestionState);
        Assert.Equal(NameSuggestionState.Idle, city.SuggestionState);

        // Asked, then the page is left: nothing shows on return.
        trains.BeginRenameCommand.Execute(null);
        trains.SuggestNamesCommand.Execute(null);
        var fourth = page.Suggesting;
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
        late.Answer(3, "也不该出现");
        await fourth;
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        Assert.Same(page, app.Workbench.CurrentPage);
        Assert.Empty(trains.Suggestions);
        Assert.Equal(NameSuggestionState.Idle, trains.SuggestionState);
        Assert.All(late.Tokens, t => Assert.True(t.IsCancellationRequested || t == late.Tokens[1]));
        window.Close();
    }

    /// <summary>A service that answers when told and ignores cancellation, as a slow one might answer after all.</summary>
    private sealed class LateSuggestions : IWorkspaceNameSuggestions
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource<NameSuggestions>> _pending = new();
        private int _calls;

        public List<CancellationToken> Tokens { get; } = [];

        public Task<NameSuggestions> SuggestAsync(WorkspaceId workspace, WorkspaceName current, CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            return Pending(Interlocked.Increment(ref _calls) - 1).Task;
        }

        public void Answer(int call, string name)
        {
            Assert.True(WorkspaceName.TryCreate(name, out var value, out _));
            Pending(call).TrySetResult(new NameSuggestions([value!]));
        }

        private TaskCompletionSource<NameSuggestions> Pending(int call) =>
            _pending.GetOrAdd(call, _ => new TaskCompletionSource<NameSuggestions>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    internal static async Task<WorkspacesViewModel> OpenPageAsync(DesktopHarness app, MainWindow window)
    {
        app.ShellPage.ManageWorkspacesCommand.Execute(null);
        var page = Assert.IsType<WorkspacesViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        window.UpdateLayout();
        return page;
    }

    private static List<Control> Rows(Window window, string name) => Rows<Control>(window, name);

    /// <summary>The named part of every row, in the rows' order.</summary>
    private static List<T> Rows<T>(Window window, string name)
        where T : Control
    {
        window.UpdateLayout();
        return [.. window.GetVisualDescendants().OfType<WorkspacesView>().Single().GetVisualDescendants().OfType<T>().Where(c => c.Name == name)];
    }
}
