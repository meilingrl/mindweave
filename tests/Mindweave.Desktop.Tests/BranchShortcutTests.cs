using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views.Conversations;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Health check 2026-11-12, a11y-2 (shortcut chosen 2026-11-13): a branch can be made from selected answer text with
/// ⌘/Ctrl+Shift+B; ⌘/Ctrl+B stays the sidebar's.
/// </summary>
public class BranchShortcutTests
{
    private static readonly RawInputModifiers Command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    [AvaloniaFact]
    public async Task The_shortcut_makes_a_branch_from_the_selected_answer_text()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.Client.Reply("蒸汽机车由斯蒂芬森改良，铁路随后在英国普及。");
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = "火车是怎么发明的？";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].CanBranch);
        await UiWait.UntilAsync(() => window.GetVisualDescendants().OfType<AnswerView>().Any(a => a.DataContext is MessageItemViewModel { CanBranch: true } && a.IsRendered));
        window.UpdateLayout();
        var answer = window.GetVisualDescendants().OfType<AnswerView>().Last(a => a.DataContext is MessageItemViewModel { IsAnswer: true });
        var block = SourceAnchors.Blocks(answer.Renderer)[0];
        block.SelectionStart = block.LayoutText.IndexOf("斯蒂芬森", StringComparison.Ordinal);
        block.SelectionEnd = block.SelectionStart + "斯蒂芬森".Length;
        block.Focus();

        window.KeyPress(Key.B, Command | RawInputModifiers.Shift, PhysicalKey.B, null);

        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel child && child.Id != page.Id);
        var child = (ConversationViewModel)app.Workbench.CurrentPage!;
        await UiWait.UntilAsync(() => child.SourceQuote is not null);
        Assert.Contains("斯蒂芬森", child.SourceQuote, StringComparison.Ordinal);
        Assert.Equal(UiText.CreateBranchTip, Avalonia.Controls.ToolTip.GetTip(answer.BranchButton));
    }

    [AvaloniaFact]
    public async Task Command_B_alone_still_toggles_the_sidebar()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        var collapsed = app.ShellPage.IsSidebarCollapsed;

        window.KeyPress(Key.B, Command, PhysicalKey.B, null);

        Assert.NotEqual(collapsed, app.ShellPage.IsSidebarCollapsed);
    }
}
