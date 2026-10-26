using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using LiveMarkdown.Avalonia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mindweave.Application.Navigation;
using Mindweave.Core.Branches;
using Mindweave.Core.Chat;
using Mindweave.Desktop;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.Views;
using Mindweave.Infrastructure.Storage;
using Xunit;
using F = Mindweave.Tests.GenerationCoordinatorTests.Fixture;

namespace Mindweave.Tests;

public sealed class BranchDesktopTests
{
    private const string Body = "重复词 **粗体** [链接](https://example.invalid) `代码`。\n\n第二段 重复词。\n\n最后一段。";
    private static MainViewModel Main(F f) => CompositionRoot.CreateMain(f.Store, f.Coordinator, new(f.Chat, f.Vault, f.Transport));
    private static ChatViewModel Chat(MainViewModel main) => Assert.IsType<ChatViewModel>(main.ActiveContent);
    private static async Task<(MainViewModel Main, ChatMessage Answer)> Start(F f)
    {
        var conversation = await f.ConversationAsync();
        var answer = await BranchStorageTests.Answer(f.Store, conversation.Id, "synthetic question", Body);
        var main = Main(f); await main.InitializeAsync();
        await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, conversation.Id));
        return (main, answer);
    }
    private static SourceSelection Capture(ChatMessage message)
    {
        var view = new MarkdownMessageView { MessageId = message.Id, Revision = message.Revision, Body = message.Body };
        var window = new Window { Content = view, Width = 700, Height = 500 }; window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        try
        {
            var block = MarkdownSelectionAdapter.Blocks(view.Renderer)[1]; block.SelectionStart = 0; block.SelectionEnd = block.LayoutText.Length;
            return view.CaptureSelection();
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task Nested_branches_double_click_source_return_history_and_reentry_use_distinct_read_states()
    {
        await using var f = await F.OpenAsync(); var (main, answer) = await Start(f);
        var a = Chat(main); var selectionA = Capture(answer);
        var aRead = new ReadingLocation(answer.Id, 0, 7, 1, MarkdownSelectionAdapter.RendererProfile);
        a.RecordReading(aRead); a.InputText = "A draft";
        await Task.WhenAll(main.CreateBranchAsync(selectionA), main.CreateBranchAsync(selectionA));
        Assert.Null(main.ErrorMessage); var b = Chat(main); Assert.NotEqual(a.ConversationId, b.ConversationId);
        Assert.Single(await f.Store.ListChildBranchesAsync(a.ConversationId));
        Assert.Equal(aRead, (await f.Store.LoadReadStateAsync(a.ConversationId))!.Location);
        var bAnswer = await BranchStorageTests.Answer(f.Store, b.ConversationId, "B question", Body);
        await main.NavigateAsync(new(MainArea.Settings)); await main.NavigateAsync(new(MainArea.Explore)); b = Chat(main);
        b.RecordReading(new(bAnswer.Id, 0, 11, 1, MarkdownSelectionAdapter.RendererProfile));
        await main.CreateBranchAsync(Capture(bAnswer)); var c = Chat(main);
        Assert.Equal(2, (await f.Store.ReadFrozenContextRangesAsync(c.ConversationId)).Count);
        Assert.Equal(2, main.Conversations.Single(i => i.Id == c.ConversationId).Depth);
        await main.ReturnToSourceAsync(c.ConversationId); b = Chat(main);
        Assert.Equal(bAnswer.Id, b.PendingSourceReveal!.Selection.MessageId);
        Assert.Equal(NavigationIntent.Open, b.Request.Intent); // Source reveal is one-time, never retained in history.
        b.ConsumeSourceReveal(); var bNew = new ReadingLocation(bAnswer.Id, 0, 26, 1, MarkdownSelectionAdapter.RendererProfile);
        b.RecordReading(bNew); b.InputText = "B latest draft";
        await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, a.ConversationId));
        Assert.Equal(aRead, Chat(main).ReadState.Location); Assert.Null(Chat(main).PendingSourceReveal);
        Assert.Equal("A draft", Chat(main).InputText);
        await main.GoBackAsync(); Assert.Equal(bNew, Chat(main).ReadState.Location); Assert.Null(Chat(main).PendingSourceReveal);
        await main.NavigateAsync(new(MainArea.Settings)); await main.NavigateAsync(new(MainArea.Explore));
        Assert.Equal(bNew, Chat(main).ReadState.Location); Assert.Null(Chat(main).PendingSourceReveal);
        await main.ReturnToSourceAsync(b.ConversationId); Assert.Equal(answer.Id, Chat(main).PendingSourceReveal!.Selection.MessageId);
        await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, b.ConversationId));
        Assert.Equal(bNew, Chat(main).ReadState.Location); Assert.Equal("B latest draft", Chat(main).InputText);
        main.DetachObservers();
    }
    [AvaloniaFact]
    public async Task Postaccept_draft_failure_keeps_one_child_and_global_gate_until_full_repair()
    {
        await using var f = await F.OpenAsync(); var (main, answer) = await Start(f); var a = Chat(main);
        a.InputText = "preserved parent draft";
        f.Sql("CREATE TRIGGER fail_draft BEFORE INSERT ON conversation_draft BEGIN SELECT RAISE(ABORT,'fixture'); END");
        // Existing acceptance creates a tombstone: also fail its update.
        f.Sql("CREATE TRIGGER fail_draft_update BEFORE UPDATE ON conversation_draft BEGIN SELECT RAISE(ABORT,'fixture'); END");
        var selection = Capture(answer); await main.CreateBranchAsync(selection);
        var child = Assert.Single(await f.Store.ListChildBranchesAsync(a.ConversationId));
        Assert.True(main.HasPersistenceFailure); Assert.True(f.Coordinator.HasPersistenceFailure);
        await main.CreateBranchAsync(selection); Assert.Single(await f.Store.ListChildBranchesAsync(a.ConversationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.SendAsync(a.ConversationId, "blocked"));
        await Assert.ThrowsAnyAsync<Exception>(() => main.RetryPersistenceAsync()); Assert.True(main.HasPersistenceFailure);
        f.Sql("DROP TRIGGER fail_draft"); f.Sql("DROP TRIGGER fail_draft_update");
        await main.RetryPersistenceAsync();
        Assert.False(main.HasPersistenceFailure); Assert.Equal(child.ChildConversationId, Chat(main).ConversationId);
        Assert.Equal("preserved parent draft", (await f.Chat.LoadDraftAsync(a.ConversationId)).Text);
        Assert.Single(await f.Store.ListChildBranchesAsync(a.ConversationId)); main.DetachObservers();
    }
    [AvaloniaFact]
    public async Task Invalid_selection_or_active_answer_refuses_without_closing_persistence_gate()
    {
        await using var f = await F.OpenAsync(); var (main, answer) = await Start(f); var a = Chat(main);
        await main.CreateBranchAsync(Capture(answer) with { MessageRevision = 99 });
        Assert.Empty(await f.Store.ListChildBranchesAsync(a.ConversationId)); Assert.False(main.HasPersistenceFailure);
        a.InputText = "active question"; await a.SendCommand.ExecuteAsync(null); var call = await f.Transport.NextAsync(); call.Emit(Body);
        await f.WaitAsync(() => f.Coordinator.Snapshots.Any(s => s.Body == Body)); a.Project();
        var active = (await f.Chat.ReadMessagesAsync(a.ConversationId)).Last();
        await main.CreateBranchAsync(BranchStorageTests.Selection(active));
        Assert.Empty(await f.Store.ListChildBranchesAsync(a.ConversationId)); Assert.False(main.HasPersistenceFailure);
        main.DetachObservers();
    }
    [AvaloniaFact]
    public async Task Reading_failure_retains_latest_location_and_reopen_restores_only_valid_workspace()
    {
        await using var f = await F.OpenAsync(); var (main, answer) = await Start(f); var a = Chat(main);
        var location = new ReadingLocation(answer.Id, 0, 29, 1, MarkdownSelectionAdapter.RendererProfile);
        a.RecordReading(location); a.InputText = "restart draft";
        f.Sql("CREATE TRIGGER fail_read BEFORE INSERT ON conversation_read_state BEGIN SELECT RAISE(ABORT,'fixture'); END");
        await main.NavigateAsync(new(MainArea.Settings)); Assert.True(main.HasPersistenceFailure);
        await Assert.ThrowsAnyAsync<Exception>(() => main.RetryPersistenceAsync());
        f.Sql("DROP TRIGGER fail_read"); await main.RetryPersistenceAsync();
        await main.NavigateAsync(new(MainArea.Explore)); Assert.Equal(location, Chat(main).ReadState.Location);
        main.NewWorkspaceName = "other"; await main.CreateWorkspaceAsync(); await main.SwitchWorkspaceAsync(main.Workspaces[0].Id);
        Assert.Equal(a.ConversationId, Chat(main).ConversationId); Assert.Equal(location, Chat(main).ReadState.Location);
        await main.PrepareCloseAsync(); await main.SaveDraftForCloseAsync(); main.DetachObservers();
        await f.Coordinator.DisposeAsync(); await f.Store.DisposeAsync();
        await using var disk = await SqliteWorkspaceStore.OpenAsync(f.Paths);
        var transport = new GenerationCoordinatorTests.ControlledTransport(); var vault = new GenerationCoordinatorTests.MemoryVault();
        await using var coordinator = new Mindweave.Application.Chat.GenerationCoordinator(disk, transport, vault);
        await coordinator.InitializeAsync();
        var reopened = CompositionRoot.CreateMain(disk, coordinator, new(disk, vault, transport)); await reopened.InitializeAsync();
        Assert.Equal(location, Chat(reopened).ReadState.Location); Assert.Equal("restart draft", Chat(reopened).InputText);
        Assert.Null(Chat(reopened).PendingSourceReveal); reopened.DetachObservers();
    }
    [AvaloniaFact]
    public async Task Product_view_native_drag_button_creates_branch_and_source_return_reveals_same_block()
    {
        await using var f = await F.OpenAsync(); var (main, answer) = await Start(f);
        var mainView = new MainView { DataContext = main };
        var window = new Window { Content = mainView, Width = 1200, Height = 850 }; window.Show(); Layout(window);
        try
        {
            var markdown = window.GetVisualDescendants().OfType<MarkdownMessageView>().Single(v => v.MessageId == answer.Id);
            var blocks = MarkdownSelectionAdapter.Blocks(markdown.Renderer);
            var first = blocks[0]; var last = blocks[1];
            var a = first.GetTextRangeBoundsInControl(4, 1)[0]; var b = last.GetTextRangeBoundsInControl(3, 1)[0];
            var from = first.TranslatePoint(new Point(a.X + .5, a.Center.Y), window)!.Value;
            var to = last.TranslatePoint(new Point(b.X + .5, b.Center.Y), window)!.Value;
            window.MouseDown(from, MouseButton.Left); window.MouseMove(to, RawInputModifiers.LeftMouseButton); window.MouseUp(to, MouseButton.Left); Layout(window);
            var captured = markdown.CaptureSelection(); Assert.Contains("粗体", captured.Quote); Assert.Contains("第二段", captured.Quote);
            var button = window.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => Equals(b.Content, "创建分支会话") && b.IsVisible);
            Assert.NotNull(button);
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitUi(() => main.ActiveContent is ChatViewModel c && c.ConversationId != answer.ConversationId);
            var child = Chat(main); var source = Assert.Single(await f.Store.ListChildBranchesAsync(answer.ConversationId));
            Assert.Equal(captured.Quote, source.Quote); Assert.Equal(captured.Anchor, source.Anchor);
            Layout(window); f.Coordinator.ReportPersistenceFailure(); main.ReturnToWorkbench(); Layout(window);
            var returnButton = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "返回来源"));
            Assert.True(returnButton.IsEnabled); // Reading by relationship stays available while mutations are paused.
            await main.RetryPersistenceAsync();
            await main.ReturnToSourceAsync(child.ConversationId); Layout(window);
            markdown = window.GetVisualDescendants().OfType<MarkdownMessageView>().Single(v => v.MessageId == answer.Id);
            Assert.Null(Chat(main).PendingSourceReveal);
            Assert.Contains(MarkdownSelectionAdapter.Blocks(markdown.Renderer), block => block.Highlights.TryGetValue("saved-source", out _));
            Assert.NotEmpty(Chat(main).Messages.Single(m => m.Id == answer.Id).Branches);
            var bookmark = window.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is BranchBookmark);
            await Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(bookmark.Command).ExecuteAsync(bookmark.CommandParameter);
            Assert.Equal(child.ConversationId, Chat(main).ConversationId); Assert.Null(Chat(main).PendingSourceReveal);
        }
        finally { main.DetachObservers(); window.Close(); }
    }
    [AvaloniaFact]
    public async Task Corrupt_source_falls_back_to_stable_message_quote_without_searching_duplicate_words()
    {
        await using var f = await F.OpenAsync(); var (main, answer) = await Start(f);
        await main.CreateBranchAsync(Capture(answer)); var child = Chat(main).ConversationId;
        f.Sql("DROP TRIGGER branch_source_immutable"); // Test-owned corruption; normal writes cannot mutate sources.
        f.Sql($"UPDATE branch_source SET anchor_payload='{{}}' WHERE child_conversation_id='{child:D}'");
        var window = new Window { Content = new MainView { DataContext = main }, Width = 1200, Height = 800 }; window.Show(); Layout(window);
        try
        {
            await main.ReturnToSourceAsync(child); Layout(window);
            Assert.Contains("无法精确定位原句", Chat(main).ErrorMessage);
            Assert.Contains("第二段 重复词", Chat(main).ErrorMessage);
            var renderer = window.GetVisualDescendants().OfType<MarkdownMessageView>().Single(v => v.MessageId == answer.Id).Renderer;
            Assert.All(MarkdownSelectionAdapter.Blocks(renderer), block => Assert.False(block.Highlights.TryGetValue("saved-source", out _)));
            Assert.False(main.HasPersistenceFailure); Assert.Null(Chat(main).PendingSourceReveal);
        }
        finally { main.DetachObservers(); window.Close(); }
    }
    [AvaloniaFact]
    public async Task Product_scroll_is_flushed_on_navigation_and_old_layout_callback_cannot_scroll_new_page()
    {
        await using var f = await F.OpenAsync(); var (main, _) = await Start(f); var a = Chat(main).ConversationId;
        var answer = await BranchStorageTests.Answer(f.Store, a, "long", string.Join("\n\n", Enumerable.Range(1, 50).Select(i => $"段落 {i} 重复词。继续阅读固定的合成文字。")));
        await main.NavigateAsync(new(MainArea.Settings)); await main.NavigateAsync(new(MainArea.Explore));
        var window = new Window { Content = new MainView { DataContext = main }, Width = 1200, Height = 800 }; window.Show(); Layout(window);
        try
        {
            var view = window.GetVisualDescendants().OfType<ChatView>().Single(); var scroll = view.FindControl<ScrollViewer>("MessagesScroll")!;
            scroll.Offset = new Vector(0, 640); Layout(window);
            await main.NavigateAsync(new(MainArea.Settings));
            var saved = await f.Store.LoadReadStateAsync(a); Assert.NotNull(saved?.Location); Assert.Equal(answer.Id, saved.Location.MessageId);
            await main.NavigateAsync(new(MainArea.Explore)); Layout(window);
            var fresh = window.GetVisualDescendants().OfType<ChatView>().Single().FindControl<ScrollViewer>("MessagesScroll")!;
            Assert.True(fresh.Offset.Y > 400);
            await main.NewConversationCommand.ExecuteAsync(null); var target = Chat(main);
            Layout(window); Assert.Null(target.ReadState.Location);
            var newScroll = window.GetVisualDescendants().OfType<ChatView>().Single().FindControl<ScrollViewer>("MessagesScroll")!;
            Assert.Equal(0, newScroll.Offset.Y);
        }
        finally { main.DetachObservers(); window.Close(); }
    }
    [AvaloniaFact]
    public async Task Explicit_flush_during_external_pause_saves_terminals_without_dispatch_or_gate_release()
    {
        await using var f = await F.OpenAsync();
        var a = await f.ConversationAsync(); var b = await f.ConversationAsync(); var c = await f.ConversationAsync();
        var aTask = await f.Coordinator.SendAsync(a.Id, "A"); var aCall = await f.Transport.NextAsync();
        await f.Coordinator.SendAsync(b.Id, "B"); await f.Transport.NextAsync();
        await f.Coordinator.SendAsync(c.Id, "C");
        f.Coordinator.ReportPersistenceFailure(); aCall.Emit("terminal A", "stop"); aCall.Finish();
        await f.WaitAsync(() => f.Snapshot(aTask).Status == GenerationStatus.Completed);
        await f.Coordinator.FlushPendingAsync();
        Assert.Equal("terminal A", (await f.Chat.ReadMessagesAsync(a.Id)).Last().Body);
        Assert.True(f.Coordinator.HasPersistenceFailure); Assert.Equal(2, f.Transport.StartCount);
        Assert.DoesNotContain(f.Coordinator.Snapshots, s => s.Kind == GenerationKind.Title);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.SendAsync(a.Id, "blocked"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.RetryPersistenceAsync());
    }
    private static void Layout(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task WaitUi(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, timeout.Token); }
    }
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Committed_branch_lost_return_or_navigation_read_failure_repairs_using_original_operation(bool lostReturn)
    {
        await using var f = await F.OpenAsync();
        var conversation = await f.ConversationAsync(); var answer = await BranchStorageTests.Answer(f.Store, conversation.Id, "q", Body);
        var controlled = new ChatDesktopTests.ControlledChatStore(f.Store);
        var main = CompositionRoot.CreateMain(controlled, f.Coordinator, new(controlled, f.Vault, f.Transport));
        await main.InitializeAsync(); await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, conversation.Id));
        controlled.LoseBranchReturn = lostReturn; controlled.FailNextReadState = !lostReturn;
        await main.CreateBranchAsync(Capture(answer));
        var accepted = Assert.Single(await f.Store.ListChildBranchesAsync(conversation.Id));
        Assert.True(main.HasPersistenceFailure); Assert.False(Chat(main).CanSend);
        await main.RetryPersistenceAsync();
        Assert.False(main.HasPersistenceFailure); Assert.Equal(accepted.ChildConversationId, Chat(main).ConversationId);
        Assert.Single(await f.Store.ListChildBranchesAsync(conversation.Id)); main.DetachObservers();
    }
    [AvaloniaFact]
    public async Task Pending_branch_repair_can_finish_after_readonly_workspace_switch()
    {
        await using var f = await F.OpenAsync(); var (main, answer) = await Start(f); var original = main.CurrentWorkspace!.Id;
        var other = await f.Store.CreateAsync("other"); main.Workspaces.Add(other);
        f.Sql("CREATE TRIGGER fail_selection BEFORE UPDATE ON app_selection BEGIN SELECT RAISE(ABORT,'fixture'); END");
        await main.CreateBranchAsync(Capture(answer)); Assert.True(main.HasPersistenceFailure);
        var child = Assert.Single(await f.Store.ListChildBranchesAsync(answer.ConversationId));
        await main.SwitchWorkspaceAsync(other.Id); Assert.Equal(other.Id, main.CurrentWorkspace!.Id);
        f.Sql("DROP TRIGGER fail_selection"); await main.RetryPersistenceAsync();
        Assert.False(main.HasPersistenceFailure);
        await main.SwitchWorkspaceAsync(original);
        Assert.Single(await f.Store.ListChildBranchesAsync(answer.ConversationId));
        Assert.Contains(main.Conversations, item => item.Id == child.ChildConversationId);
        main.DetachObservers();
    }
    [AvaloniaFact]
    public async Task Path_and_sidebar_follow_real_parent_titles_and_reject_forged_source_navigation()
    {
        await using var f = await F.OpenAsync(); var (main, answer) = await Start(f);
        var selection = Capture(answer); await main.CreateBranchAsync(selection); var child = Chat(main).ConversationId;
        await f.Store.RenameConversationAsync(answer.ConversationId, "renamed parent");
        await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, child));
        await f.Coordinator.FlushPendingAsync();
        await WaitUi(() => main.Conversations.First(i => i.Id == answer.ConversationId).Title == "renamed parent");
        Assert.Contains("renamed parent", Chat(main).BranchPath);
        await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, answer.ConversationId, NavigationIntent.ReturnToSource,
            new(Guid.NewGuid(), answer.ConversationId, selection)));
        Assert.Equal(child, Chat(main).ConversationId); Assert.NotNull(main.ErrorMessage); Assert.False(main.HasPersistenceFailure);
        main.DetachObservers();
    }
    [AvaloniaFact]
    public async Task Branch_from_initial_top_persists_explicit_parent_top_and_later_read_revision()
    {
        await using var f = await F.OpenAsync(); var (main, answer) = await Start(f);
        await main.CreateBranchAsync(Capture(answer));
        var top = await f.Store.LoadReadStateAsync(answer.ConversationId); Assert.NotNull(top); Assert.Null(top.Location);
        await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, answer.ConversationId));
        var next = new ReadingLocation(answer.Id, 0, 17, 1, MarkdownSelectionAdapter.RendererProfile);
        Chat(main).RecordReading(next); await main.NavigateAsync(new(MainArea.Settings));
        var saved = await f.Store.LoadReadStateAsync(answer.ConversationId);
        Assert.Equal(next, saved!.Location); Assert.True(saved.Revision > top.Revision); main.DetachObservers();
    }
    [AvaloniaFact]
    public async Task Branch_transaction_failure_pauses_queue_then_full_repair_reuses_the_operation()
    {
        await using var f = await F.OpenAsync(); var (main, answer) = await Start(f);
        var a = await f.ConversationAsync(); var b = await f.ConversationAsync(); var c = await f.ConversationAsync();
        var firstTask = await f.Coordinator.SendAsync(a.Id, "A"); var first = await f.Transport.NextAsync();
        await f.Coordinator.SendAsync(b.Id, "B"); await f.Transport.NextAsync(); await f.Coordinator.SendAsync(c.Id, "C");
        f.Sql("CREATE TRIGGER fail_branch BEFORE INSERT ON branch_source BEGIN SELECT RAISE(ABORT,'fixture'); END");
        await main.CreateBranchAsync(Capture(answer)); Assert.True(main.HasPersistenceFailure);
        Assert.Empty(await f.Store.ListChildBranchesAsync(answer.ConversationId));
        first.Emit("completed while paused", "stop"); first.Finish();
        await f.WaitAsync(() => f.Snapshot(firstTask).Status == GenerationStatus.Completed);
        await f.Coordinator.FlushPendingAsync(); Assert.Equal(2, f.Transport.StartCount);
        await Assert.ThrowsAnyAsync<Exception>(() => main.RetryPersistenceAsync()); Assert.True(main.HasPersistenceFailure);
        f.Sql("DROP TRIGGER fail_branch"); await main.RetryPersistenceAsync();
        Assert.Single(await f.Store.ListChildBranchesAsync(answer.ConversationId)); Assert.False(main.HasPersistenceFailure);
        var next = await f.Transport.NextAsync(); Assert.Equal("C", next.Request.Messages.Last().Content);
        main.DetachObservers();
    }
    [AvaloniaFact]
    public async Task Late_layout_callback_from_detached_source_page_cannot_move_rebound_view()
    {
        await using var f = await F.OpenAsync(); var (main, answer) = await Start(f);
        await main.CreateBranchAsync(Capture(answer)); var child = Chat(main).ConversationId;
        await main.ReturnToSourceAsync(child); var oldPage = Chat(main);
        var view = new ChatView { DataContext = oldPage }; // Source callback queued; no layout is attached yet.
        await main.NewConversationCommand.ExecuteAsync(null); var current = Chat(main);
        view.DataContext = current;
        var window = new Window { Content = view, Width = 700, Height = 500 }; window.Show(); Layout(window);
        try
        {
            Assert.NotNull(oldPage.PendingSourceReveal); Assert.Null(current.PendingSourceReveal);
            Assert.Equal(0, view.FindControl<ScrollViewer>("MessagesScroll")!.Offset.Y);
            oldPage.RecordReading(new(answer.Id, 0, 55, 1, MarkdownSelectionAdapter.RendererProfile));
            Assert.Null(current.ReadState.Location);
        }
        finally { main.DetachObservers(); window.Close(); }
    }
    [AvaloniaFact]
    public async Task Chinese_anchor_UTF8_boundary_matches_store_and_rejects_atomically()
    {
        await using var f = await F.OpenAsync(); var (main, answer) = await Start(f); var selection = Capture(answer);
        var json = JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(selection.Anchor.Payload), new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.Contains("重复词", json);
        var exact = json + new string(' ', 1_048_576 - Encoding.UTF8.GetByteCount(json));
        var view = new MarkdownMessageView { MessageId = answer.Id, Revision = answer.Revision, Body = Body };
        var window = new Window { Content = view, Width = 700, Height = 500 }; window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        try
        {
            var at = selection with { Anchor = selection.Anchor with { Payload = exact } };
            view.RestoreSelection(at);
            await f.Store.CreateBranchAsync(Chat(main).ConversationId, at, Guid.NewGuid());
            var over = at with { Anchor = at.Anchor with { Payload = exact + " " } };
            Assert.True(over.Anchor.Payload.Length < 1_048_576);
            var before = MarkdownSelectionAdapter.Blocks(view.Renderer).Select(b => (b.SelectionStart,b.SelectionEnd)).ToArray();
            Assert.Throws<InvalidDataException>(() => view.RestoreSelection(over));
            Assert.Equal(before, MarkdownSelectionAdapter.Blocks(view.Renderer).Select(b => (b.SelectionStart,b.SelectionEnd)).ToArray());
            await Assert.ThrowsAsync<ArgumentException>(() => f.Store.CreateBranchAsync(Chat(main).ConversationId, over, Guid.NewGuid()));
            Assert.Single(await f.Store.ListChildBranchesAsync(Chat(main).ConversationId));
        }
        finally { window.Close(); main.DetachObservers(); }
    }
}
