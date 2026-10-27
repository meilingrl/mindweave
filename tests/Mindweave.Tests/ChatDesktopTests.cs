using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Chat;
using Mindweave.Application.Navigation;
using Mindweave.Application.Storage;
using Mindweave.Core.Chat;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.Views;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Xunit;
using F = Mindweave.Tests.GenerationCoordinatorTests.Fixture;

namespace Mindweave.Tests;

public sealed class ChatDesktopTests
{
    [Fact]
    public async Task V2_migration_backs_up_and_restores_valid_conversation_identity()
    {
        using var dir = new TestDirectory();
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        Guid workspace, conversation;
        await using (var old = await SqliteWorkspaceStore.OpenAsync(paths, DatabaseSchema.Migrations.Take(2).ToArray()))
        {
            workspace = (await old.CreateAsync("upgrade")).Id;
            conversation = (await old.CreateConversationAsync(workspace)).Id;
        }
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths);
        using var inspect = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False");
        inspect.Open();
        using var command = inspect.CreateCommand(); command.CommandText = "PRAGMA user_version";
        Assert.Equal(6L, command.ExecuteScalar());
        var backupPath = Assert.Single(Directory.GetFiles(paths.BackupDirectory, "*.db"));
        using (var backup = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False"))
        {
            backup.Open(); using var version = backup.CreateCommand(); version.CommandText = "PRAGMA user_version";
            Assert.Equal(2L, version.ExecuteScalar());
        }
        await store.SaveSelectionAsync(new(workspace, MainArea.Explore, ContentKind.Conversation, conversation));
        Assert.Equal(conversation, (await store.LoadSelectionAsync())!.TargetId);
        var other = await store.CreateAsync("other");
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveSelectionAsync(new(other.Id, MainArea.Explore, ContentKind.Conversation, conversation)));
    }

    [AvaloniaFact]
    public async Task Bound_chat_sends_streams_stops_and_switches_to_latest_memory()
    {
        await using var f = await F.OpenAsync();
        var main = CompositionRoot.CreateMain(f.Store, f.Coordinator, new(f.Chat, f.Vault, f.Transport));
        await main.InitializeAsync();
        main.NewWorkspaceName = "desktop"; await main.CreateWorkspaceAsync();
        var mainView = Assert.IsType<MainView>(new ViewLocator().Build(main)); mainView.DataContext = main;
        var window = new Window { Content = mainView, Width = 1100, Height = 760 }; window.Show();
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)mainView.FindControl<Button>("NewConversationButton")!.Command!).ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var first = Assert.IsType<ChatViewModel>(main.ActiveContent);
        var firstId = first.ConversationId;
        var view = Assert.Single(window.GetVisualDescendants().OfType<ChatView>());
        try
        {
            view.FindControl<TextBox>("ChatInput")!.Text = "question"; Dispatcher.UIThread.RunJobs();
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)view.FindControl<Button>("SendButton")!.Command!).ExecuteAsync(null);
            var call = await f.Transport.NextAsync();
            Assert.Equal("", first.InputText);
            Assert.Equal("", (await f.Chat.LoadDraftAsync(firstId)).Text);
            await main.NewConversationCommand.ExecuteAsync(null);
            var second = Assert.IsType<ChatViewModel>(main.ActiveContent);
            second.InputText = "draft in second";
            call.Emit("latest in memory");
            await f.WaitAsync(() => f.Coordinator.Snapshots.Any(s => s.Body == "latest in memory"));
            await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, firstId));
            var restored = Assert.IsType<ChatViewModel>(main.ActiveContent);
            Assert.Equal("latest in memory", restored.Messages.Last().Body);
            Assert.Equal("draft in second", (await f.Chat.LoadDraftAsync(second.ConversationId)).Text);
            await restored.StopCommand.ExecuteAsync(null);
            Assert.Equal(GenerationStatus.Stopped, restored.Messages.Last().Status);
            Assert.True(restored.Messages.Last().IsIncomplete);
            Assert.True(call.Cancellation.IsCancellationRequested);
            restored.InputText = "newer draft"; await restored.FlushDraftAsync();
            Assert.Equal("newer draft", (await f.Chat.LoadDraftAsync(firstId)).Text);
        }
        finally { window.Close(); main.DetachObservers(); }
    }

    [AvaloniaFact]
    public async Task Reopen_recovers_interruption_draft_and_valid_selection_without_network()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        Guid conversation;
        await using (var store = await SqliteWorkspaceStore.OpenAsync(paths))
        {
            var workspace = await store.CreateAsync("restart");
            conversation = (await store.CreateConversationAsync(workspace.Id)).Id;
            var config = new AiConfiguration(Guid.NewGuid(), Guid.NewGuid(), "fixture", "http://127.0.0.1:12345/v1", "v1", "fixture", 32768, 2048);
            var work = await store.AcceptReplyAsync(conversation, "q", config);
            await store.WriteGenerationAsync(work.Task.Id, 1, "checkpoint", GenerationStatus.Running);
            await store.SaveDraftAsync(conversation, "pending input", 4);
            await store.SaveSelectionAsync(new(workspace.Id, MainArea.Explore, ContentKind.Conversation, conversation));
        }
        var transport = new GenerationCoordinatorTests.ControlledTransport(); var vault = new GenerationCoordinatorTests.MemoryVault();
        var root = new RootViewModel(async () => await SqliteWorkspaceStore.OpenAsync(paths), new DesktopActions(paths.DataDirectory, () => { }), paths.DataDirectory,
            store => new DesktopChatSession((IChatStore)store, transport, vault));
        await root.InitializeAsync();
        var main = Assert.IsType<MainViewModel>(root.Content); var chat = Assert.IsType<ChatViewModel>(main.ActiveContent);
        Assert.Equal(conversation, chat.ConversationId); Assert.Equal("pending input", chat.InputText);
        Assert.Equal("checkpoint", chat.Messages.Last().Body); Assert.Equal(GenerationStatus.Interrupted, chat.Messages.Last().Status);
        Assert.Equal(0, transport.StartCount); await root.CloseAsync(); Assert.True(root.IsClosed);
    }

    [AvaloniaFact]
    public async Task Settings_failure_retains_masked_input_and_discovery_is_explicit()
    {
        await using var f = await F.OpenAsync();
        var settings = new SettingsViewModel(f.Chat, new(f.Chat, f.Vault, f.Transport), () => false);
        await settings.InitializeAsync(); settings.Secret = "synthetic-new-secret"; settings.ModelId = "hand-entered";
        var view = Assert.IsType<SettingsView>(new ViewLocator().Build(settings)); view.DataContext = settings;
        Assert.Equal('●', view.FindControl<TextBox>("SecretInput")!.PasswordChar);
        f.Sql("CREATE TRIGGER fail_settings BEFORE UPDATE ON active_ai_configuration BEGIN SELECT RAISE(ABORT,'fixture'); END");
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(settings.ErrorMessage); Assert.Equal("synthetic-new-secret", settings.Secret); Assert.Equal("hand-entered", settings.ModelId);
        Assert.Equal(0, f.Transport.StartCount); Assert.Null(f.Transport.DiscoverySecret);
        f.Sql("DROP TRIGGER fail_settings"); await settings.SaveCommand.ExecuteAsync(null);
        Assert.Null(settings.ErrorMessage); Assert.Equal("", settings.Secret); Assert.Equal("hand-entered", (await f.Chat.LoadActiveConfigurationAsync())!.ModelId);
    }

    [AvaloniaFact]
    public async Task Close_confirmation_precedes_mutation_and_failed_close_keeps_readable_body()
    {
        await using var f = await F.OpenAsync();
        var root = new RootViewModel(() => Task.FromResult<IWorkspaceStore>(f.Store), new DesktopActions(f.Paths.DataDirectory, () => { }), f.Paths.DataDirectory,
            store => new DesktopChatSession((IChatStore)store, f.Transport, f.Vault));
        await root.InitializeAsync(); var main = Assert.IsType<MainViewModel>(root.Content);
        main.NewWorkspaceName = "close"; await main.CreateWorkspaceAsync(); await main.NewConversationCommand.ExecuteAsync(null);
        var chat = Assert.IsType<ChatViewModel>(main.ActiveContent); chat.InputText = "q"; await chat.SendCommand.ExecuteAsync(null);
        var call = await f.Transport.NextAsync(); call.Emit("copyable latest");
        await WaitUiAsync(() => chat.Messages.LastOrDefault()?.Body == "copyable latest");
        await root.CloseAsync(); Assert.True(root.IsCloseConfirmationVisible); Assert.False(call.Cancellation.IsCancellationRequested);
        root.CancelCloseCommand.Execute(null); Assert.False(root.IsCloseConfirmationVisible); Assert.False(call.Cancellation.IsCancellationRequested);
        f.Sql("CREATE TRIGGER fail_exit BEFORE UPDATE ON generation_task BEGIN SELECT RAISE(ABORT,'fixture'); END");
        await root.ConfirmCloseCommand.ExecuteAsync(null);
        Assert.False(root.IsClosed); Assert.NotNull(root.CloseError); Assert.Same(main, root.Content);
        Assert.Equal("copyable latest", chat.Messages.Last().Body); Assert.True(call.Cancellation.IsCancellationRequested);
        root.ReturnToWorkbenchCommand.Execute(null); Assert.False(main.IsClosing); Assert.True(main.HasPersistenceFailure);
        await main.NewConversationCommand.ExecuteAsync(null); Assert.Same(chat, main.ActiveContent);
        f.Sql("DROP TRIGGER fail_exit"); await root.RetrySaveCommand.ExecuteAsync(null); Assert.False(main.HasPersistenceFailure);
        await root.CloseAsync(); Assert.True(root.IsClosed);
    }

    [AvaloniaFact]
    public async Task Confirmed_close_stops_work_even_when_draft_flush_fails_and_retry_close_saves_input()
    {
        await using var f = await F.OpenAsync();
        var root = new RootViewModel(() => Task.FromResult<IWorkspaceStore>(f.Store), new DesktopActions(f.Paths.DataDirectory, () => { }), f.Paths.DataDirectory,
            store => new DesktopChatSession((IChatStore)store, f.Transport, f.Vault));
        await root.InitializeAsync(); var main = Assert.IsType<MainViewModel>(root.Content);
        main.NewWorkspaceName = "draft close"; await main.CreateWorkspaceAsync(); await main.NewConversationCommand.ExecuteAsync(null);
        var chat = Assert.IsType<ChatViewModel>(main.ActiveContent); chat.InputText = "q"; await chat.SendCommand.ExecuteAsync(null);
        var call = await f.Transport.NextAsync(); chat.InputText = "unsaved next question";
        f.Sql("CREATE TRIGGER fail_draft BEFORE UPDATE ON conversation_draft BEGIN SELECT RAISE(ABORT,'fixture'); END");
        await root.CloseAsync(); await root.ConfirmCloseCommand.ExecuteAsync(null);
        Assert.True(call.Cancellation.IsCancellationRequested);
        Assert.False(root.IsClosed); Assert.Equal("unsaved next question", chat.InputText); Assert.NotNull(root.CloseError);
        Assert.True(main.HasPersistenceFailure);
        f.Sql("DROP TRIGGER fail_draft"); await root.RetrySaveCommand.ExecuteAsync(null);
        Assert.NotNull(root.CloseError); // Keep retry-exit and return actions visible after a repaired save.
        await root.RetryCloseCommand.ExecuteAsync(null);
        Assert.True(root.IsClosed);
        await using var reopened = await SqliteWorkspaceStore.OpenAsync(f.Paths);
        Assert.Equal("unsaved next question", (await reopened.LoadDraftAsync(chat.ConversationId)).Text);
        Assert.Equal(GenerationStatus.Stopped, (await reopened.ReadMessagesAsync(chat.ConversationId)).Last().Status);
    }

    [AvaloniaFact]
    public async Task Retry_renders_both_attempts_and_rejected_input_survives_navigation_and_restart()
    {
        await using var f = await F.OpenAsync();
        var main = CompositionRoot.CreateMain(f.Store, f.Coordinator, new(f.Chat, f.Vault, f.Transport));
        await main.InitializeAsync(); main.NewWorkspaceName = "retry"; await main.CreateWorkspaceAsync(); await main.NewConversationCommand.ExecuteAsync(null);
        var chat = Assert.IsType<ChatViewModel>(main.ActiveContent); var id = chat.ConversationId;
        chat.InputText = "q"; await chat.SendCommand.ExecuteAsync(null); var first = await f.Transport.NextAsync(); first.Emit("first partial");
        await WaitUiAsync(() => chat.Messages.LastOrDefault()?.Body == "first partial");
        await chat.StopCommand.ExecuteAsync(null); await chat.RetryCommand.ExecuteAsync(null); var retry = await f.Transport.NextAsync(); retry.Emit("retry partial");
        await WaitUiAsync(() => chat.Messages.LastOrDefault()?.Body == "retry partial"); await chat.StopCommand.ExecuteAsync(null);
        Assert.Equal(3, chat.Messages.Count); Assert.Equal("first partial", chat.Messages[1].Body); Assert.Equal("retry partial", chat.Messages[2].Body);
        chat.InputText = new string('x', 33000); await chat.SendCommand.ExecuteAsync(null);
        Assert.NotNull(chat.ErrorMessage); Assert.Equal(33000, chat.InputText.Length); Assert.Equal(3, (await f.Chat.ReadMessagesAsync(id)).Count);
        await main.NavigateAsync(new(MainArea.Settings)); await main.NavigateAsync(new(MainArea.Explore));
        var restored = Assert.IsType<ChatViewModel>(main.ActiveContent); Assert.Equal(id, restored.ConversationId); Assert.Equal(33000, restored.InputText.Length);
        Assert.Equal(id, (await f.Store.LoadSelectionAsync())!.TargetId);
        await main.PrepareCloseAsync(); main.DetachObservers();
    }

    [AvaloniaFact]
    public async Task Failed_generation_save_blocks_mutations_but_allows_reading_other_conversations()
    {
        await using var f = await F.OpenAsync();
        var main = CompositionRoot.CreateMain(f.Store, f.Coordinator, new(f.Chat, f.Vault, f.Transport));
        await main.InitializeAsync(); main.NewWorkspaceName = "reading"; await main.CreateWorkspaceAsync();
        await main.NewConversationCommand.ExecuteAsync(null); var firstId = Assert.IsType<ChatViewModel>(main.ActiveContent).ConversationId;
        await main.NewConversationCommand.ExecuteAsync(null); var chat = Assert.IsType<ChatViewModel>(main.ActiveContent);
        chat.InputText = "q"; await chat.SendCommand.ExecuteAsync(null); var call = await f.Transport.NextAsync(); call.Emit("keep visible");
        await WaitUiAsync(() => chat.Messages.LastOrDefault()?.Body == "keep visible");
        f.Sql("CREATE TRIGGER fail_checkpoint BEFORE UPDATE ON generation_task BEGIN SELECT RAISE(ABORT,'fixture'); END");
        await chat.StopCommand.ExecuteAsync(null); Assert.True(main.HasPersistenceFailure);
        await main.NewConversationCommand.ExecuteAsync(null); Assert.Same(chat, main.ActiveContent);
        await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, firstId));
        Assert.Equal(firstId, Assert.IsType<ChatViewModel>(main.ActiveContent).ConversationId);
        await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, chat.ConversationId));
        var restored = Assert.IsType<ChatViewModel>(main.ActiveContent); Assert.Equal("keep visible", restored.Messages.Last().Body);
        Assert.False(restored.CanSend);
        var view = Assert.IsType<ChatView>(new ViewLocator().Build(restored)); view.DataContext = restored;
        var window = new Window { Content = view }; window.Show();
        Assert.True(view.IsEnabled); window.Close();
        f.Sql("DROP TRIGGER fail_checkpoint"); await main.RetryPersistenceAsync(); Assert.True(restored.CanSend);
        main.DetachObservers();
    }

    [AvaloniaFact]
    public async Task Draft_and_selection_failure_still_allows_reading_and_retry_saves_all_memory_drafts()
    {
        await using var f = await F.OpenAsync();
        var main = CompositionRoot.CreateMain(f.Store, f.Coordinator, new(f.Chat, f.Vault, f.Transport));
        await main.InitializeAsync(); main.NewWorkspaceName = "draft navigation"; await main.CreateWorkspaceAsync();
        await main.NewConversationCommand.ExecuteAsync(null); var first = Assert.IsType<ChatViewModel>(main.ActiveContent);
        await main.NewConversationCommand.ExecuteAsync(null); var second = Assert.IsType<ChatViewModel>(main.ActiveContent);
        second.InputText = "keep despite unavailable storage";
        f.Sql("CREATE TRIGGER fail_draft BEFORE INSERT ON conversation_draft BEGIN SELECT RAISE(ABORT,'fixture'); END");
        f.Sql("CREATE TRIGGER fail_selection BEFORE UPDATE ON app_selection BEGIN SELECT RAISE(ABORT,'fixture'); END");
        await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, first.ConversationId));
        Assert.Equal(first.ConversationId, Assert.IsType<ChatViewModel>(main.ActiveContent).ConversationId);
        await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, second.ConversationId));
        var restored = Assert.IsType<ChatViewModel>(main.ActiveContent);
        Assert.Equal("keep despite unavailable storage", restored.InputText); Assert.True(main.HasPersistenceFailure);
        await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, first.ConversationId));
        f.Sql("DROP TRIGGER fail_draft"); f.Sql("DROP TRIGGER fail_selection"); await main.RetryPersistenceAsync();
        Assert.False(main.HasPersistenceFailure);
        Assert.Equal("keep despite unavailable storage", (await f.Chat.LoadDraftAsync(second.ConversationId)).Text);
        Assert.Equal(first.ConversationId, (await f.Store.LoadSelectionAsync())!.TargetId);
        main.DetachObservers();
    }

    [AvaloniaFact]
    public async Task Mounted_settings_keeps_manual_model_and_failed_discovery_allows_save()
    {
        await using var f = await F.OpenAsync();
        var transport = new FailedDiscoveryTransport();
        var settings = new SettingsViewModel(f.Chat, new(f.Chat, f.Vault, transport), () => false);
        await settings.InitializeAsync(); settings.ModelId = "manual-model";
        var view = Assert.IsType<SettingsView>(new ViewLocator().Build(settings)); view.DataContext = settings;
        var window = new Window { Content = view, Width = 1100, Height = 760 }; window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal("manual-model", settings.ModelId);
            await settings.DiscoverCommand.ExecuteAsync(null); Assert.NotNull(settings.ErrorMessage); Assert.Equal("manual-model", settings.ModelId);
            await settings.SaveCommand.ExecuteAsync(null); Assert.Null(settings.ErrorMessage);
            Assert.Equal("manual-model", (await f.Chat.LoadActiveConfigurationAsync())!.ModelId);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task Close_waits_for_inflight_configuration_save_before_disposing_store()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        var store = await SqliteWorkspaceStore.OpenAsync(paths); await store.CreateAsync("saving settings");
        var vault = new BlockingVault(); var transport = new FailedDiscoveryTransport();
        var root = new RootViewModel(() => Task.FromResult<IWorkspaceStore>(store), new DesktopActions(paths.DataDirectory, () => { }), paths.DataDirectory,
            chat => new DesktopChatSession((IChatStore)chat, transport, vault));
        await root.InitializeAsync(); var main = Assert.IsType<MainViewModel>(root.Content);
        await main.NavigateAsync(new(MainArea.Settings)); var settings = Assert.IsType<SettingsViewModel>(main.ActiveContent);
        settings.Secret = "synthetic"; var saving = settings.SaveCommand.ExecuteAsync(null); await vault.Entered.Task;
        var closing = root.CloseAsync(); await Task.Yield();
        var closedBeforeSave = root.IsClosed;
        vault.Release.TrySetResult(); await saving; await closing;
        Assert.False(closedBeforeSave); Assert.Null(settings.ErrorMessage); Assert.True(root.IsClosed);
        await using var reopened = await SqliteWorkspaceStore.OpenAsync(paths); Assert.NotNull(await reopened.LoadActiveConfigurationAsync());
    }
    [AvaloniaFact]
    public async Task Automatic_title_updates_list_and_header_without_overwriting_manual_edit()
    {
        await using var f = await F.OpenAsync();
        var main = CompositionRoot.CreateMain(f.Store, f.Coordinator, new(f.Chat, f.Vault, f.Transport));
        await main.InitializeAsync(); main.NewWorkspaceName = "titles"; await main.CreateWorkspaceAsync(); await main.NewConversationCommand.ExecuteAsync(null);
        var chat = Assert.IsType<ChatViewModel>(main.ActiveContent); chat.InputText = "q"; await chat.SendCommand.ExecuteAsync(null);
        var reply = await f.Transport.NextAsync(); reply.Emit("answer", "stop"); reply.Finish();
        var title = await f.Transport.NextAsync(); title.Emit("automatic title", "stop"); title.Finish();
        await WaitUiAsync(() => main.Conversations.Single().Title == "automatic title");
        Assert.Equal("automatic title", chat.TitleText);
        chat.TitleText = "manual title";
        await f.Coordinator.RetryPersistenceAsync(); Dispatcher.UIThread.RunJobs();
        Assert.Equal("manual title", chat.TitleText); await chat.RenameCommand.ExecuteAsync(null);
        Assert.Equal("manual title", (await f.Chat.LoadConversationAsync(chat.ConversationId))!.Title);
        main.DetachObservers();
    }

    private sealed class BlockingVault : ICredentialVault
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> WriteNewAsync(string secret) { Entered.TrySetResult(); await Release.Task; return "v1"; }
        public Task<string> ReadAsync(string reference) => Task.FromResult("synthetic");
        public Task DeleteAsync(string reference) => Task.CompletedTask;
    }

    private sealed class FailedDiscoveryTransport : IChatTransport
    {
        public IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string secret, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected generation");
        public Task<IReadOnlyList<string>> ListModelsAsync(AiConfiguration configuration, string secret, CancellationToken cancellationToken) => throw new ChatTransportException("DiscoveryFailed");
    }

    [AvaloniaFact]
    public async Task Startup_refuses_cross_workspace_restoration_even_if_selection_was_tampered()
    {
        await using var f = await F.OpenAsync();
        var first = await f.Store.CreateAsync("first"); var foreign = await f.Store.CreateAsync("second");
        var conversation = await f.Chat.CreateConversationAsync(foreign.Id);
        await f.Store.SaveSelectionAsync(new(first.Id, MainArea.Explore));
        // Test-owned corruption bypasses only the selection validation trigger; IDs remain real.
        f.Sql("DROP TRIGGER selection_detail_update");
        f.Sql($"UPDATE app_selection SET kind='Conversation',target_id='{conversation.Id:D}'");
        var main = CompositionRoot.CreateMain(f.Store, f.Coordinator, new(f.Chat, f.Vault, f.Transport));
        await main.InitializeAsync(); Assert.Null(main.ErrorMessage); Assert.Equal(first.Id, main.CurrentWorkspace!.Id);
        Assert.Equal(ContentKind.Overview, main.ActiveContent!.Request.Kind); Assert.Null(main.ActiveContent.Request.TargetId);
        main.DetachObservers();
    }

    [AvaloniaFact]
    public async Task Navigation_waiting_for_draft_write_preserves_last_edit_and_freezes_binding()
    {
        await using var f = await F.OpenAsync(); var delayed = new ControlledChatStore(f.Store);
        var main = CompositionRoot.CreateMain(delayed, f.Coordinator, new(delayed, f.Vault, f.Transport));
        await main.InitializeAsync(); main.NewWorkspaceName = "delayed draft"; await main.CreateWorkspaceAsync(); await main.NewConversationCommand.ExecuteAsync(null);
        var chat = Assert.IsType<ChatViewModel>(main.ActiveContent); var id = chat.ConversationId;
        var view = Assert.IsType<ChatView>(new ViewLocator().Build(chat)); view.DataContext = chat;
        var window = new Window { Content = view }; window.Show();
        try
        {
            chat.InputText = "first edit"; delayed.DelayNextDraft = true;
            var navigating = main.NavigateAsync(new(MainArea.Settings)); await delayed.DraftEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Dispatcher.UIThread.RunJobs(); var inputWasFrozen = view.FindControl<TextBox>("ChatInput")!.IsReadOnly;
            // Also defend an input notification already queued when navigation began.
            chat.InputText = "last edit while first write awaited";
            delayed.DraftRelease.TrySetResult(); await navigating;
            Assert.True(inputWasFrozen);
            await main.NavigateAsync(new(MainArea.Explore));
            Assert.Equal("last edit while first write awaited", Assert.IsType<ChatViewModel>(main.ActiveContent).InputText);
            Assert.Equal("last edit while first write awaited", (await f.Chat.LoadDraftAsync(id)).Text);
            await main.PrepareCloseAsync(); await main.SaveDraftForCloseAsync(); main.DetachObservers();
            await f.Coordinator.DisposeAsync(); await f.Store.DisposeAsync();
            await using var reopened = await SqliteWorkspaceStore.OpenAsync(f.Paths);
            Assert.Equal("last edit while first write awaited", (await reopened.LoadDraftAsync(id)).Text);
        }
        finally { delayed.DraftRelease.TrySetResult(); main.DetachObservers(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepted_send_read_failure_repairs_tombstone_before_one_new_edit(bool failDraftRead)
    {
        await using var f = await F.OpenAsync(); var delayed = new ControlledChatStore(f.Store);
        var main = CompositionRoot.CreateMain(delayed, f.Coordinator, new(delayed, f.Vault, f.Transport));
        await main.InitializeAsync(); main.NewWorkspaceName = "accepted read"; await main.CreateWorkspaceAsync(); await main.NewConversationCommand.ExecuteAsync(null);
        var chat = Assert.IsType<ChatViewModel>(main.ActiveContent); var id = chat.ConversationId;
        chat.InputText = "already accepted question";
        delayed.FailNextMessages = !failDraftRead; delayed.FailNextDraftRead = failDraftRead;
        await chat.SendCommand.ExecuteAsync(null);
        Assert.Equal(2, (await f.Chat.ReadMessagesAsync(id)).Count);
        var tombstone = await f.Chat.LoadDraftAsync(id); Assert.Equal("", tombstone.Text);
        Assert.Equal("", chat.InputText); Assert.Contains("已发送", chat.ErrorMessage);
        Assert.True(main.HasPersistenceFailure); Assert.False(chat.CanSend);
        await main.RetryPersistenceAsync();
        Assert.False(main.HasPersistenceFailure); Assert.Null(chat.ErrorMessage); Assert.Equal(2, chat.Messages.Count);
        await f.Transport.NextAsync(); // External pause may have won before initial dispatch.
        Assert.Equal(tombstone.Revision, chat.CurrentDraft.Revision);
        chat.InputText = "exactly one later edit";
        await main.NavigateAsync(new(MainArea.Settings)); await main.NavigateAsync(new(MainArea.Explore));
        var restored = Assert.IsType<ChatViewModel>(main.ActiveContent);
        Assert.Equal("exactly one later edit", restored.InputText);
        Assert.Equal("exactly one later edit", (await f.Chat.LoadDraftAsync(id)).Text);
        Assert.True((await f.Chat.LoadDraftAsync(id)).Revision > tombstone.Revision);
        Assert.Equal(1, f.Transport.StartCount); Assert.Equal(2, (await f.Chat.ReadMessagesAsync(id)).Count);
        await main.PrepareCloseAsync(); await main.SaveDraftForCloseAsync(); main.DetachObservers();
        await f.Coordinator.DisposeAsync(); await f.Store.DisposeAsync();
        await using var reopened = await SqliteWorkspaceStore.OpenAsync(f.Paths);
        Assert.Equal("exactly one later edit", (await reopened.LoadDraftAsync(id)).Text);
        Assert.Single(await reopened.ReadMessagesAsync(id), m => m.Role == MessageRole.User && m.Body == "already accepted question");
    }

    [AvaloniaFact]
    public async Task Delayed_rename_preserves_new_edit_through_refresh_and_explicit_second_save()
    {
        await using var f = await F.OpenAsync(); var delayed = new ControlledChatStore(f.Store);
        var main = CompositionRoot.CreateMain(delayed, f.Coordinator, new(delayed, f.Vault, f.Transport));
        await main.InitializeAsync(); main.NewWorkspaceName = "rename baseline"; await main.CreateWorkspaceAsync(); await main.NewConversationCommand.ExecuteAsync(null);
        var chat = Assert.IsType<ChatViewModel>(main.ActiveContent);
        chat.TitleText = "submitted A"; delayed.DelayNextRename = true;
        var saving = chat.RenameCommand.ExecuteAsync(null); await delayed.RenameEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        chat.TitleText = "newer B"; delayed.RenameRelease.TrySetResult(); await saving;
        Assert.Equal("submitted A", (await f.Chat.LoadConversationAsync(chat.ConversationId))!.Title);
        await f.Coordinator.RetryPersistenceAsync();
        await WaitUiAsync(() => main.Conversations.Single().Title == "submitted A");
        Assert.Equal("newer B", chat.TitleText);
        await chat.RenameCommand.ExecuteAsync(null);
        Assert.Equal("newer B", (await f.Chat.LoadConversationAsync(chat.ConversationId))!.Title);
        main.DetachObservers();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Draft_failure_pauses_global_queue_and_titles_until_all_local_state_is_repaired(bool exitAndReturn)
    {
        await using var f = await F.OpenAsync();
        var main = CompositionRoot.CreateMain(f.Store, f.Coordinator, new(f.Chat, f.Vault, f.Transport));
        await main.InitializeAsync(); main.NewWorkspaceName = "global draft pause"; await main.CreateWorkspaceAsync();
        await main.NewConversationCommand.ExecuteAsync(null); var a = Assert.IsType<ChatViewModel>(main.ActiveContent);
        a.InputText = "question A"; await a.SendCommand.ExecuteAsync(null); var callA = await f.Transport.NextAsync();
        await main.NewConversationCommand.ExecuteAsync(null); var b = Assert.IsType<ChatViewModel>(main.ActiveContent);
        b.InputText = "question B"; await b.SendCommand.ExecuteAsync(null); var callB = await f.Transport.NextAsync();
        await main.NewConversationCommand.ExecuteAsync(null); var c = Assert.IsType<ChatViewModel>(main.ActiveContent);
        c.InputText = "question C"; await c.SendCommand.ExecuteAsync(null);
        await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, a.ConversationId));
        a = Assert.IsType<ChatViewModel>(main.ActiveContent); a.InputText = "unsaved next draft";
        f.Sql($"CREATE TRIGGER fail_only_draft BEFORE UPDATE ON conversation_draft WHEN NEW.conversation_id='{a.ConversationId:D}' BEGIN SELECT RAISE(ABORT,'fixture'); END");
        await main.NavigateAsync(new(MainArea.Settings));
        Assert.True(main.HasPersistenceFailure); Assert.True(f.Coordinator.HasPersistenceFailure);
        var redundantUiNotifications = 0;
        void ObserveReport() { if (Dispatcher.UIThread.CheckAccess()) redundantUiNotifications++; }
        f.Coordinator.Changed += ObserveReport;
        f.Coordinator.ReportPersistenceFailure(); f.Coordinator.ReportPersistenceFailure();
        f.Coordinator.Changed -= ObserveReport;
        Assert.Equal(0, redundantUiNotifications);
        Assert.False(callA.Cancellation.IsCancellationRequested); Assert.False(callB.Cancellation.IsCancellationRequested);
        if (exitAndReturn)
        {
            await f.Coordinator.ShutdownAsync(); main.ReturnToWorkbench();
            Assert.True(f.Coordinator.HasPersistenceFailure);
            Assert.All(f.Coordinator.Snapshots, snapshot => Assert.Equal(GenerationStatus.Stopped, snapshot.Status));
        }
        else
        {
            var answerA = f.Coordinator.Snapshots.Single(snapshot => snapshot.ConversationId == a.ConversationId && snapshot.Kind == GenerationKind.Reply).TaskId;
            callA.Emit("answer A", "stop"); callA.Finish();
            await f.WaitAsync(() => f.Snapshot(answerA).Status == GenerationStatus.Completed && !f.Snapshot(answerA).HasUnsavedChanges);
            Assert.Equal("answer A", (await f.Chat.ReadMessagesAsync(a.ConversationId)).Last().Body);
            Assert.False(callB.Cancellation.IsCancellationRequested);
        }
        Assert.Equal(2, f.Transport.StartCount);
        Assert.DoesNotContain(f.Coordinator.Snapshots, snapshot => snapshot.Kind == GenerationKind.Title);
        Assert.Equal(exitAndReturn ? GenerationStatus.Stopped : GenerationStatus.Queued,
            f.Coordinator.Snapshots.Single(snapshot => snapshot.ConversationId == c.ConversationId).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.SendAsync(a.ConversationId, "must not accept"));
        await Assert.ThrowsAnyAsync<Exception>(() => main.RetryPersistenceAsync());
        Assert.True(f.Coordinator.HasPersistenceFailure); Assert.Equal(2, f.Transport.StartCount);
        f.Sql("DROP TRIGGER fail_only_draft");
        // Removing the fault or logical Return must never release an external persistence pause.
        main.ReturnToWorkbench();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.RetryPersistenceAsync());
        Assert.Equal(2, f.Transport.StartCount); Assert.True(f.Coordinator.HasPersistenceFailure);
        await main.RetryPersistenceAsync();
        Assert.False(main.HasPersistenceFailure); Assert.False(f.Coordinator.HasPersistenceFailure);
        Assert.Equal("unsaved next draft", (await f.Chat.LoadDraftAsync(a.ConversationId)).Text);
        if (exitAndReturn)
        {
            Assert.Equal(2, f.Transport.StartCount);
            Assert.All(f.Coordinator.Snapshots, snapshot => Assert.Equal(GenerationStatus.Stopped, snapshot.Status));
        }
        else
        {
            var callC = await f.Transport.NextAsync();
            Assert.Equal("question C", callC.Request.Messages.Last().Content); Assert.Equal(3, f.Transport.StartCount);
            Assert.Single(f.Coordinator.Snapshots, snapshot => snapshot.Kind == GenerationKind.Title && snapshot.Status == GenerationStatus.Queued);
            callB.Emit("answer B", "stop"); callB.Finish();
            var titleA = await f.Transport.NextAsync();
            Assert.Equal("system", titleA.Request.Messages[0].Role);
            Assert.Contains(titleA.Request.Messages, message => message.Content == "answer A");
            Assert.Equal(4, f.Transport.StartCount);
        }
        main.DetachObservers();
    }

    [AvaloniaFact]
    public async Task Contended_external_repair_keeps_accepted_message_projection_on_ui_context()
    {
        await using var f = await F.OpenAsync(); var controlled = new ControlledChatStore(f.Store);
        var main = CompositionRoot.CreateMain(controlled, f.Coordinator, new(controlled, f.Vault, f.Transport));
        await main.InitializeAsync(); main.NewWorkspaceName = "repair affinity"; await main.CreateWorkspaceAsync(); await main.NewConversationCommand.ExecuteAsync(null);
        var chat = Assert.IsType<ChatViewModel>(main.ActiveContent); chat.InputText = "accepted question"; controlled.FailNextMessages = true;
        await chat.SendCommand.ExecuteAsync(null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Hold the real repair gate so Main must await it before reloading its accepted state.
        var firstRepair = f.Coordinator.RetryPersistenceAsync(async () => { entered.TrySetResult(); await release.Task; });
        await entered.Task;
        var projectedOffUi = false;
        chat.Messages.CollectionChanged += (_, _) => projectedOffUi |= !Dispatcher.UIThread.CheckAccess();
        var mainRepair = main.RetryPersistenceAsync(); release.TrySetResult();
        await Task.WhenAll(firstRepair, mainRepair);
        Assert.False(projectedOffUi); Assert.Equal(2, chat.Messages.Count);
        Assert.Null(chat.ErrorMessage); Assert.False(main.HasPersistenceFailure);
        main.DetachObservers();
    }

    // Only fault/delay the Desktop boundary. Every read/write otherwise delegates to real SQLite;
    // coordinator uses that same underlying SQLite store with its real acceptance transaction.
    internal sealed class ControlledChatStore(SqliteWorkspaceStore inner) : IWorkspaceStore, IChatStore, Mindweave.Application.Branches.IBranchStore
    {
        public bool FailContextUsage { get; set; }
        public Task<IReadOnlyList<ContextUsageMetadata>> LoadContextUsageAsync(Guid conversationId, IReadOnlyList<Guid> ids)
        {
            if (FailContextUsage) throw new IOException("test-only optional metadata unavailable");
            return inner.LoadContextUsageAsync(conversationId, ids);
        }
        public Task<ReplyContextPlan?> LoadContextPlanAsync(Guid id) => inner.LoadContextPlanAsync(id);
        public Task<CompletedContextSummary?> LoadCompletedSummaryAsync(Guid id, string key) => inner.LoadCompletedSummaryAsync(id, key);
        public bool LoseCompressionStartReturn { get; set; }
        public bool LoseCompressionWriteReturn { get; set; }
        public Func<GenerationStatus, Task>? BeforeCompressionWrite { get; set; }
        public Func<Guid, string, Task>? BeforeCompressionStart { get; set; }
        public async Task<ContextCompressionAttempt> StartCompressionAsync(Guid id, string key)
        {
            if (BeforeCompressionStart is { } before) await before(id, key);
            var attempt = await inner.StartCompressionAsync(id, key);
            if (LoseCompressionStartReturn) { LoseCompressionStartReturn = false; throw new IOException("lost compression start return"); }
            return attempt;
        }
        public Task<ContextCompressionAttempt?> LoadCompressionAttemptAsync(Guid id) => inner.LoadCompressionAttemptAsync(id);
        public Task<ContextCompressionAttempt?> LoadCompressionAttemptForReplyAsync(Guid id, string key) => inner.LoadCompressionAttemptForReplyAsync(id, key);
        public async Task<bool> WriteCompressionAsync(Guid taskId, Guid id, long revision, string payload, GenerationStatus status, string? finishReason = null, string? errorCode = null, string? actualModelId = null)
        {
            if (BeforeCompressionWrite is { } before) await before(status);
            var saved = await inner.WriteCompressionAsync(taskId, id, revision, payload, status, finishReason, errorCode, actualModelId);
            if (status == GenerationStatus.Completed && LoseCompressionWriteReturn) { LoseCompressionWriteReturn = false; throw new IOException("lost compression commit return"); }
            return saved;
        }
        public Task<PreparedReplyRequest> PrepareReplyRequestAsync(Guid id) => inner.PrepareReplyRequestAsync(id);
        public Task<PreparedReplyRequest?> LoadPreparedReplyRequestAsync(Guid id) => inner.LoadPreparedReplyRequestAsync(id);
        public bool LoseBranchReturn { get; set; }
        public bool FailNextReadState { get; set; }
        public async Task<Conversation> CreateBranchAsync(Guid parent, Mindweave.Core.Branches.SourceSelection selection, Guid operation, Mindweave.Core.Branches.ConversationReadState? state = null)
        {
            var child = await inner.CreateBranchAsync(parent, selection, operation, state);
            if (LoseBranchReturn) { LoseBranchReturn = false; throw new IOException("test-only committed branch return lost"); }
            return child;
        }
        public Task<Mindweave.Core.Branches.BranchSource?> LoadBranchSourceAsync(Guid id) => inner.LoadBranchSourceAsync(id);
        public Task<IReadOnlyList<Mindweave.Core.Branches.BranchSource>> ListChildBranchesAsync(Guid id) => inner.ListChildBranchesAsync(id);
        public Task<IReadOnlyList<Mindweave.Core.Branches.FrozenContextRange>> ReadFrozenContextRangesAsync(Guid id) => inner.ReadFrozenContextRangesAsync(id);
        public Task<Mindweave.Core.Branches.ConversationReadState?> LoadReadStateAsync(Guid id)
        {
            if (FailNextReadState) { FailNextReadState = false; throw new IOException("test-only branch navigation read failure"); }
            return inner.LoadReadStateAsync(id);
        }
        public Task SaveReadStateAsync(Mindweave.Core.Branches.ConversationReadState state) => inner.SaveReadStateAsync(state);
        public bool DelayNextDraft { get; set; }
        public bool DelayNextRename { get; set; }
        public TaskCompletionSource RenameEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RenameRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailNextMessages { get; set; }
        public bool FailNextDraftRead { get; set; }
        public TaskCompletionSource DraftEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DraftRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<Workspace>> ListAsync() => inner.ListAsync();
        public Task<Workspace> CreateAsync(string name) => inner.CreateAsync(name);
        public Task<WorkspaceSelection?> LoadSelectionAsync() => inner.LoadSelectionAsync();
        public Task SaveSelectionAsync(WorkspaceSelection selection) => inner.SaveSelectionAsync(selection);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public Task<IReadOnlyList<Conversation>> ListConversationsAsync(Guid id) => inner.ListConversationsAsync(id);
        public Task<Conversation> CreateConversationAsync(Guid id) => inner.CreateConversationAsync(id);
        public Task<Conversation?> LoadConversationAsync(Guid id) => inner.LoadConversationAsync(id);
        public async Task RenameConversationAsync(Guid id, string title)
        {
            if (DelayNextRename) { DelayNextRename = false; RenameEntered.TrySetResult(); await RenameRelease.Task; }
            await inner.RenameConversationAsync(id, title);
        }
        public Task<ChatContext> ReadContextAsync(Guid id) => inner.ReadContextAsync(id);
        public Task<IReadOnlyList<ChatMessage>> ReadMessagesAsync(Guid id)
        {
            if (FailNextMessages) { FailNextMessages = false; throw new IOException("test-only postaccept read failure"); }
            return inner.ReadMessagesAsync(id);
        }
        public Task<AiConfiguration?> LoadActiveConfigurationAsync() => inner.LoadActiveConfigurationAsync();
        public Task SaveConfigurationAsync(AiConfiguration configuration) => inner.SaveConfigurationAsync(configuration);
        public Task<GenerationWork> AcceptReplyAsync(Guid id, string? text, AiConfiguration configuration) => inner.AcceptReplyAsync(id, text, configuration);
        public Task<GenerationWork?> AcceptTitleAsync(Guid id, AiConfiguration configuration) => inner.AcceptTitleAsync(id, configuration);
        public Task<bool> WriteGenerationAsync(Guid id, long revision, string body, GenerationStatus status, string? errorCode = null, string? actualModelId = null) => inner.WriteGenerationAsync(id, revision, body, status, errorCode, actualModelId);
        public Task<bool> CompleteTitleAsync(Guid id, string title) => inner.CompleteTitleAsync(id, title);
        public Task<GenerationRecord?> LoadGenerationAsync(Guid id) => inner.LoadGenerationAsync(id);
        public Task<IReadOnlyList<GenerationRecord>> ListActiveGenerationsAsync() => inner.ListActiveGenerationsAsync();
        public Task RecoverInterruptedAsync() => inner.RecoverInterruptedAsync();
        public Task<ConversationDraft> LoadDraftAsync(Guid id)
        {
            if (FailNextDraftRead) { FailNextDraftRead = false; throw new IOException("test-only postaccept draft read failure"); }
            return inner.LoadDraftAsync(id);
        }
        public async Task SaveDraftAsync(Guid id, string text, long revision)
        {
            if (DelayNextDraft) { DelayNextDraft = false; DraftEntered.TrySetResult(); await DraftRelease.Task; }
            await inner.SaveDraftAsync(id, text, revision);
        }
    }

    private static async Task WaitUiAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, deadline.Token); }
    }
}
