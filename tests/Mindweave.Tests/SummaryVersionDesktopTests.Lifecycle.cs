using System.Reflection;
using Avalonia.Headless.XUnit;
using Mindweave.Application.Navigation;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;
using Mindweave.Desktop;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Xunit;

namespace Mindweave.Tests;

public sealed partial class SummaryVersionDesktopTests
{
    [AvaloniaTheory]
    [InlineData("AdoptSummaryCandidateAsync")]
    [InlineData("SetCurrentSummaryVersionAsync")]
    [InlineData("DiscardSummaryCandidateAsync")]
    [InlineData("DeleteSummaryVersionsAsync")]
    [InlineData("RenameSummaryNodeAsync")]
    public async Task Lost_ack_retains_exact_intent_and_repair_reconciles_without_repeating_mutation(string method)
    {
        await using var f = await SummaryStorageFixture.Open();
        var target = method is "AdoptSummaryCandidateAsync" or "DiscardSummaryCandidateAsync" ? (await f.Generate("Candidate")).VersionId : f.First;
        if (method == "SetCurrentSummaryVersionAsync") await f.Commit("Current");
        var proxy = Proxy(f, out var control); var failed = false; var notified = new List<Guid>();
        var page = new SummaryViewModel(f.Workspace, f.Node, proxy, new(proxy, f.Node, () => failed = true), () => failed,
            () => failed = true, _ => Task.CompletedTask, id => { notified.Add(id); return Task.CompletedTask; });
        await page.LoadAsync(target); using var ui = new Surface(page);
        if (method == "DeleteSummaryVersionsAsync") await ui.SelectDelete(target);
        else await ui.Click(method switch { "AdoptSummaryCandidateAsync" => "AdoptCandidateButton", "SetCurrentSummaryVersionAsync" => "RestoreSummaryVersionButton", "DiscardSummaryCandidateAsync" => "DiscardCandidateButton", _ => "RenameSummaryNodeButton" });
        if (method == "RenameSummaryNodeAsync") page.DisplayNameInput = "  My display name  ";
        control.Intercept = method; control.LoseAck = true;
        await ui.Click("ConfirmSummaryOperationButton");
        Assert.True(failed); Assert.True(page.HasPendingVersionOperation); Assert.False(page.CanCancelVersionOperation);
        if (method is "DiscardSummaryCandidateAsync" or "DeleteSummaryVersionsAsync") Assert.Empty(page.SavedBody);
        await page.RepairVersionOperationAsync();
        Assert.False(page.HasPendingVersionOperation); Assert.Equal(1, control.Calls);
        if (method is "AdoptSummaryCandidateAsync" or "SetCurrentSummaryVersionAsync") Assert.Equal(new[] { target }, notified);
        else Assert.Empty(notified);
        if (method == "RenameSummaryNodeAsync") { Assert.Equal("My display name", (await f.Snapshot()).State.Node.DisplayName); Assert.False(page.IsConfirmationOpen); }
    }

    [AvaloniaFact]
    public async Task Unknown_before_write_repair_does_not_substitute_new_candidate()
    {
        await using var f = await SummaryStorageFixture.Open(); var original = await f.Generate("Original candidate");
        var proxy = Proxy(f, out var control); var failed = false;
        var page = new SummaryViewModel(f.Workspace, f.Node, proxy, new(proxy, f.Node, () => failed = true), () => failed,
            () => failed = true, _ => Task.CompletedTask);
        await page.LoadAsync(original.VersionId); using var ui = new Surface(page);
        await ui.Click("DiscardCandidateButton"); control.Intercept = "DiscardSummaryCandidateAsync"; control.ThrowBefore = true;
        await ui.Click("ConfirmSummaryOperationButton"); Assert.True(page.HasPendingVersionOperation);
        await f.Versions.DiscardSummaryCandidateAsync((await f.Snapshot()).State.Token);
        var newer = await f.Generate("New candidate");
        await page.RepairVersionOperationAsync();
        Assert.Equal(1, control.Calls); Assert.NotNull(await f.Versions.LoadSummaryVersionAsync(newer.VersionId));
        Assert.Equal(newer.VersionId, (await f.Snapshot()).State.Node.CandidateVersionId);
    }

    [AvaloniaFact]
    public async Task Main_close_drains_held_confirmation_and_return_repair_preserves_unknown_intent()
    {
        await using var f = await SummaryStorageFixture.Open(); var candidate = await f.Generate("Candidate");
        var proxy = Proxy(f, out var control);
        var root = new RootViewModel(() => Task.FromResult<Mindweave.Application.Storage.IWorkspaceStore>(proxy),
            new DesktopActions(f.Paths.DataDirectory, () => { }), f.Paths.DataDirectory,
            store => new DesktopChatSession((Mindweave.Application.Chat.IChatStore)store,
                new GenerationCoordinatorTests.ControlledTransport(), new GenerationCoordinatorTests.MemoryVault()));
        await root.InitializeAsync(); var main = Assert.IsType<MainViewModel>(root.Content);
        await main.NavigateAsync(new(MainArea.Network, ContentKind.Summary, f.Node, SummaryVersionId: candidate.VersionId));
        var page = Assert.IsType<SummaryViewModel>(main.ActiveContent); using var ui = new Surface(page);
        await ui.Click("DiscardCandidateButton");
        control.Intercept = "DiscardSummaryCandidateAsync"; control.Hold = true; control.LoseAck = true;
        var confirming = ui.Click("ConfirmSummaryOperationButton"); await control.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var preparing = main.PrepareCloseAsync(); Assert.False(preparing.IsCompleted); Assert.False(control.Disposed);
        control.FailNodeRead = true; control.Release.TrySetResult(); await confirming; await preparing;
        await root.CloseAsync(); Assert.False(root.IsClosed); Assert.True(page.HasPendingVersionOperation);
        root.ReturnToWorkbenchCommand.Execute(null); Assert.True(page.IsConfirmationOpen); Assert.True(main.HasPersistenceFailure);
        control.FailNodeRead = false;
        await root.RetrySaveCommand.ExecuteAsync(null); Assert.False(main.HasPersistenceFailure); Assert.False(page.HasPendingVersionOperation);
        Assert.Equal(f.First, page.Version!.Header.Identity.Id); Assert.Equal(1, control.Calls);
        await root.CloseAsync(); Assert.True(root.IsClosed); Assert.True(control.Disposed);
    }

    [AvaloniaFact]
    public async Task Queued_input_during_discard_is_retained_and_requires_new_resolution()
    {
        await using var f = await SummaryStorageFixture.Open(); await f.Generate("Candidate");
        var proxy = Proxy(f, out var control);
        var page = new SummaryViewModel(f.Workspace, f.Node, proxy, new(proxy, f.Node, () => { }), () => false, () => { }, _ => Task.CompletedTask);
        await page.LoadAsync(); using var ui = new Surface(page);
        await page.EditCommand.ExecuteAsync(null); page.Draft.Title = "Old draft"; await page.Draft.FlushAsync();
        await ui.Click("AdoptCandidateButton");
        control.Intercept = "DiscardSummaryDraftAsync"; control.Hold = true;
        var discard = ui.Click("DiscardSummaryDraftBeforeOperationButton"); await control.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        page.Draft.Title = "Queued later title"; page.Draft.Markdown = "Queued later body"; page.Draft.StopDebounce();
        control.Release.TrySetResult(); await discard;
        Assert.True(page.NeedsDraftResolution); Assert.Equal("Queued later title", page.Draft.Title);
        await page.Draft.FlushAsync(); Assert.Equal("Queued later body", (await f.Versions.LoadSummaryDraftAsync(f.Node))!.Markdown);
        Assert.Equal(f.First, (await f.Snapshot()).State.Node.CurrentVersionId);
    }

    [AvaloniaFact]
    public async Task Unknown_pointer_write_with_newer_local_draft_does_not_replay_before_resolution()
    {
        await using var f = await SummaryStorageFixture.Open(); await f.Generate("Candidate");
        var proxy = Proxy(f, out var control); var failed = false;
        var page = new SummaryViewModel(f.Workspace, f.Node, proxy, new(proxy, f.Node, () => failed = true), () => failed,
            () => failed = true, _ => Task.CompletedTask);
        await page.LoadAsync(); using var ui = new Surface(page); await page.EditCommand.ExecuteAsync(null);
        await ui.Click("AdoptCandidateButton"); control.Intercept = "AdoptSummaryCandidateAsync"; control.ThrowBefore = true;
        await ui.Click("ConfirmSummaryOperationButton");
        page.Draft.Title = "Late unsaved title"; page.Draft.Markdown = "Late unsaved body"; page.Draft.StopDebounce();
        await page.RepairVersionOperationAsync();
        Assert.Equal(f.First, (await f.Snapshot()).State.Node.CurrentVersionId);
        Assert.Equal(1, control.Calls); Assert.True(page.NeedsDraftResolution);
        Assert.Equal("Late unsaved body", page.Draft.Markdown);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dirty_resolution_lost_ack_repair_keeps_input_and_reuses_original_commit_or_discard(bool discard)
    {
        await using var f = await SummaryStorageFixture.Open(); var candidate = await f.Generate("Candidate");
        var proxy = Proxy(f, out var control); var failed = false;
        var page = new SummaryViewModel(f.Workspace, f.Node, proxy, new(proxy, f.Node, () => failed = true), () => failed,
            () => failed = true, _ => Task.CompletedTask);
        await page.LoadAsync(); using var ui = new Surface(page); await page.EditCommand.ExecuteAsync(null);
        page.Draft.Title = "Confirmed draft"; page.Draft.Markdown = "Confirmed body"; await page.Draft.FlushAsync();
        await ui.Click("AdoptCandidateButton");
        control.Intercept = discard ? "DiscardSummaryDraftAsync" : "CommitSummaryDraftAsync"; control.LoseAck = true;
        await ui.Click(discard ? "DiscardSummaryDraftBeforeOperationButton" : "SaveSummaryDraftBeforeOperationButton");
        Assert.True(failed); Assert.True(page.IsConfirmationOpen); Assert.Equal("Confirmed body", page.Draft.Markdown);
        await page.FlushAsync(); failed = false; page.RefreshAvailability();
        Assert.Equal(1, control.Calls); Assert.False(page.Draft.IsDirty);
        await ui.Click(discard ? "DiscardSummaryDraftBeforeOperationButton" : "SaveSummaryDraftBeforeOperationButton");
        Assert.True(page.CanConfirmVersionOperation); await ui.Click("ConfirmSummaryOperationButton");
        Assert.Equal(candidate.VersionId, (await f.Snapshot()).State.Node.CurrentVersionId);
        Assert.Equal(discard ? 2 : 3, (await f.Versions.ListSummaryVersionsAsync(f.Node)).Length);
    }

    [AvaloniaFact]
    public async Task Navigating_after_unknown_mutation_preserves_intent_until_explicit_repair()
    {
        await using var f = await SummaryStorageFixture.Open(); var candidate = await f.Generate("Candidate"); var proxy = Proxy(f, out var control);
        await using var session = new DesktopChatSession(proxy, new GenerationCoordinatorTests.ControlledTransport(), new GenerationCoordinatorTests.MemoryVault());
        await session.InitializeAsync(); var main = CompositionRoot.CreateMain(proxy, session); await main.InitializeAsync();
        await main.NavigateAsync(new(MainArea.Network, ContentKind.Summary, f.Node));
        var page = Assert.IsType<SummaryViewModel>(main.ActiveContent); using var ui = new Surface(page);
        await ui.Click("AdoptCandidateButton"); control.Intercept = "AdoptSummaryCandidateAsync"; control.ThrowBefore = true;
        await ui.Click("ConfirmSummaryOperationButton"); Assert.True(main.HasPersistenceFailure);
        await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, f.Conversation));
        var currentAfterNavigation = (await f.Snapshot()).State.Node.CurrentVersionId;
        var callsAfterNavigation = control.Calls; var pendingAfterNavigation = page.HasPendingVersionOperation;
        // Clear the intentional external failure before assertions, so a failing assertion
        // cannot be masked by await-using's required runtime disposal gate.
        await main.RetryPersistenceAsync();
        Assert.Equal(f.First, currentAfterNavigation); Assert.Equal(1, callsAfterNavigation); Assert.True(pendingAfterNavigation);
        Assert.Equal(candidate.VersionId, (await f.Snapshot()).State.Node.CurrentVersionId); Assert.Equal(2, control.Calls);
        Assert.False(main.HasPersistenceFailure); Assert.IsType<ChatViewModel>(main.ActiveContent);
        main.DetachObservers(); await main.DrainBackgroundRefreshesAsync();
    }

    private static SummaryDesktopTests.IDesktopStore Proxy(SummaryStorageFixture f, out OperationStoreProxy control)
    {
        var proxy = DispatchProxy.Create<SummaryDesktopTests.IDesktopStore, OperationStoreProxy>();
        control = (OperationStoreProxy)(object)proxy; control.Inner = f.Store; return proxy;
    }
    public class OperationStoreProxy : DispatchProxy
    {
        public object Inner { get; set; } = null!;
        public string? Intercept { get; set; }
        public bool Hold { get; set; }
        public bool LoseAck { get; set; }
        public bool ThrowBefore { get; set; }
        public bool FailNodeRead { get; set; }
        public bool Disposed { get; private set; }
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IAsyncDisposable.DisposeAsync)) Disposed = true;
            if (method.Name == "LoadSummaryNodeAsync" && FailNodeRead) return Task.FromException<SummaryNodeSnapshot?>(new IOException("synthetic repair read failure"));
            if (method.Name != Intercept) return method.Invoke(Inner, args);
            return typeof(OperationStoreProxy).GetMethod(nameof(InterceptAsync), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(method.ReturnType.GenericTypeArguments[0]).Invoke(this, [method, args]);
        }
        private async Task<T> InterceptAsync<T>(MethodInfo method, object?[]? args)
        {
            Calls++; Entered.TrySetResult(); if (Hold) await Release.Task;
            if (ThrowBefore) { ThrowBefore = false; throw new IOException("synthetic failure before transaction"); }
            var result = await (Task<T>)method.Invoke(Inner, args)!;
            if (LoseAck) { LoseAck = false; throw new IOException("synthetic lost acknowledgement after transaction"); }
            return result;
        }
    }
}
