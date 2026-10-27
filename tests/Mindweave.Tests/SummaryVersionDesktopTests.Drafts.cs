using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Mindweave.Core.Summaries;
using Xunit;

namespace Mindweave.Tests;

public sealed partial class SummaryVersionDesktopTests
{
    [AvaloniaFact]
    public async Task Dirty_resolution_invalid_save_preserves_text_cancel_changes_nothing_discard_renews_lease()
    {
        await using var f = await SummaryStorageFixture.Open(); var candidate = await f.Generate("Candidate");
        var page = Page(f); await page.LoadAsync(); using var ui = new Surface(page);
        await page.EditCommand.ExecuteAsync(null); page.Draft.Title = ""; page.Draft.Markdown = "unfinished text";
        await page.Draft.FlushAsync(); page.Draft.StopDebounce();
        await page.SelectVersionAsync(candidate.VersionId);
        var before = f.DatabaseSnapshot();
        await ui.Click("AdoptCandidateButton");
        Assert.Contains("草稿", ui.Text("SummaryConfirmationMessage"));
        await ui.Click("CancelSummaryOperationButton"); Assert.Equal(before, f.DatabaseSnapshot());
        await ui.Click("AdoptCandidateButton"); await ui.Click("SaveSummaryDraftBeforeOperationButton");
        Assert.Equal("unfinished text", page.Draft.Markdown); Assert.Equal(f.First, (await f.Snapshot()).State.Node.CurrentVersionId);
        Assert.True(ui.View.FindControl<Button>("DiscardSummaryDraftBeforeOperationButton")!.IsVisible);
        await ui.Click("DiscardSummaryDraftBeforeOperationButton");
        Assert.False(page.Draft.IsDirty);
        await ui.Click("ConfirmSummaryOperationButton");
        Assert.Equal(candidate.VersionId, (await f.Snapshot()).State.Node.CurrentVersionId);
        await page.EditCommand.ExecuteAsync(null); page.Draft.Title = "After adoption"; page.Draft.Markdown = "new edit";
        await page.SaveCommand.ExecuteAsync(null);
        Assert.Equal("After adoption", (await f.Versions.LoadSummaryVersionAsync((await f.Snapshot()).State.Node.CurrentVersionId))!.Header.Title);
    }

    [AvaloniaFact]
    public async Task Unrelated_history_cleanup_allowed_with_dirty_draft_candidate_and_active_summary()
    {
        await using var f = await SummaryStorageFixture.Open(); await f.Commit("Current");
        var active = (await f.Store.AcceptKnowledgeSummaryAsync(Guid.NewGuid(), f.Conversation, BranchStorageTests.Config(32768))).Value!;
        var page = Page(f); await page.LoadAsync(); using var ui = new Surface(page);
        await page.EditCommand.ExecuteAsync(null); page.Draft.Title = "retained draft"; await page.Draft.FlushAsync();
        await ui.SelectDelete(f.First); await ui.Click("ConfirmSummaryOperationButton");
        Assert.Null(await f.Versions.LoadSummaryVersionAsync(f.First)); Assert.Equal("retained draft", page.Draft.Title);
        Assert.Contains(active.Id, (await f.Snapshot()).State.ActiveSummaryTaskIds);
        // An affected operation instead exposes the draft/active blockers without changing current.
        await ui.SelectDelete((await f.Snapshot()).State.Node.CurrentVersionId);
        Assert.Contains("草稿", ui.Text("SummaryConfirmationMessage"));
    }

    [AvaloniaFact]
    public async Task Restore_is_blocked_by_candidate_with_explicit_discard_route_then_requires_new_confirmation()
    {
        await using var f = await SummaryStorageFixture.Open(); await f.Commit("Current"); var candidate = await f.Generate("Candidate");
        var page = Page(f); await page.LoadAsync(f.First); using var ui = new Surface(page);
        await ui.Click("RestoreSummaryVersionButton");
        Assert.Contains("候选", ui.Text("SummaryConfirmationMessage"));
        Assert.False(ui.View.FindControl<Button>("ConfirmSummaryOperationButton")!.IsEnabled);
        await ui.Click("ResolveSummaryCandidateButton");
        Assert.Contains("不可恢复", ui.Text("SummaryConfirmationMessage"));
        await ui.Click("ConfirmSummaryOperationButton");
        Assert.Null(await f.Versions.LoadSummaryVersionAsync(candidate.VersionId));
        await page.SelectVersionAsync(f.First); await ui.Click("RestoreSummaryVersionButton"); await ui.Click("ConfirmSummaryOperationButton");
        Assert.Equal(f.First, (await f.Snapshot()).State.Node.CurrentVersionId);
    }
}
