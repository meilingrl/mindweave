using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mindweave.Application.Navigation;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;
using Mindweave.Desktop;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Xunit;

namespace Mindweave.Tests;

public sealed partial class SummaryVersionDesktopTests
{
    [AvaloniaFact]
    public async Task Delete_rejects_stale_selection_preview_and_does_not_delete_newer_current()
    {
        await using var f = await SummaryStorageFixture.Open(); var second = await f.Commit("Second");
        var page = Page(f); await page.LoadAsync(); using var ui = new Surface(page);
        await ui.SelectDelete(second.Header.Identity.Id);
        var third = await f.Commit("Third");
        await ui.Click("ConfirmSummaryOperationButton");
        Assert.NotNull(await f.Versions.LoadSummaryVersionAsync(second.Header.Identity.Id));
        Assert.Contains("Third", ui.Text("SummaryConfirmationMessage"));
        Assert.Contains("再次确认", ui.Text("SummaryConfirmationMessage"));
        await ui.Click("ConfirmSummaryOperationButton");
        Assert.Null(await f.Versions.LoadSummaryVersionAsync(second.Header.Identity.Id));
        Assert.Equal(third.Header.Identity.Id, (await f.Snapshot()).State.Node.CurrentVersionId);
    }

    [AvaloniaFact]
    public async Task Production_active_summary_stop_resolves_deletion_blocker_without_resending()
    {
        await using var f = await SummaryStorageFixture.Open(); await f.Store.SaveConfigurationAsync(BranchStorageTests.Config(32768) with { CredentialReference = "v1" });
        var transport = new GenerationCoordinatorTests.ControlledTransport();
        await using var session = new DesktopChatSession(f.Store, transport, new GenerationCoordinatorTests.MemoryVault());
        await session.InitializeAsync();
        var main = CompositionRoot.CreateMain(f.Store, session); await main.InitializeAsync();
        var job = (await session.Summaries.AcceptAsync(f.Conversation)).Value!; Assert.NotNull(job);
        await transport.NextAsync();
        await main.NavigateAsync(new(MainArea.Network, ContentKind.Summary, f.Node));
        var page = Assert.IsType<SummaryViewModel>(main.ActiveContent); using var ui = new Surface(page);
        await ui.SelectDelete(f.First);
        Assert.True(page.NeedsActiveSummaryResolution); Assert.False(page.CanConfirmVersionOperation);
        await ui.Click("StopSummaryBeforeOperationButton");
        Assert.Equal(GenerationStatus.Stopped, (await f.Store.LoadKnowledgeSummaryJobAsync(job.Id))!.Status);
        Assert.True(page.CanConfirmVersionOperation); Assert.Equal(1, transport.StartCount);
        await ui.Click("ConfirmSummaryOperationButton");
        await main.DrainBackgroundRefreshesAsync(); Dispatcher.UIThread.RunJobs(); await main.DrainBackgroundRefreshesAsync();
        Assert.Null(await f.Versions.LoadSummaryNodeAsync(f.Node));
        Assert.IsType<NetworkViewModel>(main.ActiveContent);
        main.DetachObservers(); await main.DrainBackgroundRefreshesAsync(); transport.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_naming_neither_blocks_delete_nor_overwrites_manual_display_name(bool delete)
    {
        await using var f = await SummaryStorageFixture.Open(); await SummaryNamingStorageTests.Add(f);
        var job = await SummaryNamingStorageTests.Accept(f);
        var page = Page(f); await page.LoadAsync(); using var ui = new Surface(page);
        if (delete) { await ui.SelectDelete(f.First); await ui.Click("ConfirmSummaryOperationButton"); }
        else { await ui.Click("RenameSummaryNodeButton"); page.DisplayNameInput = "Protected manual"; await ui.Click("ConfirmSummaryOperationButton"); }
        var late = await f.Store.CompleteNodeNamingAsync(new(job.Id, job.Revision, job.Snapshot.SnapshotHash, SummaryNamingStorageTests.Proposals(job)));
        Assert.False(late.Kind is SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange);
        if (delete)
        {
            Assert.Null(await f.Versions.LoadSummaryNodeAsync(f.Node)); Assert.Null(await f.Versions.LoadSummaryVersionAsync(f.First));
            Assert.Null(await f.Versions.LoadSummaryReadingAsync(f.First));
            Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_version_source WHERE version_id=$id", ("$id", f.First)));
            Assert.Equal(GenerationStatus.Stopped, (await f.Store.LoadNodeNamingJobAsync(job.Id))!.Status);
        }
        else { Assert.Equal("Protected manual", (await f.Snapshot()).State.Node.DisplayName); Assert.Equal("First", page.Version!.Header.Title); }
    }

    [AvaloniaFact]
    public async Task Dirty_save_freezes_seen_text_and_preserves_new_input_arriving_during_flush()
    {
        await using var f = await SummaryStorageFixture.Open(); await f.Generate("Candidate");
        var proxy = Proxy(f, out var control);
        var page = new SummaryViewModel(f.Workspace, f.Node, proxy, new(proxy, f.Node, () => { }), () => false, () => { }, _ => Task.CompletedTask);
        await page.LoadAsync(); using var ui = new Surface(page); await page.EditCommand.ExecuteAsync(null);
        page.Draft.Title = "Seen title"; page.Draft.Markdown = "Seen body"; page.Draft.StopDebounce();
        await ui.Click("AdoptCandidateButton");
        control.Intercept = "SaveSummaryDraftAsync"; control.Hold = true;
        var saving = ui.Click("SaveSummaryDraftBeforeOperationButton"); await control.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        page.Draft.Title = "Unseen newer title"; page.Draft.Markdown = "Unseen newer body"; page.Draft.StopDebounce();
        control.Release.TrySetResult(); await saving;
        Assert.Equal(f.First, (await f.Snapshot()).State.Node.CurrentVersionId);
        Assert.Equal("Unseen newer title", page.Draft.Title); Assert.True(page.NeedsDraftResolution);
    }

    [AvaloniaFact]
    public async Task Quiescing_during_durable_adoption_refreshes_current_on_failed_close_return()
    {
        await using var f = await SummaryStorageFixture.Open(); var candidate = await f.Generate("Candidate");
        var proxy = Proxy(f, out var control);
        var page = new SummaryViewModel(f.Workspace, f.Node, proxy, new(proxy, f.Node, () => { }), () => false, () => { }, _ => Task.CompletedTask);
        await page.LoadAsync(f.First); using var ui = new Surface(page);
        await ui.Click("AdoptCandidateButton"); control.Intercept = "AdoptSummaryCandidateAsync"; control.Hold = true;
        var confirming = ui.Click("ConfirmSummaryOperationButton"); await control.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        page.QuiesceForClose(); var drained = page.WaitForPendingOperationsAsync(); Assert.False(drained.IsCompleted);
        control.Release.TrySetResult(); await confirming; await drained;
        Assert.Equal(candidate.VersionId, (await f.Snapshot()).State.Node.CurrentVersionId);
        page.ResumeAfterClose(); await page.WaitForPendingOperationsAsync();
        Assert.Equal(candidate.VersionId, page.Version!.Header.Identity.Id);
        Assert.Equal("当前版本", page.VersionIdentity);
    }
    [AvaloniaFact]
    public async Task Last_delete_retains_queued_input_as_explicit_memory_recovery_until_its_own_confirmation()
    {
        await using var f = await SummaryStorageFixture.Open(); var proxy = Proxy(f, out var control);
        var root = new RootViewModel(() => Task.FromResult<Mindweave.Application.Storage.IWorkspaceStore>(proxy),
            new DesktopActions(f.Paths.DataDirectory, () => { }), f.Paths.DataDirectory,
            store => new DesktopChatSession((Mindweave.Application.Chat.IChatStore)store,
                new GenerationCoordinatorTests.ControlledTransport(), new GenerationCoordinatorTests.MemoryVault()));
        await root.InitializeAsync(); var main = Assert.IsType<MainViewModel>(root.Content);
        await main.NavigateAsync(new(MainArea.Network, ContentKind.Summary, f.Node));
        var page = Assert.IsType<SummaryViewModel>(main.ActiveContent); using var ui = new Surface(page);
        await page.EditCommand.ExecuteAsync(null); await ui.SelectDelete(f.First);
        control.Intercept = "DeleteSummaryVersionsAsync"; control.Hold = true;
        var deleting = ui.Click("ConfirmSummaryOperationButton"); await control.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        page.Draft.Title = "Arrived during deletion"; page.Draft.Markdown = "New user text, not a saved version"; page.Draft.StopDebounce();
        control.Release.TrySetResult(); await deleting;
        Assert.Null(await f.Versions.LoadSummaryNodeAsync(f.Node)); Assert.Empty(page.SavedBody);
        Assert.False(main.HasPersistenceFailure); // Expected unresolved local input is not an IO failure.
        Assert.Contains("删除", ui.Text("SummaryConfirmationMessage"));
        Assert.Equal("New user text, not a saved version", page.Draft.Markdown);
        await root.RetrySaveCommand.ExecuteAsync(null); Assert.False(main.HasPersistenceFailure);
        await root.CloseAsync(); Assert.False(root.IsClosed); Assert.False(main.HasPersistenceFailure);
        Assert.Contains("输入", root.CloseError!); root.ReturnToWorkbenchCommand.Execute(null);
        // A second queued edit invalidates the recovery confirmation; the first click only refreshes it.
        page.Draft.Markdown = "Even newer input"; page.Draft.StopDebounce();
        await ui.Click("DiscardRemovedSummaryInputButton"); Assert.Equal("Even newer input", page.Draft.Markdown);
        await ui.Click("DiscardRemovedSummaryInputButton");
        Assert.False(page.HasPendingVersionOperation); Assert.Empty(page.Draft.Markdown);
        Assert.Null(await f.Versions.LoadSummaryNodeAsync(f.Node)); Assert.Equal(1, control.Calls);
        Dispatcher.UIThread.RunJobs(); await main.DrainBackgroundRefreshesAsync();
        await root.CloseAsync(); Assert.True(root.IsClosed);
    }

}
