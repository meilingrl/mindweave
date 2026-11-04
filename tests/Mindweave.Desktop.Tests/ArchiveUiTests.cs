using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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
        Assert.Equal([(root.Id, 0, true), (child.Id, 1, false)], archived.Items.Select(i => (i.Id, i.Depth, i.IsArchived)));
        window.UpdateLayout();
        Assert.NotNull(window.GetVisualDescendants().OfType<ArchivedView>().SingleOrDefault());

        // The view turns the level into indentation (the view model exposes only the number, L5).
        var rows = window.GetVisualDescendants().OfType<ArchivedView>().Single().GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("archived-row")).ToList();
        Assert.Equal([0, Views.TreeIndent.Step], rows.Select(r => r.Margin.Left));
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
    public async Task A_plain_delete_asks_inline_and_leaves_for_the_parent_and_one_with_branches_opens_the_dialog()
    {
        await using var app = DesktopHarness.Create();
        var root = await ConversationAsync(app);
        var child = await BranchAsync(app, root, "火车");
        root = await OpenAsync(app, root.Id);

        // The root takes its branch along: the window's dialog states the counts instead of the inline question.
        await root.DeleteCommand.ExecuteAsync(null);
        Assert.Null(root.Confirmation);
        var dialog = app.Root.ConversationDeletion;
        Assert.True(dialog.IsOpen);
        await dialog.Loading;
        Assert.Equal(UiText.ConversationDeleteContents(2, 1, 0), dialog.Contents);
        dialog.CancelCommand.Execute(null);

        child = await OpenAsync(app, child.Id);
        child.Composer.Text = "没发出去的草稿";
        await child.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(UiText.DeleteConfirm, child.Confirmation!.Message);
        Assert.False(dialog.IsOpen);
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

        // The root has no dependants now; deleting it is plain again and returns to the explore home.
        root = await OpenAsync(app, root.Id);
        await root.DeleteCommand.ExecuteAsync(null);
        await root.ConfirmCommand.ExecuteAsync(null);
        Assert.IsType<ExploreHomeViewModel>(app.Workbench.CurrentPage);
        Assert.False(app.Workbench.CanGoBack);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 0);
        Assert.Empty(app.Services.GetRequiredService<Application.Startup.IDatabaseLifecycle>().ListBackups());
    }

    [AvaloniaFact]
    public async Task An_archived_conversation_with_branches_opens_the_dialog_without_offering_to_archive_it()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var root = await ConversationAsync(app);
        await BranchAsync(app, root, "火车");
        root = await OpenAsync(app, root.Id);
        await root.ArchiveCommand.ExecuteAsync(null);
        Assert.True(root.IsArchived);

        app.ShellPage.Conversations.OpenArchivedCommand.Execute(null);
        var archived = Assert.IsType<ArchivedViewModel>(app.Workbench.CurrentPage);
        await archived.RefreshAsync();
        await archived.DeleteCommand.ExecuteAsync(archived.Items.Single(i => i.Id == root.Id));

        Assert.Null(archived.Confirmation);
        var dialog = app.Root.ConversationDeletion;
        await dialog.Loading;
        window.UpdateLayout();
        Assert.Equal(UiText.ConversationDeleteContents(2, 1, 0), dialog.Contents);
        Assert.False(dialog.CanArchive);
        Assert.False(Find<Button>(window, "ConversationDeleteArchive").IsEffectivelyVisible);
        await dialog.DeleteCommand.ExecuteAsync(null);
        Assert.False(dialog.IsOpen);
        await archived.RefreshAsync();
        Assert.Empty(archived.Items);
        Assert.IsType<ArchivedViewModel>(app.Workbench.CurrentPage);
        window.Close();
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

    [AvaloniaFact]
    public async Task Archiving_restoring_or_deleting_that_cannot_be_written_says_so_and_changes_nothing()
    {
        var failing = new FailingArchiveStore();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<IConversationArchiveStore>(sp => failing.Wrap(ActivatorUtilities.CreateInstance<Infrastructure.Sqlite.SqliteArchiveStore>(sp))));
        Open(app);
        var root = await ConversationAsync(app);

        failing.Fail = true;
        await root.ArchiveCommand.ExecuteAsync(null);
        Assert.Equal(UiText.ArchiveStorageFailed, root.ArchiveNotice);
        Assert.False(root.IsArchived);

        await root.DeleteCommand.ExecuteAsync(null);
        await root.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(UiText.ArchiveStorageFailed, root.ArchiveNotice);
        Assert.Same(root, app.Workbench.CurrentPage);

        // The refusal paused storage-dependent commits (§9.3, D1): while paused, archiving is refused even though the
        // store would take it; once storage is found writable again, it goes through.
        failing.Fail = false;
        var health = app.Services.GetRequiredService<Application.Storage.StorageHealth>();
        Assert.True(health.IsBlocked);
        await root.ArchiveCommand.ExecuteAsync(null);
        Assert.Equal(UiText.ArchiveStorageFailed, root.ArchiveNotice);
        Assert.False(root.IsArchived);
        await UiWait.UntilAsync(() => !health.IsBlocked);
        await root.ArchiveCommand.ExecuteAsync(null);
        Assert.True(root.IsArchived);
        failing.Fail = true;
        await root.RestoreAndContinueCommand.ExecuteAsync(null);
        Assert.Equal(UiText.ArchiveStorageFailed, root.ArchiveNotice);
        Assert.True(root.IsArchived);
        failing.Fail = false;
        await UiWait.UntilAsync(() => !health.IsBlocked);
        failing.Fail = true;

        app.ShellPage.Conversations.OpenArchivedCommand.Execute(null);
        var archived = Assert.IsType<ArchivedViewModel>(app.Workbench.CurrentPage);
        await archived.RefreshAsync();
        var item = Assert.Single(archived.Items);
        await archived.RestoreCommand.ExecuteAsync(item);
        Assert.Equal(UiText.ArchiveStorageFailed, archived.Notice);
        await archived.DeleteCommand.ExecuteAsync(item);
        await archived.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(UiText.ArchiveStorageFailed, archived.Notice);
        await archived.RefreshAsync();
        Assert.Single(archived.Items);
    }

    /// <summary>The real archive store, refusing writes as storage does while <see cref="Fail"/> is set.</summary>
    private sealed class FailingArchiveStore
    {
        public bool Fail { get; set; }

        public IConversationArchiveStore Wrap(IConversationArchiveStore inner) => new Store(this, inner);

        private sealed class Store(FailingArchiveStore owner, IConversationArchiveStore inner) : IConversationArchiveStore
        {
            public Task<ArchiveResult> ArchiveAsync(Core.Conversations.ConversationId conversation, DateTimeOffset at, CancellationToken cancellationToken) =>
                owner.Fail ? Refuse<ArchiveResult>() : inner.ArchiveAsync(conversation, at, cancellationToken);

            public Task<RestorePlan> PlanRestoreAsync(Core.Conversations.ConversationId conversation, CancellationToken cancellationToken) =>
                inner.PlanRestoreAsync(conversation, cancellationToken);

            public Task<IReadOnlyList<Core.Conversations.ConversationId>> RestoreAsync(Core.Conversations.ConversationId conversation, CancellationToken cancellationToken) =>
                owner.Fail ? Refuse<IReadOnlyList<Core.Conversations.ConversationId>>() : inner.RestoreAsync(conversation, cancellationToken);

            public Task<ConversationDeleteScope> InspectDeleteAsync(IReadOnlyCollection<Core.Conversations.ConversationId> selected, CancellationToken cancellationToken) =>
                inner.InspectDeleteAsync(selected, cancellationToken);

            public Task<ConversationDeleteResult> DeleteAsync(ConversationDeleteScope confirmed, CancellationToken cancellationToken) =>
                owner.Fail ? Refuse<ConversationDeleteResult>() : inner.DeleteAsync(confirmed, cancellationToken);

            public Task<IReadOnlySet<Core.Conversations.ConversationId>> ArchivedAmongAsync(IReadOnlyCollection<Core.Conversations.ConversationId> conversations, CancellationToken cancellationToken) =>
                inner.ArchivedAmongAsync(conversations, cancellationToken);

            private static Task<T> Refuse<T>() => Task.FromException<T>(new StorageWriteException(StorageFailureKind.Full, "disk full", null!));
        }
    }

    [AvaloniaFact]
    public async Task The_archived_delete_confirmation_opens_under_its_row_names_it_and_deletes_in_the_danger_tone()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var root = await ConversationAsync(app);
        var child = await BranchAsync(app, root, "火车");
        root = await OpenAsync(app, root.Id);
        await root.ArchiveCommand.ExecuteAsync(null);
        app.ShellPage.Conversations.OpenArchivedCommand.Execute(null);
        var archived = Assert.IsType<ArchivedViewModel>(app.Workbench.CurrentPage);
        await archived.RefreshAsync();
        var last = archived.Items.Single(i => i.Id == child.Id);

        await archived.DeleteCommand.ExecuteAsync(last);

        // A reload (any conversation change) rebuilds the rows; the confirmation stays with its row.
        await archived.RefreshAsync();
        last = archived.Items.Single(i => i.Id == child.Id);
        Assert.True(last.IsConfirming);
        Assert.Single(archived.Items, i => i.IsConfirming);
        window.UpdateLayout();

        // The message names the conversation; the confirm button says 删除, in the danger style and tone.
        Assert.Equal(UiText.DeleteConfirmFor(last.Title), archived.Confirmation!.Message);
        Assert.Contains(last.Title, archived.Confirmation.Message, StringComparison.Ordinal);
        var view = window.GetVisualDescendants().OfType<ArchivedView>().Single();
        var panels = view.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "ConfirmationPanel").ToList();
        var shown = Assert.Single(panels, p => p.IsEffectivelyVisible);
        Assert.Contains("error", shown.Classes);
        Assert.DoesNotContain("warning", shown.Classes);
        Assert.Equal(archived.Confirmation.Message, Find<TextBlock>(shown, "ConfirmationMessage").Text);
        var confirm = Find<Button>(shown, "ConfirmButton");
        Assert.Equal(UiText.Delete, confirm.Content);
        Assert.Contains("danger", confirm.Classes);

        // It opens right under the clicked row (the last one), not at the top of the page.
        var row = view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("archived-row")).Single(b => b.DataContext == last);
        var rowBottom = row.TranslatePoint(new Point(0, row.Bounds.Height), view)!.Value.Y;
        var panelTop = shown.TranslatePoint(default, view)!.Value.Y;
        Assert.InRange(panelTop - rowBottom, 0, 12);
        Assert.Same(last, shown.DataContext);

        // Cancelling closes it; a restore that asks first says 恢复 and keeps the warning tone.
        archived.CancelConfirmationCommand.Execute(null);
        window.UpdateLayout();
        Assert.All(view.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "ConfirmationPanel"), p => Assert.False(p.IsEffectivelyVisible));
        await archived.RestoreCommand.ExecuteAsync(last);
        window.UpdateLayout();
        shown = Assert.Single(view.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "ConfirmationPanel"), p => p.IsEffectivelyVisible);
        Assert.Same(last, shown.DataContext);
        Assert.Contains("warning", shown.Classes);
        Assert.Equal(UiText.Restore, Find<Button>(shown, "ConfirmButton").Content);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Restoring_another_row_directly_dismisses_a_pending_confirmation()
    {
        await using var app = DesktopHarness.Create();
        Open(app);
        var root = await ConversationAsync(app);
        var first = await BranchAsync(app, root, "火车");
        await first.ArchiveCommand.ExecuteAsync(null);
        root = await OpenAsync(app, root.Id);
        var second = await BranchAsync(app, root, "火车");
        await second.ArchiveCommand.ExecuteAsync(null);
        app.ShellPage.Conversations.OpenArchivedCommand.Execute(null);
        var archived = Assert.IsType<ArchivedViewModel>(app.Workbench.CurrentPage);
        await archived.RefreshAsync();

        await archived.DeleteCommand.ExecuteAsync(archived.Items.Single(i => i.Id == first.Id));
        Assert.NotNull(archived.Confirmation);

        // The second branch's parent is not archived, so it restores without asking; the stale question goes away.
        await archived.RestoreCommand.ExecuteAsync(archived.Items.Single(i => i.Id == second.Id));
        Assert.Null(archived.Confirmation);
        Assert.DoesNotContain(archived.Items, i => i.IsConfirming);
    }

    [AvaloniaFact]
    public async Task Clicking_anywhere_in_an_archived_row_but_its_actions_opens_the_conversation()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var root = await ConversationAsync(app);
        await root.ArchiveCommand.ExecuteAsync(null);
        app.ShellPage.Conversations.OpenArchivedCommand.Execute(null);
        var archived = Assert.IsType<ArchivedViewModel>(app.Workbench.CurrentPage);
        await archived.RefreshAsync();
        window.UpdateLayout();

        // The row lights up across its width, so a click in its empty middle opens it too.
        var view = window.GetVisualDescendants().OfType<ArchivedView>().Single();
        var row = view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("archived-row"));
        var middle = row.TranslatePoint(new Point(row.Bounds.Width * 0.6, row.Bounds.Height / 2), window)!.Value;
        window.MouseDown(middle, Avalonia.Input.MouseButton.Left);
        window.MouseUp(middle, Avalonia.Input.MouseButton.Left);

        var opened = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        Assert.Equal(root.Id, opened.Id);
        window.Close();
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
