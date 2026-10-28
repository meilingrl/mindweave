using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Core.Branches;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using Mindweave.Desktop.Views.Pages;
using Mindweave.Testing;

namespace Mindweave.Desktop.Tests;

/// <summary>S6 archive, restore and delete as the reader sees them (requirements §9.5, D-021), through the real window.</summary>
public class ArchiveUiTests
{
    [AvaloniaFact]
    public async Task Archiving_hides_the_tree_from_the_sidebar_and_the_archived_page_restores_it()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var root = await ConversationAsync(app);
        var child = await BranchAsync(app, root, "火车");
        root = await OpenAsync(app, root.Id);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 2);

        await root.ArchiveCommand.ExecuteAsync(null);

        Assert.True(root.IsArchived);
        Assert.Equal(UiText.ArchivedReadOnly, root.ArchiveText);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 0 && app.ShellPage.Conversations.ArchivedCount == 1);
        Assert.Equal(UiText.ArchivedEntry(1), app.ShellPage.Conversations.ArchivedLabel);
        window.UpdateLayout();
        var view = window.GetVisualDescendants().OfType<ConversationView>().Single();
        Assert.True(Find<Control>(view, "ArchivedPanel").IsEffectivelyVisible);
        Assert.False(Find<Control>(view, "ComposerHost").IsEffectivelyVisible);
        Assert.False(Find<Control>(view, "SummarizeButton").IsEffectivelyVisible);
        Assert.True(Find<Control>(view, "RestoreAndSummarizeButton").IsEffectivelyVisible);
        Assert.All(root.Messages, m => Assert.False(m.CanBranch || m.CanRetry));
        Assert.True(Find<Control>(window.GetVisualDescendants().OfType<ShellView>().Single(), "ArchivedEntry").IsEffectivelyVisible);

        app.ShellPage.Conversations.OpenArchivedCommand.Execute(null);
        var archived = Assert.IsType<ArchivedViewModel>(app.Workbench.CurrentPage);
        await archived.Loaded;
        Assert.Equal([(root.Id, 0, true), (child.Id, 1, false)], archived.Items.Select(i => (i.Id, i.Indent.Left > 0 ? 1 : 0, i.IsArchived)));
        window.UpdateLayout();
        Assert.NotNull(window.GetVisualDescendants().OfType<ArchivedView>().SingleOrDefault());
        ViewTests.Capture(window, "08-archived");

        await archived.RestoreCommand.ExecuteAsync(archived.Items[0]);

        Assert.Null(archived.Confirmation);
        Assert.True(archived.IsEmpty);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 2 && !app.ShellPage.Conversations.HasArchived);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_branch_hidden_by_its_archived_parent_restores_its_path_after_saying_so()
    {
        await using var app = DesktopHarness.Create();
        var root = await ConversationAsync(app);
        var child = await BranchAsync(app, root, "火车");
        await app.Services.GetService<ConversationService>()!.ArchiveAsync(root.Id, CancellationToken.None);

        app.ShellPage.Conversations.OpenArchivedCommand.Execute(null);
        var archived = Assert.IsType<ArchivedViewModel>(app.Workbench.CurrentPage);
        await archived.RefreshAsync();
        archived.OpenCommand.Execute(archived.Items.Single(i => i.Id == child.Id));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        Assert.True(page.IsArchived);
        Assert.Equal(UiText.HiddenByAncestors([root.Title]), page.ArchiveText);

        await page.RestoreAndContinueCommand.ExecuteAsync(null);
        Assert.Equal(UiText.RestoreConfirm([root.Title], 0), page.Confirmation!.Message);
        Assert.True(page.IsArchived);
        await page.ConfirmCommand.ExecuteAsync(null);

        Assert.False(page.IsArchived);
        page.Composer.Text = "接着问";
        await page.Composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && !page.Messages[1].IsInProgress);
        Assert.Null(page.Composer.Rejection);
    }

    [AvaloniaFact]
    public async Task New_work_on_an_archived_page_is_refused_even_when_its_controls_are_bypassed()
    {
        await using var app = DesktopHarness.Create();
        var page = await ConversationAsync(app);
        await page.ArchiveCommand.ExecuteAsync(null);
        Assert.True(page.IsArchived);

        page.Composer.Text = "还能问吗";
        Assert.False(page.Composer.SendCommand.CanExecute(null));
        await page.RetryCommand.ExecuteAsync(page.Messages[1]);
        Assert.Equal(SendRejection.ConversationArchived, page.Composer.Rejection);
        await page.SummarizeCommand.ExecuteAsync(null);
        Assert.Equal(UiText.SummarizeRejected(SummarizeRejection.ConversationArchived), page.SummaryNotice);
        await page.CreateBranchAsync(new BranchSelection(page.Messages[1].Id, 0, "火车", "{\"v\":1}", 1, "test-profile"));
        Assert.Equal(UiText.BranchRejected(BranchRejection.ConversationArchived), page.BranchNotice);
        Assert.Equal(2, page.Messages.Count);
    }

    [AvaloniaFact]
    public async Task Deleting_asks_first_names_every_reason_when_refused_and_then_leaves_for_the_parent()
    {
        await using var app = DesktopHarness.Create();
        var root = await ConversationAsync(app);
        var child = await BranchAsync(app, root, "火车");
        root = await OpenAsync(app, root.Id);

        root.DeleteCommand.Execute(null);
        Assert.Equal(UiText.DeleteConfirm, root.Confirmation!.Message);
        await root.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(UiText.DeleteRefused(DeleteBlockers.HasBranches), root.ArchiveNotice);
        Assert.True(root.OfferArchive);

        child = await OpenAsync(app, child.Id);
        child.Composer.Text = "没发出去的草稿";
        child.DeleteCommand.Execute(null);
        await child.ConfirmCommand.ExecuteAsync(null);

        // Every page of the child left every stack; the parent is shown.
        var shown = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        Assert.Equal(root.Id, shown.Id);
        while (app.Workbench.CanGoBack)
        {
            Assert.False(app.Workbench.CurrentPage is ConversationViewModel { } p && p.Id == child.Id);
            app.Workbench.GoBack();
        }

        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 1);

        // The root has no dependants now; deleting it returns to the explore home.
        root = await OpenAsync(app, root.Id);
        root.DeleteCommand.Execute(null);
        await root.ConfirmCommand.ExecuteAsync(null);
        Assert.IsType<ExploreHomeViewModel>(app.Workbench.CurrentPage);
        Assert.False(app.Workbench.CanGoBack);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 0);
    }

    [AvaloniaFact]
    public async Task An_archived_source_opens_read_only_from_its_summary_and_restore_and_summarize_continues()
    {
        await using var app = DesktopHarness.Create();
        var page = await ConversationAsync(app);
        await page.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.HasSummary && page.RunningSummary is null);
        await page.ArchiveCommand.ExecuteAsync(null);

        app.ShellPage.ShowSectionCommand.Execute(MainSection.Network);
        var network = Assert.IsType<NetworkHomeViewModel>(app.Workbench.CurrentPage);
        await network.Loaded;
        await UiWait.UntilAsync(() => network.Items.Count == 1 && network.Items[0].Markers.Contains(UiText.SourceArchived, StringComparison.Ordinal));
        network.Select(network.Items[0].Node);
        Assert.Contains(UiText.SourceArchived, network.Selected!.Source, StringComparison.Ordinal);

        network.OpenCommand.Execute(network.Items[0].Node);
        var summary = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await summary.Loaded;
        summary.ReturnToConversationCommand.Execute(null);
        var source = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await source.Loaded;
        await UiWait.UntilAsync(() => source.IsArchived && source.Messages.Count == 2);

        await source.RestoreAndSummarizeCommand.ExecuteAsync(null);

        Assert.False(source.IsArchived);
        await UiWait.UntilAsync(() => source.SummaryMarkersText?.Contains(UiText.MarkerCandidate, StringComparison.Ordinal) == true);
        await UiWait.UntilAsync(() => !network.Items[0].Markers.Contains(UiText.SourceArchived, StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task A_separately_archived_branch_keeps_its_entry_under_the_answer_with_a_marker()
    {
        await using var app = DesktopHarness.Create();
        var root = await ConversationAsync(app);
        var child = await BranchAsync(app, root, "火车");
        await child.ArchiveCommand.ExecuteAsync(null);

        root = await OpenAsync(app, root.Id);
        await UiWait.UntilAsync(() => root.Messages[1].Branches.Count == 1 && root.Messages[1].Branches[0].Archived);
        Assert.Equal(UiText.Quoted("火车") + " · " + UiText.ArchivedMarker, root.Messages[1].Branches[0].Label);
        Assert.False(root.IsArchived);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 1);
    }

    [AvaloniaFact]
    public async Task The_archived_page_is_reopened_after_a_restart()
    {
        await using var app = DesktopHarness.Create();
        var page = await ConversationAsync(app);
        await page.ArchiveCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.HasArchived);
        app.ShellPage.Conversations.OpenArchivedCommand.Execute(null);

        await app.RestartAsync();

        var archived = Assert.IsType<ArchivedViewModel>(app.Workbench.CurrentPage);
        await archived.Loaded;
        Assert.Equal(page.Id, Assert.Single(archived.Items).Id);
    }

    private static MainWindow Open(DesktopHarness app)
    {
        var window = new MainWindow { DataContext = app.Root, Width = 1000, Height = 700 };
        window.Show();
        return window;
    }

    private static async Task<ConversationViewModel> ConversationAsync(DesktopHarness app)
    {
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = "火车是怎么发明的？";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].CanBranch);
        await UiWait.UntilAsync(() => page.Title == FakeChatClient.DefaultTitle);
        return page;
    }

    private static async Task<ConversationViewModel> BranchAsync(DesktopHarness app, ConversationViewModel parent, string quote)
    {
        await parent.CreateBranchAsync(new BranchSelection(parent.Messages[1].Id, 0, quote, "{\"v\":1}", 1, "test-profile"));
        var child = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        Assert.NotEqual(parent.Id, child.Id);
        await child.Loaded;
        return child;
    }

    private static async Task<ConversationViewModel> OpenAsync(DesktopHarness app, Core.Conversations.ConversationId id)
    {
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(id)));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        return page;
    }

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
}
