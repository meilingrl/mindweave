using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Chat;
using Mindweave.Application.Navigation;
using Mindweave.Application.Storage;
using Mindweave.Core.Chat;
using Mindweave.Desktop;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.Views;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Xunit;

namespace Mindweave.Tests;

public sealed partial class SummaryLifecycleDesktopTests
{
    private const string Payload = "{\"Title\":\"Retained title\",\"Markdown\":\"# Complete body\\nretained evidence\"}";

    [AvaloniaFact]
    public async Task First_queued_summary_can_be_stopped_from_reopened_chat_without_starting_transport()
    {
        await using var f = await Fixture.Open();
        for (var i = 0; i < 2; i++)
        {
            var other = await f.Store.CreateConversationAsync(f.Workspace);
            await f.Session.Coordinator.SendAsync(other.Id, "hold physical slot");
            await f.Transport.NextAsync();
        }
        var view = await f.OpenChat(); await Click(view, "GenerateSummaryButton");
        var task = Assert.Single(f.Session.Summaries.Snapshots);
        Assert.Equal(GenerationStatus.Queued, task.Status);
        await f.Main.NavigateAsync(new(MainArea.Network));
        view = await f.OpenChat();
        var stop = view.FindControl<Button>("StopSummaryButton"); Assert.NotNull(stop); Assert.True(stop.IsEnabled);
        await Click(view, "StopSummaryButton");
        Assert.Equal(GenerationStatus.Stopped, Assert.Single(f.Session.Summaries.Snapshots).Status);
        Assert.Equal(2, f.Transport.StartCount); Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_node"));
        Assert.Contains("已停止", view.FindControl<TextBlock>("SummaryStatusNotice")!.Text!);
        Assert.True(view.FindControl<Button>("GenerateSummaryButton")!.IsEnabled);
    }

    [AvaloniaTheory]
    [InlineData("invalid", "完整有效")]
    [InlineData("length", "完整有效")]
    [InlineData("transport", "连接")]
    [InlineData("credential", "凭据")]
    public async Task First_summary_failure_is_visible_and_retryable_without_saved_version(string mode, string reason)
    {
        await using var f = await Fixture.Open();
        if (mode == "credential") f.Vault.Secrets.Clear();
        var view = await f.OpenChat(); await Click(view, "GenerateSummaryButton");
        if (mode != "credential")
        {
            var call = await f.Transport.NextAsync();
            if (mode == "transport") call.Fail("SyntheticFailure");
            else { call.Emit(mode == "invalid" ? "{bad}" : Payload, mode == "length" ? "length" : "stop"); call.Finish(); }
        }
        await Wait(() => f.Session.Summaries.Snapshots.Single().Status == GenerationStatus.Failed && !f.Session.Summaries.Snapshots.Single().HasUnsavedChanges);
        await f.Main.DrainBackgroundRefreshesAsync(); Layout();
        var status = view.FindControl<TextBlock>("SummaryStatusNotice"); Assert.NotNull(status);
        Assert.True(status.IsVisible); Assert.Contains("失败", status.Text!); Assert.Contains(reason, status.Text!);
        Assert.True(view.FindControl<Button>("GenerateSummaryButton")!.IsEnabled);
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_version"));
    }

    [AvaloniaFact]
    public async Task Candidate_metadata_refresh_uses_selected_coverage_and_preserves_body_and_read_epoch()
    {
        await using var f = await SummaryStorageFixture.Open();
        await BranchStorageTests.Answer(f.Store, f.Conversation, "second", "second answer");
        var candidate = await f.Generate("Candidate covering both");
        var page = new SummaryViewModel(f.Workspace, f.Node, f.Versions, new(f.Versions, f.Node, () => { }), () => false, () => { }, _ => Task.CompletedTask);
        await page.LoadAsync(candidate.VersionId);
        var view = new SummaryView { DataContext = page, AutomaticObservation = false };
        var window = new Window { Content = view, Width = 1000, Height = 800 }; window.Show(); Layout();
        try
        {
            await view.ObserveNowAsync(true); var epoch = page.Reading!.State.Epoch; var body = page.SavedBody;
            Assert.DoesNotContain("此后有新内容", page.Metadata);
            Assert.True((await f.Snapshot()).HasLaterContent);
            await page.RefreshMetadataAsync(); Layout();
            Assert.DoesNotContain("此后有新内容", page.Metadata);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == page.Metadata);
            await BranchStorageTests.Answer(f.Store, f.Conversation, "third", "third answer");
            await page.RefreshMetadataAsync(); Layout(); Assert.Contains("此后有新内容", page.Metadata);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == page.Metadata);
            Assert.Equal(body, page.SavedBody); Assert.Equal(epoch, page.Reading!.State.Epoch);
            Assert.Equal(candidate.VersionId, page.SelectedVersion!.Identity.Id); Assert.Equal(candidate.VersionId, page.Version!.Header.Identity.Id);
        }
        finally { page.Detach(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Complete_retained_summary_is_selectable_across_navigation_failed_close_and_exact_save_repair()
    {
        await using var f = await Fixture.Open();
        f.Sql("CREATE TRIGGER integrated_summary_save_fault BEFORE INSERT ON summary_version BEGIN SELECT RAISE(ABORT,'synthetic completion failure'); END");
        var view = await f.OpenChat(); await Click(view, "GenerateSummaryButton");
        var call = await f.Transport.NextAsync(); call.Emit(Payload, "stop"); call.Finish();
        await Wait(() => f.Session.Runtime.HasPersistenceFailure && f.Session.Summaries.Snapshots.Single().RetainedContent is not null);
        await f.Main.DrainBackgroundRefreshesAsync(); Layout();
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_node")); Assert.True(f.Main.HasPersistenceFailure);
        AssertRetained(f);
        await f.Main.NavigateAsync(new(MainArea.Network)); Layout(); AssertRetained(f);
        await f.Main.SwitchWorkspaceAsync(f.OtherWorkspace); Layout(); AssertRetained(f);
        Assert.Equal(f.OtherWorkspace, f.Main.CurrentWorkspace!.Id);
        var retained = Assert.Single(f.Main.RetainedSummaries);
        Assert.Equal(f.Workspace, retained.WorkspaceId); Assert.Equal(f.Conversation, retained.ConversationId);
        Assert.Equal(Assert.Single(f.Session.Summaries.Snapshots).TaskId, retained.TaskId);
        await f.Root.CloseAsync(); await f.Root.ConfirmCloseCommand.ExecuteAsync(null);
        Assert.False(f.Root.IsClosed); Assert.NotNull(f.Root.CloseError);
        f.Root.ReturnToWorkbenchCommand.Execute(null); Layout(); AssertRetained(f);
        f.Sql("DROP TRIGGER integrated_summary_save_fault");
        await f.Root.RetrySaveCommand.ExecuteAsync(null); await f.Main.DrainBackgroundRefreshesAsync(); Layout();
        Assert.False(f.Main.HasPersistenceFailure); Assert.Equal(1L, f.Sql("SELECT count(*) FROM summary_version"));
        Assert.Equal(1, f.Transport.StartCount);
        Assert.DoesNotContain(f.View.GetVisualDescendants().OfType<TextBox>(), t => t.Name == "RetainedSummaryBody" && t.IsEffectivelyVisible);
        Assert.Equal(0L, f.Sql("SELECT is_read FROM summary_read_state"));
    }

    private static void AssertRetained(Fixture f)
    {
        var title = f.View.GetVisualDescendants().OfType<TextBox>().SingleOrDefault(t => t.Name == "RetainedSummaryTitle" && t.IsEffectivelyVisible);
        var body = f.View.GetVisualDescendants().OfType<TextBox>().SingleOrDefault(t => t.Name == "RetainedSummaryBody" && t.IsEffectivelyVisible);
        Assert.NotNull(title); Assert.NotNull(body); Assert.True(title.IsReadOnly); Assert.True(body.IsReadOnly);
        Assert.Equal("Retained title", title.Text); Assert.Equal("# Complete body\nretained evidence", body.Text);
        body.SelectAll(); Assert.Equal(body.Text, body.SelectedText);
        Assert.Contains(f.View.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text?.Contains("尚未确认保存") == true);
    }
    private static async Task Click(Control view, string name)
    {
        var button = view.FindControl<Button>(name); Assert.NotNull(button); Assert.True(button.IsEnabled);
        await Assert.IsAssignableFrom<IAsyncRelayCommand>(button.Command).ExecuteAsync(button.CommandParameter); Layout();
    }
    private static void Layout() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static async Task Wait(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) { Layout(); await Task.Delay(10, timeout.Token); }
        Layout();
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TestDirectory directory = new();
        public AppPaths Paths { get; }
        public SqliteWorkspaceStore Store { get; private set; } = null!;
        public GenerationCoordinatorTests.ControlledTransport Transport { get; } = new();
        public GenerationCoordinatorTests.MemoryVault Vault { get; } = new();
        public DesktopChatSession Session { get; private set; } = null!;
        public RootViewModel Root { get; private set; } = null!;
        public MainViewModel Main { get; private set; } = null!;
        public MainView View { get; private set; } = null!;
        public Window Window { get; private set; } = null!;
        public Guid Workspace { get; private set; }
        public Guid Conversation { get; private set; }
        public Guid OtherWorkspace { get; private set; }
        public Guid OtherConversation { get; private set; }
        private Fixture() => Paths = AppPaths.ForTrack("codex-steady", directory.Path);
        public static async Task<Fixture> Open(bool recovered = false, Func<SqliteWorkspaceStore, IWorkspaceStore>? wrap = null)
        {
            var f = new Fixture(); f.Store = await SqliteWorkspaceStore.OpenAsync(f.Paths);
            var config = BranchStorageTests.Config() with { CredentialReference = "v1" }; await f.Store.SaveConfigurationAsync(config);
            f.Workspace = (await f.Store.CreateAsync("Synthetic lifecycle")).Id;
            f.Conversation = (await f.Store.CreateConversationAsync(f.Workspace)).Id;
            await BranchStorageTests.Answer(f.Store, f.Conversation, "question", "answer");
            f.OtherWorkspace = (await f.Store.CreateAsync("Other synthetic workspace")).Id;
            f.OtherConversation = (await f.Store.CreateConversationAsync(f.OtherWorkspace)).Id;
            await BranchStorageTests.Answer(f.Store, f.OtherConversation, "other question", "other answer");
            if (recovered) await f.Store.AcceptKnowledgeSummaryAsync(Guid.NewGuid(), f.Conversation, config);
            f.Root = new(() => Task.FromResult(wrap?.Invoke(f.Store) ?? (IWorkspaceStore)f.Store), new DesktopActions(f.Paths.DataDirectory, () => { }), f.Paths.DataDirectory,
                store => f.Session = new DesktopChatSession((IChatStore)store, f.Transport, f.Vault));
            await f.Root.InitializeAsync(); f.Main = Assert.IsType<MainViewModel>(f.Root.Content);
            await f.Main.SwitchWorkspaceAsync(f.Workspace);
            f.View = new MainView { DataContext = f.Main }; f.Window = new Window { Content = f.View, Width = 1400, Height = 1000 };
            f.Window.Show(); Layout(); return f;
        }
        public async Task<ChatView> OpenChat()
        {
            await Main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, Conversation)); Layout();
            return View.GetVisualDescendants().OfType<ChatView>().Single();
        }
        public object? Sql(string sql) => ContextStorageTests.Sql(Paths, sql);
        public async ValueTask DisposeAsync()
        {
            Transport.Close(); if (!Root.IsClosed) { Sql("DROP TRIGGER IF EXISTS integrated_summary_save_fault"); await Root.ConfirmCloseCommand.ExecuteAsync(null); }
            Window.Close(); await Store.DisposeAsync(); directory.Dispose();
        }
    }
}
