using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
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
/// 设置 › 工作区, renaming (2026-11-11): 建议名称 asks the model for names drawn from the workspace's conversations. The
/// first is offered dimly after the text in the box (user decision of 2026-11-11): Tab takes it, Tab again the next;
/// 更多建议 shows them all as chips. Nothing is saved until Enter or 保存. Stopping, failures, an empty workspace and late
/// answers.
/// </summary>
public class WorkspaceNameSuggestionUiTests
{
    [AvaloniaFact]
    public async Task Asking_offers_the_first_name_after_the_text_and_tab_takes_it_and_cycles_without_saving()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var page = await OpenPageAsync(app, window);
        var row = page.Rows[0];

        // Not renaming: the counts, and no 建议名称.
        Assert.True(Rows(window, "WorkspaceRowCounts")[0].IsEffectivelyVisible);
        Assert.False(Rows(window, "SuggestNamesHint")[0].IsEffectivelyVisible);
        Assert.False(Rows(window, "SuggestNamesButton")[0].IsEffectivelyVisible);

        UiInput.Click(window, Rows<Button>(window, "RenameWorkspaceButton")[0]);
        var box = Rows<GhostTextBox>(window, "WorkspaceRenameBox")[0];
        await UiWait.UntilAsync(() => box.IsFocused);
        await page.KeptNamesRead;
        window.UpdateLayout();

        // While renaming, the line under the box says what the button sends, before anything is sent; nothing is offered.
        var button = Rows<Button>(window, "SuggestNamesButton")[0];
        Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
        Assert.Equal(UiText.SuggestNamesName("火车"), AutomationProperties.GetName(button));
        Assert.Equal(UiText.SuggestNamesTip, AutomationProperties.GetHelpText(button));
        Assert.True(Rows(window, "SuggestNamesHint")[0].IsEffectivelyVisible);
        Assert.False(Rows(window, "WorkspaceRowCounts")[0].IsEffectivelyVisible);
        Assert.False(Rows(window, "SuggestionPanel")[0].IsEffectivelyVisible);
        Assert.Null(box.Offered);
        Assert.False(box.ShowsGhost);
        Assert.DoesNotContain(app.Client.Requests, FakeChatClient.IsWorkspaceNaming);

        UiInput.Click(window, button);
        await page.Suggesting;
        await UiWait.UntilAsync(() => box.IsFocused);
        window.UpdateLayout();

        // The first name is offered after the text, said in the box's help text; no chips unless asked; 换一批 now.
        Assert.Equal(NameSuggestionState.Shown, row.SuggestionState);
        Assert.Equal("蒸汽机车史", box.Offered);
        Assert.True(box.ShowsGhost);
        Assert.Equal(OfferHelp("蒸汽机车史"), AutomationProperties.GetHelpText(box));
        Assert.False(Rows(window, "SuggestionPanel")[0].IsEffectivelyVisible);
        Assert.True(Rows(window, "GhostNamesHintLine")[0].IsEffectivelyVisible);
        Assert.False(Rows(window, "SuggestNamesHint")[0].IsEffectivelyVisible);
        Assert.Equal(UiText.SuggestOtherNamesName("火车"), AutomationProperties.GetName(Rows(window, "SuggestNamesButton")[0]));
        Assert.Equal("火车", box.Text);
        var sent = Assert.Single(app.Client.Requests, FakeChatClient.IsWorkspaceNaming).Turns[1].Content;
        Assert.Contains("当前名称：火车", sent, StringComparison.Ordinal);

        // Tab takes it: in the box, the caret at the end, still editable; nothing saved. The next one is offered.
        Tab(window);
        Assert.True(box.IsFocused);
        Assert.Equal("蒸汽机车史", row.RenameText);
        Assert.Equal("蒸汽机车史".Length, box.CaretIndex);
        Assert.True(string.IsNullOrEmpty(box.SelectedText));
        Assert.Equal("铁路发展", box.Offered);
        Assert.Equal(OfferHelp("铁路发展"), AutomationProperties.GetHelpText(box));
        Assert.Equal("火车", app.Workbench.CurrentWorkspace!.Name.Value);

        // Tab again cycles through the three, wrapping.
        Tab(window);
        Assert.Equal("铁路发展", row.RenameText);
        Tab(window);
        Assert.Equal("火车与工业革命", row.RenameText);
        Tab(window);
        Assert.Equal("蒸汽机车史", row.RenameText);
        Assert.True(box.IsFocused);
        Assert.Equal("火车", (await app.Services.GetRequiredService<WorkspaceService>().ListAsync(CancellationToken.None)).Single().Name.Value);

        // Typing ends the offer for this rename: nothing drawn, the help text plain, and Tab moves on as usual.
        window.KeyTextInput("与铁路");
        Assert.Null(box.Offered);
        Assert.False(box.ShowsGhost);
        Assert.Null(AutomationProperties.GetHelpText(box));
        Tab(window);
        Assert.False(box.IsFocused);
        Assert.Equal("蒸汽机车史与铁路", row.RenameText);

        // Enter saves what the box says, and the names go with the rename.
        box.Focus(NavigationMethod.Pointer);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await page.Renaming;
        Assert.False(row.IsRenaming);
        Assert.Equal("蒸汽机车史与铁路", app.Workbench.CurrentWorkspace!.Name.Value);
        Assert.Empty(row.GhostNames);
        Assert.Equal(NameSuggestionState.Idle, row.SuggestionState);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Deleting_hides_the_offer_shift_tab_always_moves_back_and_tab_moves_on_when_nothing_is_offered()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var page = await OpenPageAsync(app, window);
        var row = page.Rows[0];
        row.BeginRenameCommand.Execute(null);
        var box = Rows<GhostTextBox>(window, "WorkspaceRenameBox")[0];
        await UiWait.UntilAsync(() => box.IsFocused);

        // Nothing offered: Tab leaves the box (to 更多建议's line is not there yet: the next stop after the box).
        Tab(window);
        Assert.False(box.IsFocused);
        box.Focus(NavigationMethod.Tab);

        row.SuggestNamesCommand.Execute(null);
        await page.Suggesting;
        await UiWait.UntilAsync(() => box.IsFocused && box.Offered == "蒸汽机车史");

        // Shift+Tab is never taken: it moves back to 保存.
        window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        await UiWait.UntilAsync(() => Rows<Button>(window, "SaveRenameWorkspaceButton")[0].IsFocused);
        Assert.Equal("火车", row.RenameText);

        // Back in the box, Tab takes a name; deleting a character ends the offer.
        box.Focus(NavigationMethod.Pointer);
        box.CaretIndex = box.Text!.Length;
        Tab(window);
        Assert.Equal("蒸汽机车史", row.RenameText);
        window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
        Assert.Equal("蒸汽机车", row.RenameText);
        Assert.Null(box.Offered);
        Tab(window);
        Assert.False(box.IsFocused);
        Assert.Equal("蒸汽机车", row.RenameText);

        // 换一批 brings a new offer, even after typing; a failed one keeps the names offered before.
        app.Client.WorkspaceNames.Replies.Enqueue("蒸汽时代\n铁路网络");
        row.SuggestNamesCommand.Execute(null);
        await page.Suggesting;
        Assert.Equal(["蒸汽时代", "铁路网络"], row.GhostNames);
        Assert.Equal("蒸汽时代", box.Offered);
        app.Client.WorkspaceNames.FailNext(GenerationErrorKind.Network);
        row.SuggestNamesCommand.Execute(null);
        await page.Suggesting;
        Assert.Equal(["蒸汽时代", "铁路网络"], row.GhostNames);
        Assert.Equal(UiText.SuggestionsFailed(GenerationErrorKind.Network), row.SuggestionProblem);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_offer_is_drawn_after_the_text_never_over_it_and_trimmed_to_the_box()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var page = await OpenPageAsync(app, window);
        var row = page.Rows[0];
        row.BeginRenameCommand.Execute(null);
        var box = Rows<GhostTextBox>(window, "WorkspaceRenameBox")[0];
        await UiWait.UntilAsync(() => box.IsFocused);

        app.Client.WorkspaceNames.Replies.Enqueue("蒸汽机车\n" + new string('长', 19));
        row.SuggestNamesCommand.Execute(null);
        await page.Suggesting;
        await UiWait.UntilAsync(() => box.IsFocused);
        window.UpdateLayout();
        var place = box.Place()!;
        Assert.True(place.Ghost.Left >= place.Text.Right + 4, $"{place.Ghost} after {place.Text}");
        Assert.True(place.Key.Left >= place.Ghost.Right);
        Assert.True(place.Key.Right <= place.View.Right + 0.5, $"{place.Key} inside {place.View}");
        Assert.False(place.Trimmed);

        // A long name is trimmed to what fits after the text; it still ends inside the box.
        Tab(window);
        window.UpdateLayout();
        Assert.Equal(new string('长', 19), box.Offered);
        row.RenameText = string.Concat(Enumerable.Repeat("很长的名字", 3));
        row.SetNames([new string('长', 19)]);
        window.UpdateLayout();
        place = box.Place()!;
        Assert.True(place.Trimmed);
        Assert.True(place.Ghost.Left >= place.Text.Right + 4);
        Assert.True(place.Key.Right <= place.View.Right + 0.5);

        // Without room after the text nothing is drawn, so Tab takes nothing either (review 2026-11-12): the help text is
        // plain and Tab moves on as usual instead of silently replacing a long name with an offer nobody can see.
        var longName = string.Concat(Enumerable.Repeat("很长的名字", 12));
        row.RenameText = longName;
        row.SetNames(["火车史"]);
        window.UpdateLayout();
        Assert.Equal("火车史", box.Offered);
        Assert.Null(box.Place());
        Assert.Null(AutomationProperties.GetHelpText(box));
        Tab(window);
        Assert.False(box.IsFocused);
        Assert.Equal(longName, row.RenameText);
        box.Focus(NavigationMethod.Pointer);

        // Nor while an input method is composing (pinyin shown in the box, not yet text): nothing drawn or announced.
        row.RenameText = "火车";
        row.SetNames(["火车史"]);
        window.UpdateLayout();
        Assert.True(box.ShowsGhost);
        var presenter = box.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().Single();
        presenter.SetCurrentValue(Avalonia.Controls.Presenters.TextPresenter.PreeditTextProperty, "huoche");
        window.UpdateLayout();
        Assert.False(box.ShowsGhost);
        Assert.Null(AutomationProperties.GetHelpText(box));
        presenter.SetCurrentValue(Avalonia.Controls.Presenters.TextPresenter.PreeditTextProperty, null);
        window.UpdateLayout();
        Assert.True(box.ShowsGhost);
        Assert.Equal(OfferHelp("火车史"), AutomationProperties.GetHelpText(box));

        // Without the focus nothing is drawn; the help text still says it (it is read when the box gets the focus).
        row.RenameText = "火车";
        row.SetNames(["火车史"]);
        window.UpdateLayout();
        Assert.NotNull(box.Place());
        Rows<Button>(window, "SaveRenameWorkspaceButton")[0].Focus(NavigationMethod.Tab);
        window.UpdateLayout();
        Assert.Null(box.Place());
        Assert.Equal(OfferHelp("火车史"), AutomationProperties.GetHelpText(box));
        window.Close();
    }

    [AvaloniaFact]
    public async Task More_suggestions_shows_every_name_as_a_chip_and_picking_one_fills_the_box()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var page = await OpenPageAsync(app, window);
        var row = page.Rows[0];
        UiInput.Click(window, Rows<Button>(window, "RenameWorkspaceButton")[0]);
        var box = Rows<GhostTextBox>(window, "WorkspaceRenameBox")[0];
        await UiWait.UntilAsync(() => box.IsFocused);
        UiInput.Click(window, Rows<Button>(window, "SuggestNamesButton")[0]);
        await page.Suggesting;
        window.UpdateLayout();

        var toggle = Rows<Button>(window, "NameChipsToggle")[0];
        Assert.Equal(UiText.ShowNameChipsName, AutomationProperties.GetName(toggle));
        UiInput.Click(window, toggle);
        window.UpdateLayout();
        Assert.True(Rows(window, "SuggestionPanel")[0].IsEffectivelyVisible);
        Assert.True(Rows(window, "SuggestionsCaption")[0].IsEffectivelyVisible);
        Assert.Equal(UiText.HideNameChipsName, AutomationProperties.GetName(Rows<Button>(window, "NameChipsToggle")[0]));
        var chips = Rows<Button>(window, "SuggestionChip");
        Assert.Equal(["蒸汽机车史", "铁路发展", "火车与工业革命"], chips.Select(c => c.Content as string));
        Assert.Equal(UiText.SuggestionChipName("铁路发展"), AutomationProperties.GetName(chips[1]));

        // Picking fills the box, still editable, and saves nothing; the focus is back in the box after the name.
        UiInput.Click(window, chips[1]);
        Assert.True(row.IsRenaming);
        Assert.Equal("铁路发展", row.RenameText);
        await UiWait.UntilAsync(() => box.IsFocused && box.CaretIndex == "铁路发展".Length && string.IsNullOrEmpty(box.SelectedText));
        Assert.Equal("火车", row.Name);
        Assert.Equal(3, Rows<Button>(window, "SuggestionChip").Count);

        // From the keyboard: a chip, Tab to the next, Enter picks it.
        Rows<Button>(window, "SuggestionChip")[0].Focus(NavigationMethod.Tab);
        Tab(window);
        await UiWait.UntilAsync(() => Rows<Button>(window, "SuggestionChip")[1].IsFocused);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal("铁路发展", row.RenameText);
        await UiWait.UntilAsync(() => box.IsFocused);

        // Folded again; a new rename starts folded.
        UiInput.Click(window, Rows<Button>(window, "NameChipsToggle")[0]);
        window.UpdateLayout();
        Assert.False(Rows(window, "SuggestionPanel")[0].IsEffectivelyVisible);
        box.Focus(NavigationMethod.Pointer);
        box.CaretIndex = box.Text!.Length;
        window.KeyTextInput("史");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await page.Renaming;
        Assert.Equal("铁路发展史", app.Workbench.CurrentWorkspace!.Name.Value);
        Assert.False(row.ShowsChips);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Names_kept_from_before_are_offered_at_once_and_a_new_batch_replaces_them()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var workspace = app.Workbench.CurrentWorkspace!;
        var cache = app.Services.GetRequiredService<IWorkspaceNameCache>();
        await cache.SetAsync(workspace.Id, new PreparedNames([Name("火车的演进"), Name("火车"), Name("铁路技术史")], DateTimeOffset.UtcNow, 4), CancellationToken.None);
        var page = await OpenPageAsync(app, window);
        var row = page.Rows[0];

        UiInput.Click(window, Rows<Button>(window, "RenameWorkspaceButton")[0]);
        var box = Rows<GhostTextBox>(window, "WorkspaceRenameBox")[0];
        await page.KeptNamesRead;
        await UiWait.UntilAsync(() => box.IsFocused && box.Offered is not null);
        window.UpdateLayout();

        // Offered without asking; the current name is never among them; the button asks for another batch.
        Assert.Equal(["火车的演进", "铁路技术史"], row.GhostNames);
        Assert.Equal("火车的演进", box.Offered);
        Assert.True(box.ShowsGhost);
        Assert.DoesNotContain(app.Client.Requests, FakeChatClient.IsWorkspaceNaming);
        Assert.Equal(UiText.SuggestOtherNames, row.SuggestLabel);
        Assert.Equal(UiText.SuggestOtherNamesTip, AutomationProperties.GetHelpText(Rows<Button>(window, "SuggestNamesButton")[0]));

        // 换一批: a request, and the new names replace the kept ones (and are kept in their place).
        app.Client.WorkspaceNames.Replies.Enqueue("蒸汽与钢轨\n轨道上的世纪");
        UiInput.Click(window, Rows<Button>(window, "SuggestNamesButton")[0]);
        await page.Suggesting;
        Assert.Equal(["蒸汽与钢轨", "轨道上的世纪"], row.GhostNames);
        await UiWait.UntilAsync(() => box.Offered == "蒸汽与钢轨");
        Assert.Equal(["蒸汽与钢轨", "轨道上的世纪"], (await cache.GetAsync(workspace.Id, null, CancellationToken.None))!.Names.Select(n => n.Value));

        // Typed before the kept names were read: they are not offered over what was typed.
        row.CancelRenameCommand.Execute(null);
        row.BeginRenameCommand.Execute(null);
        row.RenameText = "我自己起的";
        await page.KeptNamesRead;
        Assert.Empty(row.GhostNames);
        window.Close();
    }

    private static readonly CompositeFormat OfferFormat = CompositeFormat.Parse(UiText.GhostNameHelpFormat);

    private static string OfferHelp(string name) => string.Format(CultureInfo.CurrentCulture, OfferFormat, name);

    private static void Tab(Window window) => window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);

    private static WorkspaceName Name(string raw)
    {
        Assert.True(WorkspaceName.TryCreate(raw, out var name, out _));
        return name!;
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
        await SummaryNetworkScenario.SeedAsync(app);
        var page = await OpenPageAsync(app, window);
        var row = page.Rows[0];
        UiInput.Click(window, Rows<Button>(window, "RenameWorkspaceButton")[0]);
        var box = Rows<TextBox>(window, "WorkspaceRenameBox")[0];
        await UiWait.UntilAsync(() => box.IsFocused);

        // With 更多建议 open, the keyboard follows new names to the first chip.
        row.SuggestNamesCommand.Execute(null);
        await page.Suggesting;
        row.ToggleChipsCommand.Execute(null);
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
        await SummaryNetworkScenario.SeedAsync(app);
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
        await SummaryNetworkScenario.SeedAsync(app);
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
        Assert.Empty(row.GhostNames);

        UiInput.Click(window, Rows<Button>(window, "RetrySuggestButton")[0]);
        await page.Suggesting;
        window.UpdateLayout();
        Assert.Equal(3, row.GhostNames.Count);
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
        await SummaryNetworkScenario.SeedAsync(app);
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
        await SummaryNetworkScenario.SeedAsync(app);
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
