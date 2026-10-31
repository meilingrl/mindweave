using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Mindweave.Application.Conversations;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using Mindweave.Testing;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// S7 round 2 (conversation page, branch page, sidebar): the controls moved or restyled still do what they did —
/// stop from the composer, retry from the failure notice, one storage notice, disabled quiet buttons, the archive
/// offer once, the breadcrumb, the sidebar markers.
/// </summary>
public class ConversationPolishTests
{
    [AvaloniaFact]
    public async Task Stop_sits_where_send_is_while_an_answer_runs_and_still_stops_it()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.Hold("半句");
        var page = await AskAsync(app, "问");
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].Body == "半句");
        window.UpdateLayout();
        var view = CurrentView(window);
        var composer = Find<ComposerView>(view, "ComposerHost");

        // Stop replaces send inside the composer; the answer shows a small in-answer mark, not a progress bar.
        var stop = Find<Button>(composer, "StopButton");
        Assert.True(stop.IsEffectivelyVisible);
        Assert.True(stop.IsEffectivelyEnabled);
        Assert.False(Find<Button>(composer, "SendButton").IsEffectivelyVisible);
        Assert.True(view.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "StreamingIndicator" && p.IsVisible).IsEffectivelyVisible);

        Click(window, stop);

        await UiWait.UntilAsync(() => page.Messages[1].State == MessageState.Stopped);
        window.UpdateLayout();
        Assert.Equal("半句", page.Messages[1].Body);
        Assert.False(stop.IsEffectivelyVisible);
        Assert.True(Find<Button>(composer, "SendButton").IsEffectivelyVisible);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<StackPanel>(), p => p.Name == "StreamingIndicator" && p.IsEffectivelyVisible);

        // A stopped answer is not a failure: its state and retry are in the quiet row under it.
        var retry = VisibleNamed<Button>(view, "RetryButton");
        Assert.Equal(UiText.Retry, page.Messages[1].RetryLabel);
        Assert.Equal("已停止", VisibleNamed<TextBlock>(view, "StateText").Text);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Border>(), b => b.Name == "ErrorNotice" && b.IsEffectivelyVisible);
        app.Client.Reply("完整的回答。");
        Click(window, retry);
        await UiWait.UntilAsync(() => page.Messages.Count == 3 && page.Messages[2].State == MessageState.Complete);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_failed_answer_shows_its_error_and_retry_together_and_the_retry_still_retries()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.Client.Fail(GenerationErrorKind.Network, "connection reset", "半句");
        var page = await AskAsync(app, "问");
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1] is { State: MessageState.Failed, CanRetry: true });
        window.UpdateLayout();
        var view = CurrentView(window);

        var notice = VisibleNamed<Border>(view, "ErrorNotice");
        Assert.Contains("error", notice.Classes);
        var retry = Find<Button>(notice, "ErrorRetryButton");
        Assert.True(retry.IsEffectivelyVisible);
        Assert.Equal(page.Messages[1].StateLabel, Find<TextBlock>(notice, "ErrorText").Text);

        // Together: the retry starts right after the error text, inside the same notice, not at the far edge of the page.
        var text = Find<TextBlock>(notice, "ErrorText");
        var textRight = text.TranslatePoint(new Point(text.DesiredSize.Width, 0), notice)!.Value.X;
        var retryLeft = retry.TranslatePoint(default, notice)!.Value.X;
        Assert.InRange(retryLeft - textRight, 0, 40);
        Assert.True(notice.Bounds.Width < view.Bounds.Width / 2);

        // The quiet row has nothing to repeat.
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), b => b.Name == "RetryButton" && b.IsEffectivelyVisible);

        app.Client.Reply("完整的回答。");
        Click(window, retry);
        await UiWait.UntilAsync(() => page.Messages.Count == 3 && page.Messages[2].State == MessageState.Complete);
        Assert.True(page.Messages[1].IsSuperseded);
        window.UpdateLayout();
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), b => b.Name == "ErrorRetryButton" && b.IsEffectivelyVisible);
        Assert.Equal(UiText.Regenerate, VisibleNamed<Button>(view, "RetryButton").DataContext is MessageItemViewModel m ? m.RetryLabel : null);
        window.Close();
    }

    [Fact]
    public void The_composer_leaves_the_storage_pause_to_a_page_that_already_shows_it()
    {
        static Task<SendOutcome> Never(string _) => throw new InvalidOperationException();

        var onPage = new Composer(Never) { HostShowsStoragePause = true, IsStoragePaused = true };
        Assert.Equal(UiText.SendRejected(SendRejection.StorageUnavailable), onPage.Notice);
        Assert.False(onPage.ShowNotice);

        // A new conversation has no page notice: the composer is the only place that says it.
        var alone = new Composer(Never) { IsStoragePaused = true };
        Assert.True(alone.ShowNotice);

        // Other refusals still show on the page.
        onPage.IsStoragePaused = false;
        onPage.Show(SendRejection.NotConfigured);
        Assert.True(onPage.ShowNotice);
        Assert.True(onPage.ShowSettingsLink);
    }

    [AvaloniaFact]
    public async Task Disabled_quiet_buttons_keep_the_quiet_look()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var page = await ConversationAsync(app);
        window.UpdateLayout();
        var summarize = Find<Button>(CurrentView(window), "SummarizeButton");
        Assert.True(summarize.IsEffectivelyEnabled);

        summarize.IsEnabled = false;
        window.UpdateLayout();
        var presenter = summarize.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
        var background = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background);
        Assert.Equal(0, background.Color.A);
        var muted = Assert.IsAssignableFrom<ISolidColorBrush>(summarize.FindResource(summarize.ActualThemeVariant, "MutedText"));
        Assert.Equal(muted.Color, Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Foreground).Color);
        Assert.NotNull(page);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_refused_delete_offers_archive_once_next_to_its_reason()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var root = await ConversationAsync(app);
        await root.CreateBranchAsync(new BranchSelection(root.Messages[1].Id, 0, "火车", "{\"v\":1}", 1, "test-profile"));
        root = await OpenAsync(app, root.Id);
        window.UpdateLayout();
        var view = CurrentView(window);
        Assert.True(Find<Button>(view, "ArchiveButton").IsEffectivelyVisible);

        root.DeleteCommand.Execute(null);
        await root.ConfirmCommand.ExecuteAsync(null);
        Assert.True(root.OfferArchive);
        window.UpdateLayout();

        Assert.False(Find<Button>(view, "ArchiveButton").IsEffectivelyVisible);
        var offer = Find<Button>(view, "OfferArchiveButton");
        Assert.True(offer.IsEffectivelyVisible);
        Assert.Contains(offer.GetVisualAncestors().OfType<Border>(), b => b.Name == "ArchiveNoticePanel");

        Click(window, offer);
        await UiWait.UntilAsync(() => root.IsArchived);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_breadcrumb_separates_ancestors_without_a_trailing_mark()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var root = await ConversationAsync(app);
        await root.CreateBranchAsync(new BranchSelection(root.Messages[1].Id, 0, "火车", "{\"v\":1}", 1, "test-profile"));
        var child = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await child.Loaded;
        app.Client.Reply("分支的回答。");
        child.Composer.Text = "再问";
        await child.Composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => child.Messages.Count == 2 && child.Messages[1].CanBranch);
        await child.CreateBranchAsync(new BranchSelection(child.Messages[1].Id, 0, "分支", "{\"v\":1}", 1, "test-profile"));
        var grandchild = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await grandchild.Loaded;
        window.UpdateLayout();

        Assert.Equal([true, false], grandchild.Lineage.Select(l => l.IsFirst));
        var path = Find<ItemsControl>(CurrentView(window), "LineagePath");
        var separators = path.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == "›").ToList();
        Assert.Equal(2, separators.Count);
        Assert.Single(separators, s => s.IsEffectivelyVisible);

        // The visible separator sits between the two ancestors, never after the last one.
        var crumbs = path.GetVisualDescendants().OfType<Button>().ToList();
        var mark = separators.Single(s => s.IsEffectivelyVisible).TranslatePoint(default, path)!.Value.X;
        Assert.True(mark > crumbs[0].TranslatePoint(default, path)!.Value.X && mark < crumbs[1].TranslatePoint(default, path)!.Value.X);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_sidebar_draws_branches_with_a_glyph_and_markers_as_small_badges()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var root = await ConversationAsync(app);
        await root.CreateBranchAsync(new BranchSelection(root.Messages[1].Id, 0, "火车", "{\"v\":1}", 1, "test-profile"));
        var child = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await child.Loaded;
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 2);
        var held = app.Client.Hold("写到一半");
        child.Composer.Text = "继续";
        await child.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var items = app.ShellPage.Conversations.Items;
        await UiWait.UntilAsync(() => items.Single(i => i.Id == child.Id).IsGenerating);
        window.UpdateLayout();

        Assert.Equal([false, true], items.Select(i => i.IsBranch));
        var list = Find<ItemsControl>(window.GetVisualDescendants().OfType<ShellView>().Single(), "ConversationList");
        var rows = list.GetVisualDescendants().OfType<Button>().ToList();
        Assert.False(rows[0].GetVisualDescendants().OfType<PathIcon>().Single().IsVisible);
        Assert.True(rows[1].GetVisualDescendants().OfType<PathIcon>().Single().IsEffectivelyVisible);
        Assert.True(rows[1].GetVisualDescendants().OfType<Ellipse>().Single(e => e.Classes.Contains("running-dot")).IsEffectivelyVisible);
        Assert.Equal(UiText.MarkerGenerating, items[1].MarkerHint);

        held.Finish("。");
        await UiWait.UntilAsync(() => child.Messages.Count == 2 && child.Messages[1].State == MessageState.Complete);
        app.Client.Fail(GenerationErrorKind.Network, "connection reset", "半句");
        child.Composer.Text = "再继续";
        await child.Composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => items.Single(i => i.Id == child.Id).NeedsAttention);
        window.UpdateLayout();
        Assert.True(rows[1].GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("attention-badge")).IsEffectivelyVisible);
        Assert.False(rows[1].GetVisualDescendants().OfType<Ellipse>().Single(e => e.Classes.Contains("running-dot")).IsVisible);
        window.Close();
    }

    /// <summary>A real pointer click at the button's centre, so the button is where the reader would click it.</summary>
    private static void Click(Window window, Button button)
    {
        button.BringIntoView();
        window.UpdateLayout();
        var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        window.UpdateLayout();
    }

    private static MainWindow Open(DesktopHarness app)
    {
        var window = new MainWindow { DataContext = app.Root, Width = 1000, Height = 700 };
        window.Show();
        return window;
    }

    private static async Task<ConversationViewModel> AskAsync(DesktopHarness app, string question)
    {
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = question;
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        return page;
    }

    private static async Task<ConversationViewModel> ConversationAsync(DesktopHarness app)
    {
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var page = await AskAsync(app, "火车是怎么发明的？");
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].CanBranch);
        await UiWait.UntilAsync(() => page.Title == FakeChatClient.DefaultTitle);
        return page;
    }

    private static async Task<ConversationViewModel> OpenAsync(DesktopHarness app, ConversationId id)
    {
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(id)));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        return page;
    }

    private static ConversationView CurrentView(Window window) => window.GetVisualDescendants().OfType<ConversationView>().Single();

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static T VisibleNamed<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name && c.IsEffectivelyVisible);
}
