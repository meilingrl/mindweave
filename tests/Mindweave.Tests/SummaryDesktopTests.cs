using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Navigation;
using Mindweave.Application.Summaries;
using System.Reflection;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.Views;
using Xunit;

namespace Mindweave.Tests;

public sealed class SummaryDesktopTests
{
    [AvaloniaFact]
    public async Task Production_root_exposes_explicit_summary_entry_and_scope_without_navigating()
    {
        await using var f = await SummaryStorageFixture.Open();
        var transport = new GenerationCoordinatorTests.ControlledTransport();
        var root = new RootViewModel(() => Task.FromResult<Mindweave.Application.Storage.IWorkspaceStore>(f.Store),
            new DesktopActions(f.Paths.DataDirectory, () => { }), f.Paths.DataDirectory,
            store => new DesktopChatSession((Mindweave.Application.Chat.IChatStore)store, transport, new GenerationCoordinatorTests.MemoryVault()));
        await root.InitializeAsync();
        var main = Assert.IsType<MainViewModel>(root.Content);
        await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, f.Conversation));
        var view = new ChatView { DataContext = main.ActiveContent };
        var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show(); Layout();
        try
        {
            var button = view.FindControl<Button>("GenerateSummaryButton");
            Assert.NotNull(button);
            Assert.Equal("总结当前会话", button.Content);
            var scope = view.FindControl<TextBlock>("SummaryScopeNotice")!.Text!;
            Assert.Contains("费用", scope); Assert.Contains("预算", scope); Assert.Contains("当前", scope);
            await ((IAsyncRelayCommand)button.Command!).ExecuteAsync(null);
            Assert.IsType<ChatViewModel>(main.ActiveContent);
            Assert.Equal(0, transport.StartCount); // Missing configuration is an ordinary rejection.
        }
        finally { window.Close(); await root.CloseAsync(); }
    }
    [AvaloniaFact]
    public async Task Bound_list_metadata_does_not_read_and_current_editor_preserves_invalid_draft_across_versions()
    {
        await using var f = await SummaryStorageFixture.Open();
        var candidate = await f.Generate("Candidate");
        await BranchStorageTests.Answer(f.Store, f.Conversation, "later", "later answer");
        await f.Versions.RenameSummaryNodeAsync(f.Node, (await f.Snapshot()).State.Node.NameRevision, "A display name");
        var list = new SummaryListViewModel(f.Workspace, f.Versions, _ => Task.CompletedTask);
        await list.ReloadAsync();
        var view = new SummaryListView { DataContext = list };
        var window = new Window { Content = view, Width = 750, Height = 600 }; window.Show(); Layout();
        try
        {
            var item = Assert.Single(list.Items);
            Assert.True(item.IsUnread); Assert.True(item.HasCandidate); Assert.True(item.HasLaterContent);
            view.FindControl<TextBox>("SummarySearch")!.Text = "First"; Layout(); Assert.Single(list.Items);
            list.SelectedItem = item; Assert.False((await f.Versions.LoadSummaryReadingAsync(f.First))!.State.IsRead);
            var draft = new SummaryDraftSession(f.Versions, f.Node, () => { });
            var page = new SummaryViewModel(f.Workspace, f.Node, f.Versions, draft, () => false, () => { }, _ => Task.CompletedTask);
            await page.LoadAsync();
            var detail = new SummaryView { DataContext = page };
            window.Content = detail; Layout();
            await page.EditCommand.ExecuteAsync(null); Layout();
            detail.FindControl<TextBox>("SummaryEditTitle")!.Text = "";
            detail.FindControl<TextBox>("SummaryEditBody")!.Text = "unfinished"; Layout();
            await page.FlushAsync();
            await page.SelectVersionAsync(candidate.VersionId); Layout();
            Assert.False(page.CanEditCurrent); Assert.False(page.IsEditing);
            Assert.Equal("", draft.Title); Assert.Equal("unfinished", draft.Markdown);
            await page.SelectVersionAsync(f.First); await page.EditCommand.ExecuteAsync(null); Layout();
            Assert.Equal("unfinished", detail.FindControl<TextBox>("SummaryEditBody")!.Text);
            await page.SaveCommand.ExecuteAsync(null);
            Assert.Equal(2, (await f.Versions.ListSummaryVersionsAsync(f.Node)).Length);
            Assert.NotNull(page.ErrorMessage);
            page.Detach();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Actual_renderer_short_body_requires_continuous_three_seconds_and_manual_unread_suppresses_epoch()
    {
        await using var f = await SummaryStorageFixture.Open();
        var page = new SummaryViewModel(f.Workspace, f.Node, f.Versions,
            new SummaryDraftSession(f.Versions, f.Node, () => { }), () => false, () => { }, _ => Task.CompletedTask);
        await page.LoadAsync();
        var now = TimeSpan.Zero;
        var view = new SummaryView { DataContext = page, Clock = () => now, AutomaticObservation = false };
        var window = new Window { Content = view, Width = 900, Height = 800 }; window.Show(); Layout();
        try
        {
            await view.ObserveNowAsync(true);
            Assert.False(page.IsRead);
            now = TimeSpan.FromSeconds(2); await view.ObserveNowAsync(true); Assert.False(page.IsRead);
            await view.ObserveNowAsync(false);
            now = TimeSpan.FromSeconds(20); await view.ObserveNowAsync(true); Assert.False(page.IsRead);
            now = TimeSpan.FromSeconds(23); await view.ObserveNowAsync(true); Assert.True(page.IsRead);
            await page.MarkUnreadCommand.ExecuteAsync(null);
            now = TimeSpan.FromSeconds(30); await view.ObserveNowAsync(true); Assert.False(page.IsRead);
            Assert.Equal(TimeSpan.Zero, page.Reading!.State.VisibleDwell);
            now = TimeSpan.FromSeconds(33); await view.ObserveNowAsync(true);
            Assert.Equal(TimeSpan.FromSeconds(3), page.Reading!.State.VisibleDwell); Assert.False(page.IsRead);
        }
        finally { page.Detach(); window.Close(); }
    }

    [AvaloniaFact]
    public void Actual_renderer_mapping_normalizes_formatted_unicode_and_measures_offviewport_text()
    {
        var markdown = new MarkdownMessageView { Body = "# **e\u0301😀** [link](https://example.invalid)\n\n😀 block\n\n```text\ncode value\n```\n\n| one | two |\n| --- | --- |\n| red | blue |\n\n" + string.Join("\n\n", Enumerable.Repeat("long text 😀 with words", 30)) };
        var scroll = new ScrollViewer { Content = markdown, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var window = new Window { Content = scroll, Width = 600, Height = 200 }; window.Show(); Layout();
        try
        {
            var measured = SummaryReadingAdapter.Measure(markdown.Renderer, scroll)!;
            Assert.StartsWith("é😀 link", measured.ReadableText);
            Assert.DoesNotContain("**", measured.ReadableText);
            Assert.Contains("code value", measured.ReadableText); Assert.Contains("red", measured.ReadableText); Assert.Contains("blue", measured.ReadableText);
            Assert.False(measured.FitsViewport);
            Assert.True(measured.Intervals.Sum(i => i.End - i.Start) < measured.ReadableText.EnumerateRunes().Count() / 2);
            Assert.NotNull(measured.Location);
            scroll.Offset = new Avalonia.Vector(0, scroll.Extent.Height); Layout();
            var bottom = SummaryReadingAdapter.Measure(markdown.Renderer, scroll)!;
            Assert.Equal(measured.ReadableText, bottom.ReadableText);
            Assert.True(bottom.Intervals[0].Start > measured.Intervals[^1].End);
            Assert.True(SummaryReadingAdapter.Restore(measured.Location!, markdown.Renderer, scroll));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Session_navigation_restores_summary_selection_source_conversation_drafts_and_rejects_foreign_nodes()
    {
        await using var f = await SummaryStorageFixture.Open();
        await using var session = new DesktopChatSession(f.Store, new GenerationCoordinatorTests.ControlledTransport(), new GenerationCoordinatorTests.MemoryVault());
        await session.InitializeAsync();
        var main = CompositionRoot.CreateMain(f.Store, session); await main.InitializeAsync();
        await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, f.Conversation));
        ((ChatViewModel)main.ActiveContent!).InputText = "source chat draft";
        await main.NavigateAsync(new(MainArea.Network, ContentKind.Summary, f.Node));
        var page = Assert.IsType<SummaryViewModel>(main.ActiveContent);
        await page.EditCommand.ExecuteAsync(null); page.Draft.Title = "retained edit"; page.Draft.Markdown = "retained body";
        await page.ReturnToConversationCommand.ExecuteAsync(null);
        Assert.Equal(f.Conversation, Assert.IsType<ChatViewModel>(main.ActiveContent).ConversationId);
        Assert.Equal("source chat draft", ((ChatViewModel)main.ActiveContent).InputText);
        await main.NavigateAsync(new(MainArea.Network));
        page = Assert.IsType<SummaryViewModel>(main.ActiveContent);
        await page.EditCommand.ExecuteAsync(null); Assert.Equal("retained edit", page.Draft.Title);
        var selection = (await f.Versions.LoadSummarySelectionAsync()).Selection!;
        Assert.Equal(f.Node, selection.NodeId); Assert.Equal(f.First, selection.VersionId);
        main.DetachObservers(); await main.DrainBackgroundRefreshesAsync();
        var restored = CompositionRoot.CreateMain(f.Store, session); await restored.InitializeAsync();
        Assert.Equal(f.First, Assert.IsType<SummaryViewModel>(restored.ActiveContent).Version!.Header.Identity.Id);
        restored.NewWorkspaceName = "another workspace"; await restored.CreateWorkspaceAsync();
        var before = restored.ActiveContent;
        await restored.NavigateAsync(new(MainArea.Network, ContentKind.Summary, f.Node));
        Assert.Same(before, restored.ActiveContent); Assert.Contains("不属于", restored.ErrorMessage!);
        Assert.Null((await f.Versions.LoadSummarySelectionAsync()).Selection);
        restored.DetachObservers(); await restored.DrainBackgroundRefreshesAsync();
    }

    [AvaloniaFact]
    public async Task No_change_commit_creates_no_version_changed_commit_notifies_once_and_history_is_readonly()
    {
        await using var f = await SummaryStorageFixture.Open(); var notified = new List<Guid>();
        var page = new SummaryViewModel(f.Workspace, f.Node, f.Versions, new(f.Versions, f.Node, () => { }),
            () => false, () => { }, _ => Task.CompletedTask, id => { notified.Add(id); return Task.CompletedTask; });
        await page.LoadAsync(); await page.EditCommand.ExecuteAsync(null); await page.SaveCommand.ExecuteAsync(null);
        Assert.Single(await f.Versions.ListSummaryVersionsAsync(f.Node)); Assert.Empty(notified);
        await page.EditCommand.ExecuteAsync(null); page.Draft.Title = "Changed"; await page.SaveCommand.ExecuteAsync(null);
        Assert.Equal(2, (await f.Versions.ListSummaryVersionsAsync(f.Node)).Length); Assert.Single(notified); Assert.True(page.IsRead);
        await page.SelectVersionAsync(f.First); Assert.False(page.CanEditCurrent);
        await page.EditCommand.ExecuteAsync(null); Assert.False(page.IsEditing); Assert.Equal("历史版本 · 只读", page.VersionIdentity);
        page.Detach();
    }

    [AvaloniaFact]
    public async Task Failed_autosave_retains_memory_and_global_gate_but_source_and_version_reading_still_work()
    {
        await using var f = await SummaryStorageFixture.Open(); var candidate = await f.Generate("Candidate");
        await using var session = new DesktopChatSession(f.Store, new GenerationCoordinatorTests.ControlledTransport(), new GenerationCoordinatorTests.MemoryVault());
        await session.InitializeAsync(); var main = CompositionRoot.CreateMain(f.Store, session); await main.InitializeAsync();
        await main.NavigateAsync(new(MainArea.Network, ContentKind.Summary, f.Node));
        var page = Assert.IsType<SummaryViewModel>(main.ActiveContent); await page.EditCommand.ExecuteAsync(null);
        f.Sql("CREATE TRIGGER task7_fail_draft BEFORE UPDATE ON summary_edit_draft BEGIN SELECT RAISE(ABORT,'synthetic save failure'); END");
        page.Draft.Title = "memory only"; page.Draft.Markdown = "";
        await main.NavigateAsync(new(MainArea.Settings)); Assert.True(main.HasPersistenceFailure); Assert.True(session.Runtime.IsAdmissionPaused);
        await main.NavigateAsync(new(MainArea.Network)); page = Assert.IsType<SummaryViewModel>(main.ActiveContent);
        await page.SelectVersionAsync(candidate.VersionId);
        Assert.Equal(candidate.VersionId, page.Version?.Header.Identity.Id); Assert.Equal("memory only", page.Draft.Title);
        await page.ReturnToConversationCommand.ExecuteAsync(null); Assert.IsType<ChatViewModel>(main.ActiveContent);
        f.Sql("DROP TRIGGER task7_fail_draft"); await main.RetryPersistenceAsync();
        Assert.False(main.HasPersistenceFailure); Assert.Equal("", (await f.Versions.LoadSummaryDraftAsync(f.Node))!.Markdown);
        main.DetachObservers(); await main.DrainBackgroundRefreshesAsync();
    }

    [AvaloniaFact]
    public async Task Deleted_history_is_not_reopened_by_back_or_menu_restore()
    {
        await using var f = await SummaryStorageFixture.Open(); var current = await f.Commit("Current");
        await using var session = new DesktopChatSession(f.Store, new GenerationCoordinatorTests.ControlledTransport(), new GenerationCoordinatorTests.MemoryVault());
        await session.InitializeAsync(); var main = CompositionRoot.CreateMain(f.Store, session); await main.InitializeAsync();
        await main.NavigateAsync(new(MainArea.Network, ContentKind.Summary, f.Node, SummaryVersionId: f.First));
        await main.NavigateAsync(new(MainArea.Network, ContentKind.Summary, f.Node, SummaryVersionId: current.Header.Identity.Id));
        var preview = (await f.Versions.PreviewDeleteSummaryVersionsAsync(f.Node, [f.First])).Value!;
        Assert.Equal(SummaryOutcomeKind.Applied, (await f.Versions.DeleteSummaryVersionsAsync(preview)).Kind);
        await main.GoBackAsync(); Assert.IsType<SummaryListViewModel>(main.ActiveContent);
        await main.NavigateAsync(new(MainArea.Network, ContentKind.Summary, f.Node));
        await main.NavigateAsync(new(MainArea.Explore));
        preview = (await f.Versions.PreviewDeleteSummaryVersionsAsync(f.Node, [current.Header.Identity.Id])).Value!;
        Assert.Equal(SummaryOutcomeKind.Applied, (await f.Versions.DeleteSummaryVersionsAsync(preview)).Kind);
        await main.NavigateAsync(new(MainArea.Network)); Assert.IsType<SummaryListViewModel>(main.ActiveContent);
        main.DetachObservers(); await main.DrainBackgroundRefreshesAsync();
    }

    [AvaloniaFact]
    public async Task First_generation_bound_entry_saves_without_navigation_and_own_active_reply_disables_entry()
    {
        await using var f = await SummaryStorageFixture.Open();
        var conversation = (await f.Store.CreateConversationAsync(f.Workspace)).Id;
        await BranchStorageTests.Answer(f.Store, conversation, "q", "a");
        await f.Store.SaveConfigurationAsync(BranchStorageTests.Config(32768) with { CredentialReference = "v1" });
        var transport = new GenerationCoordinatorTests.ControlledTransport();
        await using var session = new DesktopChatSession(f.Store, transport, new GenerationCoordinatorTests.MemoryVault());
        await session.InitializeAsync(); var main = CompositionRoot.CreateMain(f.Store, session); await main.InitializeAsync();
        await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, conversation));
        var chat = Assert.IsType<ChatViewModel>(main.ActiveContent);
        var view = new ChatView { DataContext = chat }; var window = new Window { Content = view, Width = 900, Height = 750 }; window.Show(); Layout();
        try
        {
            var completed = new TaskCompletionSource<KnowledgeSummaryCommitted>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Summaries.Committed += result => completed.TrySetResult(result);
            var button = view.FindControl<Button>("GenerateSummaryButton")!;
            Assert.True(button.IsEnabled); await ((IAsyncRelayCommand)button.Command!).ExecuteAsync(null);
            var call = await transport.NextAsync(); call.Emit("{\"Title\":\"Unique result\",\"Markdown\":\"saved complete result\"}", "stop"); call.Finish();
            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)); Layout();
            Assert.False(result.Result.IsCandidate); Assert.Same(chat, main.ActiveContent);
            Assert.False((await f.Versions.LoadSummaryReadingAsync(result.Result.VersionId))!.State.IsRead);
            await chat.RefreshSummaryAsync(); chat.InputText = "next question"; await chat.SendCommand.ExecuteAsync(null);
            var reply = await transport.NextAsync(); Layout();
            Assert.False(button.IsEnabled); Assert.True(chat.HasActiveReply);
            await chat.StopCommand.ExecuteAsync(null); reply.Finish();
            await f.Generate("Candidate"); await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, f.Conversation));
            Assert.False(Assert.IsType<ChatViewModel>(main.ActiveContent).CanGenerateSummary);
        }
        finally { window.Close(); main.DetachObservers(); await main.DrainBackgroundRefreshesAsync(); transport.Close(); }
    }

    [AvaloniaFact]
    public async Task Late_body_load_and_failed_body_load_never_pair_old_body_with_new_identity_and_foreign_load_never_reads_body()
    {
        await using var f = await SummaryStorageFixture.Open(); var candidate = await f.Generate("Candidate");
        var proxy = DispatchProxy.Create<ISummaryVersionStore, VersionStoreProxy>(); var control = (VersionStoreProxy)(object)proxy; control.Inner = f.Versions;
        var failed = 0;
        var page = new SummaryViewModel(f.Workspace, f.Node, proxy, new(proxy, f.Node, () => failed++), () => false, () => failed++, _ => Task.CompletedTask);
        await page.LoadAsync();
        control.DelayedId = candidate.VersionId;
        var old = page.SelectVersionAsync(candidate.VersionId); await control.Entered.Task;
        Assert.Equal("", page.SavedBody); Assert.Null(page.Version);
        await page.SelectVersionAsync(f.First);
        control.Release.SetResult(await f.Versions.LoadSummaryVersionAsync(candidate.VersionId)); await old;
        Assert.Equal(f.First, page.Version!.Header.Identity.Id); Assert.Contains("First", page.SavedBody);
        control.DelayedId = null; control.FailId = candidate.VersionId;
        await page.SelectVersionAsync(candidate.VersionId); Assert.Null(page.Version); Assert.Equal("", page.SavedBody); Assert.True(failed > 0);
        var reads = control.BodyReads;
        var foreign = new SummaryViewModel(Guid.NewGuid(), f.Node, proxy, new(proxy, f.Node, () => { }), () => false, () => { }, _ => Task.CompletedTask);
        await foreign.LoadAsync(); Assert.Null(foreign.Version); Assert.Equal(reads, control.BodyReads);
        page.Detach(); foreign.Detach();
    }

    [AvaloniaFact]
    public async Task Long_body_measured_union_marks_read_at_85_percent_and_reopen_restores_reading_location()
    {
        await using var f = await SummaryStorageFixture.Open();
        var saved = await f.Commit("Long", string.Join("\n\n", Enumerable.Repeat("**formatted** text e\u0301 😀 " + new string('字', 100), 12)));
        var state = (await f.Versions.LoadSummaryReadingAsync(saved.Header.Identity.Id))!;
        await f.Versions.SetSummaryReadAsync(saved.Header.Identity.Id, state.State.Epoch, state.State.Revision, false);
        var page = new SummaryViewModel(f.Workspace, f.Node, f.Versions, new(f.Versions, f.Node, () => { }), () => false, () => { }, _ => Task.CompletedTask);
        await page.LoadAsync(); var now = TimeSpan.Zero;
        var view = new SummaryView { DataContext = page, AutomaticObservation = false, Clock = () => now };
        var window = new Window { Content = view, Width = 650, Height = 650 }; window.Show(); Layout();
        try
        {
            await view.ObserveNowAsync(true); Assert.False(page.IsRead);
            var scroll = view.FindControl<ScrollViewer>("SummaryScroll")!;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            scroll.Offset = new Avalonia.Vector(0, scroll.Extent.Height); Layout(); await page.FlushAsync();
            Assert.True(page.Reading!.Location!.ScalarOffset > 0); Assert.False(page.IsRead);
            scroll.Offset = default; Layout(); await view.ObserveNowAsync(true);
            var step = scroll.Viewport.Height * .8;
            for (var offset = step; offset <= scroll.Extent.Height + step; offset += step)
            {
                scroll.Offset = new Avalonia.Vector(0, offset); Layout(); now += TimeSpan.FromTicks(1); await view.ObserveNowAsync(true);
                var fraction = page.Reading!.State.Seen.Sum(i => i.End - i.Start) / (double)page.Reading.State.TextLength;
                Assert.Equal(fraction >= .85, page.IsRead);
            }
            Assert.True(page.IsRead); var location = page.Reading!.Location!; Assert.True(location.ScalarOffset > 0);
            var oldEpoch = page.Reading.State.Epoch; window.Content = null; page.Detach();
            var reopened = new SummaryViewModel(f.Workspace, f.Node, f.Versions, new(f.Versions, f.Node, () => { }), () => false, () => { }, _ => Task.CompletedTask);
            await reopened.LoadAsync(); var newView = new SummaryView { DataContext = reopened, AutomaticObservation = false, Clock = () => now };
            window.Content = newView; Layout(); await newView.ObserveNowAsync(true);
            Assert.True(newView.FindControl<ScrollViewer>("SummaryScroll")!.Offset.Y > 0); Assert.True(reopened.Reading!.State.Epoch > oldEpoch);
            reopened.Detach();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Unsaved_editor_is_not_read_evidence_and_hidden_view_does_not_accumulate_dwell()
    {
        await using var f = await SummaryStorageFixture.Open();
        var page = new SummaryViewModel(f.Workspace, f.Node, f.Versions, new(f.Versions, f.Node, () => { }), () => false, () => { }, _ => Task.CompletedTask);
        await page.LoadAsync(); var now = TimeSpan.Zero;
        var view = new SummaryView { DataContext = page, AutomaticObservation = false, Clock = () => now };
        var window = new Window { Content = view, Width = 900, Height = 800 }; window.Show(); Layout();
        try
        {
            await view.ObserveNowAsync(true); now = TimeSpan.FromSeconds(2); await view.ObserveNowAsync(true);
            view.IsVisible = false; await view.ObserveNowAsync(true); now = TimeSpan.FromSeconds(20); view.IsVisible = true;
            await view.ObserveNowAsync(true); Assert.False(page.IsRead);
            await page.EditCommand.ExecuteAsync(null); page.Draft.Markdown = "unsaved preview"; Layout();
            now = TimeSpan.FromSeconds(100); await view.ObserveNowAsync(true); Assert.False(page.IsRead);
            Assert.Equal("# body\nFirst", page.SavedBody);
            page.ReadCommand.Execute(null); Layout(); await view.ObserveNowAsync(true); Assert.False(page.IsRead);
            now = TimeSpan.FromSeconds(103); await view.ObserveNowAsync(true); Assert.True(page.IsRead);
        }
        finally { await page.FlushAsync(); page.Detach(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Manual_commit_lost_ack_repairs_exact_durable_version_and_notifies_without_duplication()
    {
        await using var f = await SummaryStorageFixture.Open();
        var proxy = DispatchProxy.Create<ISummaryVersionStore, VersionStoreProxy>(); var control = (VersionStoreProxy)(object)proxy; control.Inner = f.Versions;
        var notifications = new List<Guid>(); var failed = 0;
        var draft = new SummaryDraftSession(proxy, f.Node, () => failed++);
        var page = new SummaryViewModel(f.Workspace, f.Node, proxy, draft, () => false, () => failed++, _ => Task.CompletedTask,
            id => { notifications.Add(id); return Task.CompletedTask; });
        await page.LoadAsync(); await page.EditCommand.ExecuteAsync(null); draft.Title = "committed despite lost ack";
        control.LoseCommitAck = true; await page.SaveCommand.ExecuteAsync(null);
        Assert.True(failed > 0); Assert.Equal(2, (await f.Versions.ListSummaryVersionsAsync(f.Node)).Length); Assert.Empty(notifications);
        await draft.FlushAsync();
        Assert.Single(notifications); Assert.Equal(2, (await f.Versions.ListSummaryVersionsAsync(f.Node)).Length);
        await draft.FlushAsync(); Assert.Single(notifications);
        page.Detach();
    }

    [AvaloniaFact]
    public async Task Queued_edit_arriving_during_commit_is_retained_against_new_current_version()
    {
        await using var f = await SummaryStorageFixture.Open();
        var proxy = DispatchProxy.Create<ISummaryVersionStore, VersionStoreProxy>(); var control = (VersionStoreProxy)(object)proxy; control.Inner = f.Versions;
        var draft = new SummaryDraftSession(proxy, f.Node, () => { });
        var page = new SummaryViewModel(f.Workspace, f.Node, proxy, draft, () => false, () => { }, _ => Task.CompletedTask);
        await page.LoadAsync(); await page.EditCommand.ExecuteAsync(null); draft.Title = "submitted";
        control.HoldCommit = true; var save = page.SaveCommand.ExecuteAsync(null); await control.CommitEntered.Task;
        Assert.False(page.CanEditCurrent); draft.Markdown = "new queued edit";
        control.CommitRelease.SetResult(); await save; await draft.FlushAsync();
        Assert.Equal("new queued edit", draft.Markdown); Assert.True(draft.IsDirty);
        Assert.Equal("new queued edit", (await f.Versions.LoadSummaryDraftAsync(f.Node))!.Markdown);
        page.Detach();
    }

    [AvaloniaFact]
    public async Task Shared_save_pause_interrupts_automatic_dwell_until_repair_without_blocking_body_reading()
    {
        await using var f = await SummaryStorageFixture.Open(); var blocked = false;
        var page = new SummaryViewModel(f.Workspace, f.Node, f.Versions, new(f.Versions, f.Node, () => { }), () => blocked, () => { }, _ => Task.CompletedTask);
        await page.LoadAsync(); var now = TimeSpan.Zero;
        var view = new SummaryView { DataContext = page, AutomaticObservation = false, Clock = () => now };
        var window = new Window { Content = view, Width = 900, Height = 800 }; window.Show(); Layout();
        try
        {
            await view.ObserveNowAsync(true); now = TimeSpan.FromSeconds(2); await view.ObserveNowAsync(true);
            blocked = true; now = TimeSpan.FromSeconds(20); await view.ObserveNowAsync(true);
            Assert.False(page.IsRead); Assert.True(page.CanRead); Assert.Contains("First", page.SavedBody);
            blocked = false; now = TimeSpan.FromSeconds(30); await view.ObserveNowAsync(true); Assert.False(page.IsRead);
            now = TimeSpan.FromSeconds(33); await view.ObserveNowAsync(true); Assert.True(page.IsRead);
        }
        finally { page.Detach(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Candidate_read_state_survives_adoption_and_late_previous_epoch_observation_is_ignored()
    {
        await using var f = await SummaryStorageFixture.Open(); var candidate = await f.Generate("Candidate");
        var page = new SummaryViewModel(f.Workspace, f.Node, f.Versions, new(f.Versions, f.Node, () => { }), () => false, () => { }, _ => Task.CompletedTask);
        await page.LoadAsync(candidate.VersionId);
        var view = new SummaryView { DataContext = page, AutomaticObservation = false };
        var window = new Window { Content = view, Width = 900, Height = 800 }; window.Show(); Layout();
        try
        {
            await view.ObserveNowAsync(true); var old = page.Reading!.State;
            await page.MarkReadCommand.ExecuteAsync(null); Assert.True(page.IsRead);
            await page.SelectVersionAsync(f.First); Layout(); await view.ObserveNowAsync(true);
            await page.ObserveReadingAsync(candidate.VersionId, old.Epoch, TimeSpan.FromSeconds(99), true, true, true, [new(0, old.TextLength)], null);
            Assert.False(page.IsRead);
            var adopted = await f.Versions.AdoptSummaryCandidateAsync((await f.Snapshot()).State.Token);
            Assert.Equal(SummaryOutcomeKind.Applied, adopted.Kind);
            await page.LoadAsync(); Assert.Equal(candidate.VersionId, page.Version!.Header.Identity.Id); Assert.True(page.IsRead); Assert.Equal("当前版本", page.VersionIdentity);
        }
        finally { page.Detach(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Background_candidate_metadata_refresh_does_not_replace_saved_body_read_epoch_or_dirty_editor()
    {
        await using var f = await SummaryStorageFixture.Open();
        var page = new SummaryViewModel(f.Workspace, f.Node, f.Versions, new(f.Versions, f.Node, () => { }), () => false, () => { }, _ => Task.CompletedTask);
        await page.LoadAsync();
        var view = new SummaryView { DataContext = page, AutomaticObservation = false };
        var window = new Window { Content = view, Width = 900, Height = 800 }; window.Show(); Layout();
        try
        {
            await view.ObserveNowAsync(true); var epoch = page.Reading!.State.Epoch;
            await page.EditCommand.ExecuteAsync(null); page.Draft.Title = "my draft";
            var candidate = await f.Generate("Candidate"); await page.RefreshMetadataAsync(); Layout();
            Assert.Equal(2, page.Versions.Count); Assert.Contains(page.Versions, h => h.Identity.Id == candidate.VersionId);
            Assert.Equal(epoch, page.Reading!.State.Epoch); Assert.Equal(f.First, page.Version!.Header.Identity.Id);
            Assert.True(page.IsEditing); Assert.Equal("my draft", page.Draft.Title);
        }
        finally { await page.FlushAsync(); page.Detach(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Draft_lost_ack_reconciles_before_saving_a_newer_queued_input()
    {
        await using var f = await SummaryStorageFixture.Open();
        var proxy = DispatchProxy.Create<ISummaryVersionStore, VersionStoreProxy>(); var control = (VersionStoreProxy)(object)proxy; control.Inner = f.Versions;
        var draft = new SummaryDraftSession(proxy, f.Node, () => { }); await draft.BeginAsync();
        draft.Title = "first write"; control.LoseDraftAck = true;
        await Assert.ThrowsAsync<IOException>(() => draft.FlushAsync());
        draft.Title = "newer queued input"; await draft.FlushAsync();
        Assert.Equal("newer queued input", (await f.Versions.LoadSummaryDraftAsync(f.Node))!.Title); Assert.False(draft.HasMemoryChanges);
    }

    [AvaloniaFact]
    public async Task Close_preparation_drains_started_dispatcher_refresh_reads_before_store_release()
    {
        await using var f = await SummaryStorageFixture.Open();
        var proxy = DispatchProxy.Create<IDesktopStore, DesktopStoreProxy>(); var control = (DesktopStoreProxy)(object)proxy; control.Inner = f.Store;
        DesktopChatSession? session = null;
        var root = new RootViewModel(() => Task.FromResult<Mindweave.Application.Storage.IWorkspaceStore>(proxy),
            new DesktopActions(f.Paths.DataDirectory, () => { }), f.Paths.DataDirectory,
            store => session = new DesktopChatSession((Mindweave.Application.Chat.IChatStore)store,
                new GenerationCoordinatorTests.ControlledTransport(), new GenerationCoordinatorTests.MemoryVault()));
        await root.InitializeAsync(); var main = Assert.IsType<MainViewModel>(root.Content);
        await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, f.Conversation));
        control.HoldSummaryRead = true;
        session!.Runtime.ResumeAccepting(); Dispatcher.UIThread.RunJobs();
        await control.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var preparing = main.PrepareCloseAsync();
        try { Assert.False(preparing.IsCompleted); Assert.False(control.Disposed); }
        finally { control.ReleaseRead.TrySetResult(); await preparing; await root.CloseAsync(); }
        Assert.True(root.IsClosed); Assert.True(control.Disposed);
        Dispatcher.UIThread.RunJobs(); Assert.Equal(0, control.ReadsAfterDispose);
    }

    [AvaloniaFact]
    public async Task Page_close_drain_waits_for_inflight_version_body_load()
    {
        await using var f = await SummaryStorageFixture.Open(); var candidate = await f.Generate("Candidate");
        var proxy = DispatchProxy.Create<ISummaryVersionStore, VersionStoreProxy>(); var control = (VersionStoreProxy)(object)proxy; control.Inner = f.Versions;
        var page = new SummaryViewModel(f.Workspace, f.Node, proxy, new(proxy, f.Node, () => { }), () => false, () => { }, _ => Task.CompletedTask);
        await page.LoadAsync(); control.DelayedId = candidate.VersionId;
        var loading = page.SelectVersionAsync(candidate.VersionId); await control.Entered.Task;
        var draining = page.WaitForPendingOperationsAsync();
        try { Assert.False(draining.IsCompleted); }
        finally { control.Release.TrySetResult(await f.Versions.LoadSummaryVersionAsync(candidate.VersionId)); await loading; await draining; page.Detach(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Precommit_node_read_freezes_persisted_draft_and_retains_later_input(bool loseAcknowledgement)
    {
        await using var f = await SummaryStorageFixture.Open();
        var proxy = DispatchProxy.Create<ISummaryVersionStore, VersionStoreProxy>(); var control = (VersionStoreProxy)(object)proxy; control.Inner = f.Versions;
        var notified = new List<Guid>();
        var draft = new SummaryDraftSession(proxy, f.Node, () => { });
        var page = new SummaryViewModel(f.Workspace, f.Node, proxy, draft, () => false, () => { }, _ => Task.CompletedTask,
            id => { notified.Add(id); return Task.CompletedTask; });
        await page.LoadAsync(); await page.EditCommand.ExecuteAsync(null);
        draft.Title = "submitted title"; draft.Markdown = "submitted body";
        control.HoldNextNodeRead = true; control.LoseCommitAck = loseAcknowledgement;
        var saving = page.SaveCommand.ExecuteAsync(null); await control.NodeReadEntered.Task;
        Assert.Equal("submitted body", (await f.Versions.LoadSummaryDraftAsync(f.Node))!.Markdown);
        draft.Title = "new queued title"; draft.Markdown = "new queued body";
        control.NodeReadRelease.TrySetResult(); await saving;
        await draft.FlushAsync();
        var node = await f.Snapshot(); var current = (await f.Versions.LoadSummaryVersionAsync(node.State.Node.CurrentVersionId))!;
        Assert.Equal("submitted title", current.Header.Title); Assert.Equal("submitted body", current.Markdown);
        Assert.True(draft.IsDirty); Assert.Equal("new queued title", draft.Title); Assert.Equal("new queued body", draft.Markdown);
        var retained = (await f.Versions.LoadSummaryDraftAsync(f.Node))!;
        Assert.Equal(current.Header.Identity.Id, retained.Token.BaseVersionId); Assert.Equal("new queued body", retained.Markdown);
        Assert.Equal(2, (await f.Versions.ListSummaryVersionsAsync(f.Node)).Length); Assert.Single(notified);
        page.Detach();
    }

    [AvaloniaFact]
    public async Task Queued_input_during_current_change_notification_is_retained_against_committed_version()
    {
        await using var f = await SummaryStorageFixture.Open();
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var draft = new SummaryDraftSession(f.Versions, f.Node, () => { });
        draft.ConfigureCurrentChanged(async _ => { entered.SetResult(); await release.Task; });
        await draft.BeginAsync(); draft.Title = "submitted title";
        var committing = draft.CommitAsync(); await entered.Task;
        draft.Title = "queued during notification"; draft.Markdown = "retained later body";
        release.SetResult(); var committed = await committing; await draft.FlushAsync();
        Assert.Equal(SummaryOutcomeKind.Applied, committed.Kind); Assert.True(draft.IsDirty);
        var retained = (await f.Versions.LoadSummaryDraftAsync(f.Node))!;
        Assert.Equal(committed.Value!.Header.Identity.Id, retained.Token.BaseVersionId);
        Assert.Equal("queued during notification", retained.Title); Assert.Equal("retained later body", retained.Markdown);
        Assert.Equal(2, (await f.Versions.ListSummaryVersionsAsync(f.Node)).Length);
    }

    [AvaloniaFact]
    public async Task Hidden_edges_during_held_read_write_reset_dwell_before_next_active_sample()
    {
        await using var f = await SummaryStorageFixture.Open();
        var proxy = DispatchProxy.Create<ISummaryVersionStore, VersionStoreProxy>(); var control = (VersionStoreProxy)(object)proxy; control.Inner = f.Versions;
        var page = new SummaryViewModel(f.Workspace, f.Node, proxy, new(proxy, f.Node, () => { }), () => false, () => { }, _ => Task.CompletedTask);
        await page.LoadAsync(); var now = TimeSpan.Zero;
        var view = new SummaryView { DataContext = page, AutomaticObservation = false, Clock = () => now };
        var window = new Window { Content = view, Width = 900, Height = 800 }; window.Show(); Layout();
        Task? observing = null;
        try
        {
            await view.ObserveNowAsync(true);
            now = TimeSpan.FromSeconds(2); control.HoldNextReadingWrite = true;
            observing = view.ObserveNowAsync(true); await control.ReadingWriteEntered.Task;
            view.AutomaticObservation = true;
            view.IsVisible = false;
            now = TimeSpan.FromSeconds(20); view.IsVisible = true;
            view.AutomaticObservation = false;
            control.ReadingWriteRelease.TrySetResult(); await observing;
            await view.ObserveNowAsync(true); Assert.False(page.IsRead); Assert.Equal(TimeSpan.Zero, page.Reading!.State.VisibleDwell);
            now = TimeSpan.FromSeconds(22); await view.ObserveNowAsync(true); Assert.False(page.IsRead);
            now = TimeSpan.FromSeconds(23); await view.ObserveNowAsync(true); Assert.True(page.IsRead);
        }
        finally { control.ReadingWriteRelease.TrySetResult(); if (observing is not null) await observing; page.Detach(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Failed_close_return_and_explicit_repair_resume_active_summary_reading_from_fresh_dwell()
    {
        await using var f = await SummaryStorageFixture.Open();
        var root = new RootViewModel(() => Task.FromResult<Mindweave.Application.Storage.IWorkspaceStore>(f.Store),
            new DesktopActions(f.Paths.DataDirectory, () => { }), f.Paths.DataDirectory,
            store => new DesktopChatSession((Mindweave.Application.Chat.IChatStore)store,
                new GenerationCoordinatorTests.ControlledTransport(), new GenerationCoordinatorTests.MemoryVault()));
        await root.InitializeAsync(); var main = Assert.IsType<MainViewModel>(root.Content);
        await main.NavigateAsync(new(MainArea.Network, ContentKind.Summary, f.Node));
        var page = Assert.IsType<SummaryViewModel>(main.ActiveContent); var now = TimeSpan.Zero;
        var view = new SummaryView { DataContext = page, AutomaticObservation = false, Clock = () => now };
        var window = new Window { Content = view, Width = 900, Height = 800 }; window.Show(); Layout();
        try
        {
            await view.ObserveNowAsync(true); now = TimeSpan.FromSeconds(2); await view.ObserveNowAsync(true);
            f.Sql("CREATE TRIGGER task7_failed_close BEFORE UPDATE ON summary_read_state BEGIN SELECT RAISE(ABORT,'synthetic close read save failure'); END");
            await root.CloseAsync(); Assert.False(root.IsClosed); Assert.NotNull(root.CloseError); Assert.False(page.CanRead);
            now = TimeSpan.FromSeconds(20); root.ReturnToWorkbenchCommand.Execute(null);
            Assert.Same(page, main.ActiveContent); Assert.True(page.CanRead); Assert.True(main.HasPersistenceFailure);
            await view.ObserveNowAsync(true); Assert.False(page.IsRead);
            f.Sql("DROP TRIGGER task7_failed_close"); await root.RetrySaveCommand.ExecuteAsync(null); Assert.False(main.HasPersistenceFailure);
            await view.ObserveNowAsync(true); Assert.False(page.IsRead); Assert.Equal(TimeSpan.Zero, page.Reading!.State.VisibleDwell);
            now = TimeSpan.FromSeconds(23); await view.ObserveNowAsync(true); Assert.True(page.IsRead);
        }
        finally { f.Sql("DROP TRIGGER IF EXISTS task7_failed_close"); window.Close(); await root.CloseAsync(); }
        Assert.True(root.IsClosed);
    }

    [AvaloniaFact]
    public async Task Hidden_edge_during_read_begin_invalidates_sample_without_reopening_the_same_view_epoch()
    {
        await using var f = await SummaryStorageFixture.Open();
        var proxy = DispatchProxy.Create<ISummaryVersionStore, VersionStoreProxy>(); var control = (VersionStoreProxy)(object)proxy; control.Inner = f.Versions;
        var page = new SummaryViewModel(f.Workspace, f.Node, proxy, new(proxy, f.Node, () => { }), () => false, () => { }, _ => Task.CompletedTask);
        await page.LoadAsync(); var now = TimeSpan.Zero;
        var view = new SummaryView { DataContext = page, AutomaticObservation = false, Clock = () => now };
        var window = new Window { Content = view, Width = 900, Height = 800 }; window.Show(); Layout();
        control.HoldNextReadingBegin = true; var opening = view.ObserveNowAsync(true); await control.ReadingBeginEntered.Task;
        try
        {
            view.IsVisible = false; now = TimeSpan.FromSeconds(20); view.IsVisible = true;
            control.ReadingBeginRelease.TrySetResult(); await opening;
            Assert.Empty(page.Reading!.State.Seen); var openedEpoch = page.Reading.State.Epoch;
            await page.MarkUnreadCommand.ExecuteAsync(null);
            await view.ObserveNowAsync(true);
            Assert.Equal(openedEpoch, page.Reading!.State.Epoch); Assert.True(page.Reading.State.SuppressAutomatic);
            now = TimeSpan.FromSeconds(23); await view.ObserveNowAsync(true); Assert.False(page.IsRead);
        }
        finally { control.ReadingBeginRelease.TrySetResult(); await opening; page.Detach(); window.Close(); }
    }

    public interface IDesktopStore : Mindweave.Application.Storage.IWorkspaceStore, Mindweave.Application.Chat.IChatStore,
        Mindweave.Application.Branches.IBranchStore, ISummaryVersionStore, IKnowledgeSummaryStore, INodeNamingStore { }
    public class DesktopStoreProxy : DispatchProxy
    {
        public Mindweave.Infrastructure.Storage.SqliteWorkspaceStore Inner { get; set; } = null!;
        public bool HoldSummaryRead { get; set; }
        public bool Disposed { get; private set; }
        public int ReadsAfterDispose { get; private set; }
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IAsyncDisposable.DisposeAsync)) Disposed = true;
            if (method.Name == nameof(ISummaryVersionStore.FindConversationSummaryAsync) && HoldSummaryRead)
                return ReadHeldAsync((Guid)args![0]!);
            return method.Invoke(Inner, args);
        }
        private async Task<SummaryNodeSnapshot?> ReadHeldAsync(Guid conversation)
        {
            ReadEntered.TrySetResult(); await ReleaseRead.Task;
            if (Disposed) ReadsAfterDispose++;
            return await Inner.FindConversationSummaryAsync(conversation);
        }
    }

    public class VersionStoreProxy : DispatchProxy
    {
        public ISummaryVersionStore Inner { get; set; } = null!;
        public Guid? DelayedId { get; set; }
        public Guid? FailId { get; set; }
        public bool HoldNextReadingBegin { get; set; }
        public TaskCompletionSource ReadingBeginEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadingBeginRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldNextReadingWrite { get; set; }
        public TaskCompletionSource ReadingWriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadingWriteRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldNextNodeRead { get; set; }
        public TaskCompletionSource NodeReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NodeReadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool LoseDraftAck { get; set; }
        public bool LoseCommitAck { get; set; }
        public bool HoldCommit { get; set; }
        public TaskCompletionSource CommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CommitRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BodyReads { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SummaryVersion?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(ISummaryVersionStore.BeginSummaryReadingAsync) && HoldNextReadingBegin)
            { HoldNextReadingBegin = false; return BeginReadingHeldAsync((Guid)args![0]!, (long)args[1]!, (string)args[2]!); }
            if (targetMethod.Name == nameof(ISummaryVersionStore.SaveSummaryReadingAsync) && HoldNextReadingWrite)
            { HoldNextReadingWrite = false; return SaveReadingHeldAsync((Guid)args![0]!, (SummaryReadObservation)args[1]!, (SummaryReadLocation?)args[2]); }
            if (targetMethod.Name == nameof(ISummaryVersionStore.LoadSummaryNodeAsync) && HoldNextNodeRead)
            { HoldNextNodeRead = false; return LoadNodeHeldAsync((Guid)args![0]!); }
            if (targetMethod.Name == nameof(ISummaryVersionStore.LoadSummaryVersionAsync))
            {
                BodyReads++;
                if ((Guid)args![0]! == DelayedId) { Entered.TrySetResult(); return Release.Task; }
                if ((Guid)args![0]! == FailId) return Task.FromException<SummaryVersion?>(new IOException("synthetic read failure"));
            }
            if (targetMethod.Name == nameof(ISummaryVersionStore.SaveSummaryDraftAsync) && LoseDraftAck) return SaveDraftAsync((SummaryDraftToken)args![0]!, (SummaryContent)args[1]!);
            if (targetMethod.Name == nameof(ISummaryVersionStore.CommitSummaryDraftAsync) && (LoseCommitAck || HoldCommit))
                return CommitAsync((SummaryNodeToken)args![0]!, (SummaryDraftToken)args[1]!);
            return targetMethod.Invoke(Inner, args);
        }
        private async Task<SummaryOutcome<SummaryReadingSnapshot>> BeginReadingHeldAsync(Guid version, long revision, string text)
        { ReadingBeginEntered.TrySetResult(); await ReadingBeginRelease.Task; return await Inner.BeginSummaryReadingAsync(version, revision, text); }
        private async Task<SummaryOutcome<SummaryReadingSnapshot>> SaveReadingHeldAsync(Guid version, SummaryReadObservation observation, SummaryReadLocation? location)
        { ReadingWriteEntered.TrySetResult(); await ReadingWriteRelease.Task; return await Inner.SaveSummaryReadingAsync(version, observation, location); }
        private async Task<SummaryNodeSnapshot?> LoadNodeHeldAsync(Guid node)
        { NodeReadEntered.TrySetResult(); await NodeReadRelease.Task; return await Inner.LoadSummaryNodeAsync(node); }
        private async Task<SummaryOutcome<SummaryEditDraft>> SaveDraftAsync(SummaryDraftToken token, SummaryContent content)
        {
            var result = await Inner.SaveSummaryDraftAsync(token, content); LoseDraftAck = false;
            throw new IOException("synthetic lost draft acknowledgement");
        }
        private async Task<SummaryOutcome<SummaryVersion>> CommitAsync(SummaryNodeToken node, SummaryDraftToken draft)
        {
            CommitEntered.TrySetResult(); if (HoldCommit) await CommitRelease.Task;
            var result = await Inner.CommitSummaryDraftAsync(node, draft);
            if (LoseCommitAck) { LoseCommitAck = false; throw new IOException("synthetic lost manual commit acknowledgement"); }
            return result;
        }
    }

    private static void Layout() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
}
