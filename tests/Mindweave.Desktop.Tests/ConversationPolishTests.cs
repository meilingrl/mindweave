using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Mindweave.Application.Conversations;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task A_delete_refused_for_running_work_names_it_and_the_toolbar_keeps_archive()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var root = await ConversationAsync(app);
        root = await OpenAsync(app, root.Id);
        window.UpdateLayout();
        var view = CurrentView(window);
        await root.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(UiText.DeleteConfirm, root.Confirmation!.Message);

        // An answer starts between the question and the press: the delete is refused and says which conversation runs.
        var held = app.Client.Hold("半句");
        root.Composer.Text = "再问一句";
        await root.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await root.ConfirmCommand.ExecuteAsync(null);
        window.UpdateLayout();

        Assert.NotNull(root.ArchiveNotice);
        Assert.Contains(root.DisplayTitle, root.ArchiveNotice, StringComparison.Ordinal);
        Assert.True(Find<Control>(view, "ArchiveNoticePanel").IsEffectivelyVisible);
        Assert.True(Find<Button>(view, "ArchiveButton").IsEffectivelyVisible);
        Assert.Same(root, app.Workbench.CurrentPage);
        held.Finish();
        await UiWait.UntilAsync(() => root.RunningAnswer is null);
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
        Assert.Equal([0, TreeIndent.Step], rows.Select(r => r.Margin.Left));
        // The branch glyph (the row also holds the 选择 mode's check box, hidden here).
        static PathIcon Glyph(Button row) => row.GetVisualDescendants().OfType<PathIcon>().Single(p => p.FindAncestorOfType<Border>() is not { Name: "RowCheck" });
        Assert.False(Glyph(rows[0]).IsVisible);
        Assert.True(Glyph(rows[1]).IsEffectivelyVisible);
        Assert.True(rows[1].GetVisualDescendants().OfType<ActivitySpinner>().Single().IsEffectivelyVisible);
        Assert.Equal(UiText.MarkerGenerating, items[1].MarkerHint);

        held.Finish("。");
        await UiWait.UntilAsync(() => child.Messages.Count == 2 && child.Messages[1].State == MessageState.Complete);
        app.Client.Fail(GenerationErrorKind.Network, "connection reset", "半句");
        child.Composer.Text = "再继续";
        await child.Composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => items.Single(i => i.Id == child.Id).NeedsAttention);
        window.UpdateLayout();
        Assert.True(rows[1].GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("attention-badge")).IsEffectivelyVisible);
        Assert.False(rows[1].GetVisualDescendants().OfType<ActivitySpinner>().Single().IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_notice_button_label_takes_the_button_colour_not_the_notice_text_colour()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var root = await ConversationAsync(app);
        root = await OpenAsync(app, root.Id);
        await root.DeleteCommand.ExecuteAsync(null);
        window.UpdateLayout();
        var view = CurrentView(window);

        // A quiet action in a notice (the delete question's 取消): its label in the link colour, the notice's own text stays primary.
        var panel = Find<Border>(view, "ConfirmationPanel");
        var cancel = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("quiet"));
        Assert.Equal(Brush(cancel, "LinkText"), Colour(cancel.GetVisualDescendants().OfType<TextBlock>().Single().Foreground));
        Assert.Equal(Brush(cancel, "PrimaryText"), Colour(panel.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == UiText.DeleteConfirm).Foreground));
        root.CancelConfirmationCommand.Execute(null);

        // A primary button in a notice: its label is the on-accent colour, not the notice's text colour.
        await root.ArchiveCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => root.IsArchived);
        window.UpdateLayout();
        var restore = Find<Button>(view, "RestoreAndContinueButton");
        Assert.True(restore.IsEffectivelyVisible);
        Assert.Equal(Brush(restore, "OnAccentText"), Colour(restore.GetVisualDescendants().OfType<TextBlock>().Single().Foreground));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Renaming_keeps_one_primary_button_on_the_page()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var page = await ConversationAsync(app);
        page.Composer.Text = "下一个问题";
        page.BeginRenameCommand.Execute(null);
        window.UpdateLayout();
        var view = CurrentView(window);

        var save = Find<Button>(view, "SaveRenameButton");
        Assert.True(save.IsEffectivelyVisible);
        Assert.True(save.IsDefault);
        Assert.DoesNotContain("primary", save.Classes);
        Assert.Equal(["SendButton"], view.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("primary")).Select(b => b.Name));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_disabled_send_is_a_neutral_surface_in_both_themes()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var page = await ConversationAsync(app);
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            window.RequestedThemeVariant = theme;
            page.Composer.Text = string.Empty;
            window.UpdateLayout();
            var send = Find<Button>(CurrentView(window), "SendButton");
            Assert.False(send.IsEffectivelyEnabled);
            var presenter = Presenter(send);
            Assert.Equal(Brush(send, "DisabledButtonBackground"), Colour(presenter.Background));
            Assert.NotEqual(Brush(send, "AccentBrush"), Colour(presenter.Background));
            Assert.Equal(Brush(send, "MutedText"), Colour(presenter.Foreground));

            page.Composer.Text = "问";
            window.UpdateLayout();
            Assert.True(send.IsEffectivelyEnabled);
            Assert.Equal(Brush(send, "AccentBrush"), Colour(presenter.Background));
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_disabled_answer_action_is_visibly_fainter_than_an_enabled_one()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        await ConversationAsync(app);
        window.UpdateLayout();
        var regenerate = VisibleNamed<Button>(CurrentView(window), "RetryButton");
        Assert.True(regenerate.IsEffectivelyEnabled);
        var presenter = Presenter(regenerate);
        var resting = Colour(presenter.Foreground);
        Assert.Equal(Brush(regenerate, "QuietActionText"), resting);
        Assert.NotEqual(Brush(regenerate, "MutedText"), resting);
        Assert.Equal(1, presenter.Opacity);

        regenerate.IsEnabled = false;
        window.UpdateLayout();
        Assert.Equal(Brush(regenerate, "MutedText"), Colour(presenter.Foreground));
        Assert.True(presenter.Opacity <= 0.5);
        window.Close();
    }

    [AvaloniaFact]
    public void The_sidebar_tree_glyph_stands_out_from_the_sidebar_in_both_themes()
    {
        var app = Avalonia.Application.Current!;
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var guide = Assert.IsAssignableFrom<ISolidColorBrush>(app.FindResource(variant, "TreeGuide")).Color;
            var sidebar = Assert.IsAssignableFrom<ISolidColorBrush>(app.FindResource(variant, "SidebarBackground")).Color;
            Assert.True(Contrast(guide, sidebar) >= 1.5, $"{variant}: {guide} on {sidebar} is {Contrast(guide, sidebar):F2}:1");
        }
    }

    [AvaloniaFact]
    public async Task Only_a_delete_confirms_in_the_danger_colour()
    {
        await using var app = DesktopHarness.Create();
        var window = Open(app);
        var root = await ConversationAsync(app);
        await root.CreateBranchAsync(new BranchSelection(root.Messages[1].Id, 0, "火车", "{\"v\":1}", 1, "test-profile"));
        var child = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await child.Loaded;

        await child.DeleteCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Assert.True(child.Confirmation!.IsDestructive);
        var confirm = Find<Button>(CurrentView(window), "ConfirmButton");
        Assert.True(confirm.IsEffectivelyVisible);
        Assert.Contains("danger", confirm.Classes);
        Assert.Contains("secondary", confirm.Classes);
        Assert.Equal(Brush(confirm, "DangerText"), Colour(confirm.GetVisualDescendants().OfType<TextBlock>().Single().Foreground));
        child.CancelConfirmationCommand.Execute(null);

        // Restoring archived ancestors asks too, but it is not destructive: a plain secondary confirm.
        await app.Services.GetService<ConversationService>()!.ArchiveAsync(root.Id, CancellationToken.None);
        app.ShellPage.Conversations.OpenArchivedCommand.Execute(null);
        var archived = Assert.IsType<ArchivedViewModel>(app.Workbench.CurrentPage);
        await archived.RefreshAsync();
        var childId = child.Id;
        archived.OpenCommand.Execute(archived.Items.Single(i => i.Id == childId));
        child = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await child.Loaded;
        Assert.True(child.IsArchived);
        await child.RestoreAndContinueCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Assert.False(child.Confirmation!.IsDestructive);
        confirm = Find<Button>(CurrentView(window), "ConfirmButton");
        Assert.True(confirm.IsEffectivelyVisible);
        Assert.DoesNotContain("danger", confirm.Classes);
        Assert.Equal(Brush(confirm, "PrimaryText"), Colour(confirm.GetVisualDescendants().OfType<TextBlock>().Single().Foreground));
        window.Close();
    }

    private static ContentPresenter Presenter(Button button) =>
        button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");

    private static Color Brush(Control control, string key) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(control.FindResource(control.ActualThemeVariant, key)).Color;

    private static Color Colour(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte c) => c / 255.0 is var v && v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        static double Luminance(Color c) => (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
        var (high, low) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (high + 0.05) / (low + 0.05);
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
