using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Testing;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// A conversation page off screen reads nothing when its conversation changes, and reads it once when it is shown again
/// (health check 2026-11-12, performance-5).
/// </summary>
public class HiddenPageTests
{
    [AvaloniaFact]
    public async Task A_page_in_the_back_stack_waits_and_reads_its_changes_when_shown_again()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var first = await AskAsync(app, "第一个问题");
        await AskAsync(app, "第二个问题");
        await UiWait.UntilAsync(() => !first.IsShown);
        var loads = first.Loads;
        var title = first.DisplayTitle;

        await app.Services.GetRequiredService<IConversations>().RenameAsync(first.Id, "改过的名字", CancellationToken.None);
        await UiWait.UntilAsync(() => first.IsStale);
        Assert.Equal(loads, first.Loads);
        Assert.Equal(title, first.DisplayTitle);

        while (!ReferenceEquals(app.Workbench.CurrentPage, first))
        {
            app.Workbench.GoBack();
        }

        await UiWait.UntilAsync(() => first.DisplayTitle == "改过的名字");
        Assert.False(first.IsStale);
        Assert.Equal(loads + 1, first.Loads);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_page_in_a_minimised_window_reads_its_changes_when_the_window_is_restored()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var page = await AskAsync(app, "一个问题");

        window.WindowState = WindowState.Minimized;
        await UiWait.UntilAsync(() => !page.IsShown);
        var loads = page.Loads;
        await app.Services.GetRequiredService<IConversations>().RenameAsync(page.Id, "改过的名字", CancellationToken.None);
        await UiWait.UntilAsync(() => page.IsStale);
        Assert.Equal(loads, page.Loads);

        window.WindowState = WindowState.Normal;
        await UiWait.UntilAsync(() => page.DisplayTitle == "改过的名字");
        Assert.False(page.IsStale);
        window.Close();
    }

    private static async Task<ConversationViewModel> AskAsync(DesktopHarness app, string question)
    {
        app.Client.Reply("答。");
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = question;
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => page.IsShown && page.Messages.Count == 2 && page.Messages[1] is { State: MessageState.Complete, IsLive: false });
        return page;
    }
}
