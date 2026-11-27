using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Exchange;
using Mindweave.Application.Import;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using Mindweave.Infrastructure.Share;
using static Mindweave.Desktop.Tests.UiTree;

using TextBlock = Avalonia.Controls.TextBlock;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Importing a structure-only share (docs/design/sharing-import.md §5, §9 scenarios 1 to 4) as the user does it: the entry
/// points, the dialog, and empty received conversations in the sidebar, on the Mindweave page and on their own page.
/// </summary>
public sealed class ImportUiTests : IDisposable
{
    internal static readonly ConversationId Root = ConversationId.Parse("0190f000-0000-7000-8000-000000000101");
    internal static readonly ConversationId Gauge = ConversationId.Parse("0190f000-0000-7000-8000-000000000102");
    internal static readonly ConversationId Unnamed = ConversationId.Parse("0190f000-0000-7000-8000-000000000103");

    private static readonly DateTimeOffset Day = new(2026, 11, 18, 11, 42, 30, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mindweave-import-ui", Guid.NewGuid().ToString("N"));

    public ImportUiTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    internal static ExchangeDocument Share => new(
        ExchangeDocument.StructureKind, "火车是怎么来的", Day.AddDays(7), "0.7.0",
        [new(Root, null, "火车起源", Day, 3, true), new(Gauge, Root, "标准轨距", Day.AddMinutes(9), 1, false), new(Unnamed, Gauge, null, Day.AddMinutes(30), 2, false)], []);

    private string Save(ExchangeDocument? document = null)
    {
        var path = Path.Combine(_dir, "share.html");
        File.WriteAllBytes(path, SharePage.Encode(document ?? Share));
        return path;
    }

    private static ImportViewModel Import(DesktopHarness app) => app.Root.Import;

    private static async Task<ImportViewModel> OpenAsync(DesktopHarness app, string path)
    {
        await Import(app).OpenAsync(path);
        return Import(app);
    }

    [AvaloniaFact]
    public async Task The_workspace_menu_asks_for_the_file_and_opens_the_dialog()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("历史");
        app.Shell.NextSharedFile = () => Save();
        app.ShellPage.IsWorkspaceMenuOpen = true;
        window.UpdateLayout();
        var entry = Find<Button>(window, "ImportEntry");
        Assert.True(entry.IsEffectivelyVisible);

        entry.Command!.Execute(null);
        await Import(app).Opening;

        Assert.False(app.ShellPage.IsWorkspaceMenuOpen);
        Assert.True(Find<Border>(window, "ImportDialog").IsEffectivelyVisible);
        Assert.Equal("火车是怎么来的", Import(app).ShareTitle);
        Assert.Equal(UiText.ImportKindStructure, Import(app).KindText);

        // Cancelled in the panel: nothing opens.
        Import(app).CloseCommand.Execute(null);
        app.Shell.NextSharedFile = () => null;
        app.ShellPage.ImportCommand.Execute(null);
        await Import(app).Opening;
        Assert.False(Import(app).IsOpen);
    }

    [AvaloniaFact]
    public async Task A_share_dropped_on_the_window_opens_the_dialog_and_other_files_are_not_taken()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("历史");
        window.UpdateLayout();
        var at = new Point(700, 200);

        Assert.Equal(DragDropEffects.None, Drag(window, at, RawDragEventType.DragEnter, "picture.png"));
        Assert.Equal(DragDropEffects.None, Drag(window, at, RawDragEventType.DragOver, "a.html", "b.html"));
        Drag(window, at, RawDragEventType.Drop, "picture.png");
        Assert.False(Import(app).IsOpen);

        var path = Save();
        Assert.Equal(DragDropEffects.Copy, Drag(window, at, RawDragEventType.DragOver, path));
        Drag(window, at, RawDragEventType.Drop, path);
        await Import(app).Opening;
        Assert.True(Import(app).IsOpen);

        // With a dialog open nothing more is taken.
        Assert.Equal(DragDropEffects.None, Drag(window, at, RawDragEventType.DragOver, path));
    }

    [AvaloniaFact]
    public async Task A_share_dropped_on_the_input_box_is_an_attachment_as_any_file()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("历史");
        app.ShellPage.Conversations.NewCommand.Execute(null);
        window.UpdateLayout();
        var box = Find<Border>(window, "ComposerBox");
        var at = box.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), window)!.Value;

        var path = Save();
        Assert.Equal(DragDropEffects.Copy, Drag(window, at, RawDragEventType.DragOver, path));
        Drag(window, at, RawDragEventType.Drop, path);

        var composer = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage).Composer;
        await UiWait.UntilAsync(() => composer.Attachments.Count == 1 && !composer.IsStoringAttachments);
        Assert.False(Import(app).IsOpen);
    }

    [AvaloniaFact]
    public async Task Nothing_is_taken_before_the_workbench_shows()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartAsync();
        Assert.IsType<OnboardingViewModel>(app.Root.Current);

        Drag(window, new Point(500, 300), RawDragEventType.Drop, Save());

        Assert.False(Import(app).IsOpen);
    }

    /// <summary>Acceptance 1: a new workspace named after the share, the first root open, every row muted in its place.</summary>
    [AvaloniaFact]
    public async Task Importing_into_a_new_workspace_opens_the_first_root_there()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        var first = await app.StartWithWorkspaceAsync("历史");
        var import = await OpenAsync(app, Save());

        Assert.Equal([UiText.ImportIntoNew("火车是怎么来的"), UiText.ImportIntoCurrent("历史")], import.Targets.Select(t => t.Text));
        Assert.True(import.Targets[0].IsChosen);
        Assert.Equal([UiText.ImportNew(3)], import.Counts);
        await import.ImportCommand.ExecuteAsync(null);

        Assert.False(import.IsOpen);
        Assert.Equal("火车是怎么来的", app.Workbench.CurrentWorkspace!.Name.Value);
        Assert.Equal(2, app.Workbench.Workspaces.Count);
        Assert.Equal(app.Workbench.CurrentWorkspace, app.ShellPage.SelectedWorkspace);
        Assert.Equal(Root, Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage).Id);
        await app.ShellPage.Conversations.Loaded;
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 3);
        Assert.Equal(
            [(Root, "火车起源", 0, true), (Gauge, "标准轨距", 1, true), (Unnamed, UiText.NewConversation, 2, true)],
            app.ShellPage.Conversations.Items.Select(i => (i.Id, i.Title, i.Depth, i.IsUnexplored)));
        Assert.Empty(await app.Services.GetRequiredService<ConversationService>().ListAsync(first.Id, CancellationToken.None));
    }

    [AvaloniaFact]
    public async Task Importing_into_the_current_workspace_adds_to_it()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        var page = await app.StartWithConversationAsync(waitForTitle: false);
        var import = await OpenAsync(app, Save());

        import.Targets[1].IsChosen = true;
        Assert.False(import.Targets[0].IsChosen);
        await import.ImportCommand.ExecuteAsync(null);

        Assert.Single(app.Workbench.Workspaces);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 4);
        Assert.False(app.ShellPage.Conversations.Items.Single(i => i.Id == page.Id).IsUnexplored);
    }

    /// <summary>Acceptance 3.</summary>
    [AvaloniaFact]
    public async Task The_same_file_again_says_so_and_offers_only_close()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("历史");
        await (await OpenAsync(app, Save())).ImportCommand.ExecuteAsync(null);

        var import = await OpenAsync(app, Save());
        window.UpdateLayout();

        Assert.True(import.IsAllHere);
        Assert.False(import.ImportCommand.CanExecute(null));
        Assert.False(Find<Button>(window, "ImportConfirm").IsEffectivelyVisible);
        Assert.False(Find<Control>(window, "ImportTargets").IsEffectivelyVisible);
        Assert.Equal(UiText.ImportAllHere, Find<TextBlock>(window, "ImportAllHere").Text);
        Assert.Equal(UiText.ShareClose, Find<Button>(window, "ImportCancel").Content);
    }

    [AvaloniaFact]
    public async Task The_workspace_that_holds_most_is_offered_first_and_another_one_is_named_as_the_reason_for_skipping()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        var first = await app.StartWithWorkspaceAsync("历史");
        await (await OpenAsync(app, Save())).ImportCommand.ExecuteAsync(null);
        app.Workbench.SwitchWorkspace(first.Id);
        var grown = Share with { Conversations = [.. Share.Conversations, new(ConversationId.New(), Root, "蒸汽机", Day.AddHours(1), 1, false), new(ConversationId.New(), null, "另一个", Day.AddHours(2), 1, false)] };

        var import = await OpenAsync(app, Save(grown));

        Assert.Equal([UiText.ImportIntoExisting("火车是怎么来的"), UiText.ImportIntoCurrent("历史"), UiText.ImportIntoNew("火车是怎么来的")], import.Targets.Select(t => t.Text));
        Assert.True(import.Targets[0].IsChosen);
        Assert.Equal([UiText.ImportNew(2), UiText.ImportHere(3)], import.Counts);

        import.Targets[1].IsChosen = true;
        Assert.Equal([UiText.ImportNew(1), UiText.ImportSkipped(4, "火车是怎么来的")], import.Counts);

        // A file whose conversations all hang off the other workspace brings nothing here.
        import.CloseCommand.Execute(null);
        import = await OpenAsync(app, Save(Share with { Conversations = [.. Share.Conversations, new(ConversationId.New(), Root, "蒸汽机", Day.AddHours(1), 1, false)] }));
        import.Targets[1].IsChosen = true;
        Assert.Equal([UiText.ImportSkipped(4, "火车是怎么来的")], import.Counts);
        Assert.False(import.ImportCommand.CanExecute(null));
        Assert.False(import.IsAllHere);
    }

    /// <summary>Acceptance 4: each refusal has its reason and only a way out.</summary>
    [AvaloniaFact]
    public async Task A_refused_file_says_why_and_nothing_changes()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("历史");
        var html = SharePage.Format(Share);
        var cases = new Dictionary<string, ImportRefusal>
        {
            ["<!doctype html>"] = ImportRefusal.NotAShare,
            [html.Replace("\"conversations\":[", "\"conversations\":[7,", StringComparison.Ordinal)] = ImportRefusal.Damaged,
            [html.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal)] = ImportRefusal.NewerFormat,
            [html.Replace("\"kind\":\"structure\"", "\"kind\":\"device\"", StringComparison.Ordinal)] = ImportRefusal.DeviceCopy,
        };

        foreach (var (text, refusal) in cases)
        {
            var path = Path.Combine(_dir, "refused.html");
            await File.WriteAllTextAsync(path, text, CancellationToken.None);
            var import = await OpenAsync(app, path);
            window.UpdateLayout();
            Assert.Equal(UiText.ImportRefused(refusal), import.Problem);
            Assert.False(import.ImportCommand.CanExecute(null));
            Assert.False(Find<Button>(window, "ImportConfirm").IsEffectivelyVisible);
            Assert.Equal("refused.html", import.ShareTitle);
            import.CloseCommand.Execute(null);
            Assert.False(import.IsOpen);
        }

        Assert.Equal(cases.Count, cases.Values.Select(UiText.ImportRefused).Distinct().Count());
        Assert.Single(app.Workbench.Workspaces);
        Assert.Empty(await app.Services.GetRequiredService<ConversationService>().ListAsync(app.Workbench.CurrentWorkspace!.Id, CancellationToken.None));
    }

    [AvaloniaFact]
    public async Task A_workspace_deleted_while_the_dialog_is_open_refuses_the_import_and_says_so()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("历史");
        var import = await OpenAsync(app, Save());
        import.Targets[1].IsChosen = true;
        await app.Services.GetRequiredService<IWorkspaceDeletion>().DeleteAsync(app.Workbench.CurrentWorkspace!.Id, CancellationToken.None);

        await import.ImportCommand.ExecuteAsync(null);

        Assert.True(import.IsOpen);
        Assert.Equal(UiText.ImportWorkspaceMissing, import.Problem);
    }

    [AvaloniaFact]
    public async Task An_empty_received_conversation_shows_its_input_box_and_nothing_else()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("历史");
        await (await OpenAsync(app, Save())).ImportCommand.ExecuteAsync(null);

        var page = await OpenConversationAsync(app, Gauge);
        window.UpdateLayout();

        Assert.True(page.IsUnexplored);
        Assert.Empty(page.Messages);
        Assert.False(Find<Border>(window, "BranchHeader").IsEffectivelyVisible);
        Assert.False(Find<Button>(window, "ReturnButton").IsEffectivelyVisible);
        Assert.Equal(UiText.AskInReceivedPlaceholder, page.Composer.Placeholder);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), b => b.PlaceholderText == UiText.AskInReceivedPlaceholder);
    }

    /// <summary>Acceptance 2 (decision I4).</summary>
    [AvaloniaFact]
    public async Task Asking_in_it_opens_the_users_own_branch_and_leaves_it_as_it_was()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("历史");
        await app.ConfigureModelAsync();
        await (await OpenAsync(app, Save())).ImportCommand.ExecuteAsync(null);
        var received = await OpenConversationAsync(app, Gauge);
        app.Client.Reply("1435 毫米。");
        received.Composer.Text = "标准轨距是多少？";

        await received.Composer.SendCommand.ExecuteAsync(null);

        var own = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await own.Loaded;
        Assert.NotEqual(Gauge, own.Id);
        await UiWait.UntilAsync(() => own.Messages.Count == 2 && own.RunningAnswer is null);
        Assert.Equal("1435 毫米。", own.Messages[1].Body);
        Assert.Equal(string.Empty, received.Composer.Text);
        window.UpdateLayout();

        // Where it sits is shown; there is no sentence to return to.
        Assert.True(Find<Border>(window, "BranchHeader").IsEffectivelyVisible);
        Assert.Equal(["火车起源", "标准轨距"], own.Lineage.Select(l => l.Title));
        Assert.False(Find<Button>(window, "ReturnButton").IsEffectivelyVisible);
        Assert.False(Find<TextBlock>(window, "QuoteText").IsEffectivelyVisible);

        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Any(i => i.Id == own.Id));
        var rows = app.ShellPage.Conversations.Items.ToDictionary(i => i.Id);
        Assert.Equal((2, false), (rows[own.Id].Depth, rows[own.Id].IsUnexplored));
        Assert.True(rows[Gauge].IsUnexplored);
        app.ShellPage.GoBackCommand.Execute(null);
        Assert.Empty((await OpenConversationAsync(app, Gauge)).Messages);
    }

    [AvaloniaFact]
    public async Task Both_views_draw_it_hollow_and_its_details_say_so()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("历史");
        await app.ConfigureModelAsync();
        await (await OpenAsync(app, Save())).ImportCommand.ExecuteAsync(null);
        var sent = await app.Services.GetRequiredService<ConversationService>().AskAsync(Gauge, "多宽？", CancellationToken.None);
        await UiWait.UntilAsync(() => !app.Services.GetRequiredService<ConversationService>().HasUnfinishedAnswer(sent.Conversation));
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);

        foreach (var timeline in new[] { false, true })
        {
            network.SetViewModeCommand.Execute(timeline ? Desktop.SummaryNetwork.SummaryNetworkViewMode.Timeline : Desktop.SummaryNetwork.SummaryNetworkViewMode.Spatial);
            window.UpdateLayout();
            Assert.Equal(4, network.Nodes.Count);
            var hollow = Find<SummaryNetworkCanvas>(window, "Canvas").PaintNodes().ToDictionary(p => p.Node.DisplayName, p => p.Hollow);
            Assert.True(hollow["火车起源"] && hollow["标准轨距"] && hollow[UiText.NewConversation]);
            Assert.Equal(1, hollow.Values.Count(h => !h));
        }

        network.Select(network.Nodes.Single(n => n.DisplayName == "标准轨距").Id);
        window.UpdateLayout();
        Assert.True(Find<Border>(window, "UnexploredBadge").IsEffectivelyVisible);
        Assert.StartsWith(UiText.ReceivedFrom("火车是怎么来的", DateTimeOffset.Now)[..12], Find<TextBlock>(window, "DetailReceived").Text, StringComparison.Ordinal);

        network.Select(network.Nodes.Single(n => !n.Unexplored).Id);
        window.UpdateLayout();
        Assert.False(Find<Border>(window, "UnexploredBadge").IsEffectivelyVisible);
        Assert.False(Find<TextBlock>(window, "DetailReceived").IsEffectivelyVisible);
        network.IsListView = true;
        await UiWait.UntilAsync(() => network.Items.Count == 4);
        Assert.Equal(3, network.Items.Count(i => i.Other.Contains(UiText.NotExplored, StringComparison.Ordinal)));
    }

    /// <summary>A workspace's page remembers which conversations are another workspace's; a deleted one can come back in its own.</summary>
    [AvaloniaFact]
    public async Task A_Mindweave_page_shows_what_is_imported_after_it_was_deleted_elsewhere()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithConversationAsync(waitForTitle: true);
        var first = app.Workbench.CurrentWorkspace!;
        var one = Share with { Conversations = [Share.Conversations[0]] };
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await (await OpenAsync(app, Save(one))).ImportCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => network.Knows(Root));
        var second = app.Workbench.CurrentWorkspace!.Id;
        app.Workbench.SwitchWorkspace(first.Id);
        Assert.Equal(WorkspaceDeleteStatus.Deleted, (await app.Services.GetRequiredService<IWorkspaceDeletion>().DeleteAsync(second, CancellationToken.None)).Status);
        app.Workbench.RemoveWorkspace(second, first.Id);

        var import = await OpenAsync(app, Save(one));
        import.Targets[1].IsChosen = true;
        await Task.Delay(300, CancellationToken.None); // Whatever the deletion still reloads is done.
        Assert.Single(network.Nodes);
        await import.ImportCommand.ExecuteAsync(null);

        await UiWait.UntilAsync(() => network.Nodes.Count == 2);
    }

    [AvaloniaFact]
    public async Task The_dialog_takes_the_keyboard_when_it_opens()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("历史");

        await OpenAsync(app, Save());
        window.UpdateLayout();
        await UiWait.UntilAsync(() => Find<Button>(window, "ImportConfirm").IsFocused);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(Import(app).IsOpen);

        await File.WriteAllTextAsync(Path.Combine(_dir, "other.html"), "<!doctype html>", CancellationToken.None);
        await OpenAsync(app, Path.Combine(_dir, "other.html"));
        window.UpdateLayout();
        await UiWait.UntilAsync(() => Find<Button>(window, "ImportCancel").IsFocused);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.False(Import(app).IsOpen);
    }

    private static async Task<ConversationViewModel> OpenConversationAsync(DesktopHarness app, ConversationId id)
    {
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(id)));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        Assert.Equal(id, page.Id);
        await page.Loaded;
        return page;
    }

    private static DragDropEffects Drag(Window window, Point at, RawDragEventType type, params string[] paths)
    {
        using var data = new DataTransfer();
        foreach (var path in paths)
        {
            data.Add(DataTransferItem.CreateFile(Dropped(path)));
        }

        // What the window's own handlers decided; the window itself is asked last.
        var effects = DragDropEffects.None;
        void Seen(object? sender, DragEventArgs e) => effects = e.DragEffects;
        window.AddHandler(DragDrop.DragEnterEvent, Seen, RoutingStrategies.Bubble, handledEventsToo: true);
        window.AddHandler(DragDrop.DragOverEvent, Seen, RoutingStrategies.Bubble, handledEventsToo: true);
        window.DragDrop(at, type, data, DragDropEffects.Copy);
        window.RemoveHandler(DragDrop.DragEnterEvent, Seen);
        window.RemoveHandler(DragDrop.DragOverEvent, Seen);
        return effects;
    }

    /// <summary>A file on this computer as a drop from the file manager carries it. Avalonia's own type, which no test may implement.</summary>
    private static IStorageItem Dropped(string path) => (IStorageItem)Activator.CreateInstance(
        typeof(IStorageItem).Assembly.GetType("Avalonia.Platform.Storage.FileIO.BclStorageFile", throwOnError: true)!,
        new FileInfo(Path.IsPathRooted(path) ? path : Path.Combine(Path.GetTempPath(), path)))!;
}
