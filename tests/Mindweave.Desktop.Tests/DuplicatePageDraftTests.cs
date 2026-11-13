using Avalonia.Headless.XUnit;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Health check 2026-11-12, item 9: opening A, then B, then A again leaves two pages of A in the back stack. They share
/// one stored draft; the older page, shown again by going back, must show that draft, not the text it read when it
/// first opened — otherwise the newer draft looks lost and the next keystroke overwrites it, or a sent question is
/// still in the box and can be sent twice.
/// </summary>
public class DuplicatePageDraftTests
{
    [AvaloniaFact]
    public async Task Going_back_to_an_older_page_of_the_same_conversation_shows_the_draft_typed_on_the_newer_one()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow();
        var (a, b) = await TwoConversationsAsync(app);
        var a1 = await OpenAsync(app, a);
        await OpenAsync(app, b);
        var a2 = await OpenAsync(app, a);
        Assert.NotSame(a1, a2);

        a2.Composer.Text = "在较新的页面里写下的长问题";
        app.Workbench.GoBack();
        app.Workbench.GoBack();

        Assert.Same(a1, app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => a1.Composer.Text == "在较新的页面里写下的长问题");
    }

    [AvaloniaFact]
    public async Task A_question_sent_from_the_newer_page_is_not_left_in_the_older_one()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow();
        var (a, b) = await TwoConversationsAsync(app);
        var a1 = await OpenAsync(app, a);
        a1.Composer.Text = "要发送的问题";
        await OpenAsync(app, b);
        var a2 = await OpenAsync(app, a);
        await UiWait.UntilAsync(() => a2.Composer.Text == "要发送的问题");

        await a2.Composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => a2.Messages.Count == 4 && !a2.Messages[3].IsInProgress);
        app.Workbench.GoBack();
        app.Workbench.GoBack();

        Assert.Same(a1, app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => a1.Composer.Text.Length == 0);
    }

    private static async Task<ConversationViewModel> OpenAsync(DesktopHarness app, ConversationId id)
    {
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(id)));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => page.Messages.Count >= 2);
        return page;
    }

    private static async Task<(ConversationId A, ConversationId B)> TwoConversationsAsync(DesktopHarness app)
    {
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        return (await StartAsync(app, "火车是怎么发明的？"), await StartAsync(app, "高铁为什么快？"));
    }

    private static async Task<ConversationId> StartAsync(DesktopHarness app, string question)
    {
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = question;
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && !page.Messages[1].IsInProgress);
        return page.Id;
    }
}
