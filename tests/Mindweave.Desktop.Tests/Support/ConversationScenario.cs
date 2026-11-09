using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Testing;

namespace Mindweave.Desktop.Tests;

/// <summary>Conversations started the way the reader starts them, shared by the desktop UI tests.</summary>
internal static class ConversationScenario
{
    /// <summary>
    /// Starts with workspace "火车" and a configured model, asks <paramref name="question"/> from a new conversation, and
    /// waits until the answer is stored (it can be branched from).
    /// </summary>
    /// <param name="app">The harness, not started yet.</param>
    /// <param name="waitForTitle">Also waits until the conversation has its generated title.</param>
    /// <param name="question">What is asked.</param>
    public static async Task<ConversationViewModel> StartWithConversationAsync(this DesktopHarness app, bool waitForTitle, string question = "火车是怎么发明的？")
    {
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = question;
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].CanBranch);
        if (waitForTitle)
        {
            await UiWait.UntilAsync(() => page.Title == FakeChatClient.DefaultTitle);
        }

        return page;
    }
}
