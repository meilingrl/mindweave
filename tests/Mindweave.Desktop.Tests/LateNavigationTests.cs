using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Summaries;
using Mindweave.Core.Branches;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The reader goes elsewhere while a page's own work is under way (a send, a branch, deleting a summary's last version):
/// what that work opens goes where its page is, and never over what the reader went to (requirements §6.1).
/// </summary>
public class LateNavigationTests
{
    [AvaloniaFact]
    public async Task A_conversation_started_while_the_reader_went_to_another_section_takes_the_place_of_its_page()
    {
        await using var app = DesktopHarness.Create();
        var (_, sending) = await SendingAsync(app);

        app.Workbench.Navigate(new NavigationRequest(MainSection.Settings));
        await sending;

        Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        Assert.False(app.Workbench.CanGoBack);
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore));
        Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        app.Workbench.GoBack();
        Assert.IsType<ExploreHomeViewModel>(app.Workbench.CurrentPage);
    }

    [AvaloniaFact]
    public async Task A_conversation_started_while_the_reader_went_to_another_workspace_opens_in_its_own()
    {
        await using var app = DesktopHarness.Create();
        var (workspace, sending) = await SendingAsync(app, second: "摄影");
        var other = app.Workbench.Workspaces.Single(w => w.Id != workspace);

        app.Workbench.SwitchWorkspace(other.Id);
        var shown = app.Workbench.CurrentPage;
        await sending;

        Assert.Same(shown, app.Workbench.CurrentPage);
        app.Workbench.SwitchWorkspace(workspace);
        Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
    }

    [AvaloniaFact]
    public async Task A_conversation_started_while_another_page_was_opened_over_its_page_is_behind_that_page()
    {
        await using var app = DesktopHarness.Create();
        var (_, sending) = await SendingAsync(app);

        app.Workbench.Push(ArchivedRoute.Instance);
        await sending;

        Assert.IsType<ArchivedViewModel>(app.Workbench.CurrentPage);
        app.Workbench.GoBack();
        Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        app.Workbench.GoBack();
        Assert.IsType<ExploreHomeViewModel>(app.Workbench.CurrentPage);
    }

    [AvaloniaFact]
    public async Task A_conversation_started_while_the_reader_went_back_opens_over_nothing()
    {
        await using var app = DesktopHarness.Create();
        var (workspace, sending) = await SendingAsync(app);

        app.Workbench.GoBack();
        await sending;

        Assert.IsType<ExploreHomeViewModel>(app.Workbench.CurrentPage);
        Assert.Single(await app.Services.GetRequiredService<ConversationService>().ListAsync(workspace, CancellationToken.None));
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_branch_made_while_the_reader_went_elsewhere_does_not_open_over_it(bool workspace)
    {
        await using var app = DesktopHarness.Create();
        var page = await ConversationAsync(app);
        var answer = page.Messages.Last(m => !m.IsUser);

        var branching = page.CreateBranchAsync(new BranchSelection(answer.Id, 0, answer.Body[..2], "{\"v\":1}", 1, "test-profile"));
        var shown = GoElsewhere(app, workspace);
        await branching;

        Assert.Same(shown, app.Workbench.CurrentPage);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var origin = (await conversations.LoadAsync(page.Id, CancellationToken.None))!.Conversation.WorkspaceId;
        Assert.Equal(2, (await conversations.ListAsync(origin, CancellationToken.None)).Count);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_summary_deleted_while_the_reader_went_elsewhere_does_not_open_its_conversation_over_it(bool workspace)
    {
        await using var app = DesktopHarness.Create();
        var conversation = await ConversationAsync(app);
        var summaries = app.Services.GetRequiredService<SummaryService>();
        Assert.True((await summaries.SummarizeAsync(conversation.Id, CancellationToken.None)).Accepted);
        await UiWait.UntilAsync(() => summaries.ForConversationAsync(conversation.Id, CancellationToken.None).Result is not null);
        var node = (await summaries.ForConversationAsync(conversation.Id, CancellationToken.None))!.Node.Id;
        app.Workbench.Navigate(new NavigationRequest(MainSection.SummaryNetwork, new SummaryRoute(node)));
        var page = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => page.History.Count == 1);
        page.History[0].IsSelected = true;
        page.DeleteSelectedCommand.Execute(null);

        var deleting = page.ConfirmCommand.ExecuteAsync(null);
        var shown = GoElsewhere(app, workspace);
        await deleting;
        await UiWait.UntilAsync(() => summaries.ForConversationAsync(conversation.Id, CancellationToken.None).Result is null);

        Assert.Same(shown, app.Workbench.CurrentPage);
    }

    private static ViewModels.PageViewModel? GoElsewhere(DesktopHarness app, bool workspace)
    {
        if (workspace)
        {
            app.Workbench.SwitchWorkspace(app.Workbench.Workspaces.Single(w => w.Id != app.Workbench.CurrentWorkspace!.Id).Id);
        }
        else
        {
            app.Workbench.Navigate(new NavigationRequest(MainSection.Settings));
        }

        return app.Workbench.CurrentPage;
    }

    /// <summary>A conversation's page with one answer, and a second workspace.</summary>
    private static async Task<ConversationViewModel> ConversationAsync(DesktopHarness app)
    {
        var (_, sending) = await SendingAsync(app, second: "摄影");
        await sending;
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages.All(m => !m.IsInProgress));
        return page;
    }

    /// <summary>The first question of a new exploration, being sent: its store has not answered yet.</summary>
    private static async Task<(WorkspaceId Workspace, Task Sending)> SendingAsync(DesktopHarness app, string? second = null)
    {
        var workspace = await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        if (second is not null)
        {
            var other = await app.Services.GetRequiredService<Application.Workspaces.WorkspaceService>().CreateAsync(WorkspaceName.FromStored(second), CancellationToken.None);
            app.Workbench.AddWorkspace(other);
            app.Workbench.SwitchWorkspace(workspace.Id);
        }

        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, NewConversationRoute.Instance));
        var page = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        await page.DraftLoaded;
        page.Composer.Text = "火车是怎么发明的？";
        var sending = page.Composer.SendCommand.ExecuteAsync(null);
        Assert.False(sending.IsCompleted);
        return (workspace.Id, sending);
    }
}
