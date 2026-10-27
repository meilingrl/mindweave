using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.Views;
using Xunit;

namespace Mindweave.Tests;

public sealed partial class SummaryVersionDesktopTests
{
    // These tests operate compiled controls and a fresh SQLite store, not XAML source text.
    [AvaloniaFact]
    public async Task Adoption_requires_fresh_confirmation_after_current_changes_and_keeps_candidate_read()
    {
        await using var f = await SummaryStorageFixture.Open();
        var candidate = await f.Generate("Candidate");
        var read = (await f.Versions.LoadSummaryReadingAsync(candidate.VersionId))!.State;
        await f.Versions.SetSummaryReadAsync(candidate.VersionId, read.Epoch, read.Revision, true);
        var notified = new List<Guid>();
        var page = Page(f, notified); await page.LoadAsync(candidate.VersionId);
        using var ui = new Surface(page);
        await ui.Click("AdoptCandidateButton");
        Assert.Contains("Candidate", ui.Text("SummaryConfirmationMessage"));
        var second = await f.Commit("Changed since preview");
        await ui.Click("ConfirmSummaryOperationButton");
        Assert.Equal(second.Header.Identity.Id, (await f.Snapshot()).State.Node.CurrentVersionId);
        Assert.Contains("Changed since preview", ui.Text("SummaryConfirmationMessage"));
        Assert.Contains("再次确认", ui.Text("SummaryConfirmationMessage"));
        var third = await f.Commit("Changed again");
        await ui.Click("ConfirmSummaryOperationButton");
        Assert.Equal(third.Header.Identity.Id, (await f.Snapshot()).State.Node.CurrentVersionId);
        await ui.Click("ConfirmSummaryOperationButton");
        Assert.Equal(candidate.VersionId, (await f.Snapshot()).State.Node.CurrentVersionId);
        Assert.True((await f.Versions.LoadSummaryReadingAsync(candidate.VersionId))!.State.IsRead);
        Assert.Equal(new[] { candidate.VersionId }, notified);
    }

    [AvaloniaFact]
    public async Task Candidate_discard_cancel_writes_nothing_then_confirm_physically_removes_body_and_reading()
    {
        await using var f = await SummaryStorageFixture.Open(); var candidate = await f.Generate("Discard me");
        var page = Page(f); await page.LoadAsync(candidate.VersionId); using var ui = new Surface(page);
        var before = f.DatabaseSnapshot();
        await ui.Click("DiscardCandidateButton");
        Assert.Contains("不可恢复", ui.Text("SummaryConfirmationMessage"));
        await ui.Click("CancelSummaryOperationButton"); Assert.Equal(before, f.DatabaseSnapshot());
        await ui.Click("DiscardCandidateButton"); await ui.Click("ConfirmSummaryOperationButton");
        Assert.Null(await f.Versions.LoadSummaryVersionAsync(candidate.VersionId));
        Assert.Null(await f.Versions.LoadSummaryReadingAsync(candidate.VersionId));
        Assert.Equal(f.First, page.Version!.Header.Identity.Id);
    }

    [AvaloniaFact]
    public async Task Multiselect_delete_previews_exact_fallback_and_last_node_keeps_conversation()
    {
        await using var f = await SummaryStorageFixture.Open();
        var second = await f.Commit("Second"); var third = await f.Commit("Third");
        var notified = new List<Guid>(); var page = Page(f, notified); await page.LoadAsync(); using var ui = new Surface(page);
        await ui.SelectDelete(second.Header.Identity.Id, third.Header.Identity.Id);
        var text = ui.Text("SummaryConfirmationMessage");
        Assert.Contains("Second", text); Assert.Contains("Third", text); Assert.Contains("First", text); Assert.Contains("不可恢复", text);
        await ui.Click("ConfirmSummaryOperationButton");
        Assert.Equal(f.First, (await f.Snapshot()).State.Node.CurrentVersionId);
        Assert.Equal(f.First, page.Version!.Header.Identity.Id); Assert.Equal(new[] { f.First }, notified);
        Assert.Null(await f.Versions.LoadSummaryVersionAsync(third.Header.Identity.Id));
        await ui.SelectDelete(f.First);
        Assert.Contains("节点", ui.Text("SummaryConfirmationMessage")); Assert.Contains("来源会话", ui.Text("SummaryConfirmationMessage"));
        await ui.Click("ConfirmSummaryOperationButton");
        Assert.Null(await f.Versions.LoadSummaryNodeAsync(f.Node)); Assert.Empty(page.SavedBody);
        Assert.NotEmpty(await f.Store.ReadMessagesAsync(f.Conversation));
    }

    [AvaloniaFact]
    public async Task Manual_display_name_is_separate_from_title_and_survives_restore()
    {
        await using var f = await SummaryStorageFixture.Open(); var second = await f.Commit("Second");
        var notified = new List<Guid>(); var page = Page(f, notified); await page.LoadAsync(f.First); using var ui = new Surface(page);
        await ui.Click("RenameSummaryNodeButton");
        ui.View.FindControl<TextBox>("SummaryDisplayNameInput")!.Text = "My display name"; Layout();
        await ui.Click("ConfirmSummaryOperationButton");
        Assert.Equal("My display name", (await f.Snapshot()).State.Node.DisplayName); Assert.Empty(notified);
        Assert.Equal("First", (await f.Versions.LoadSummaryVersionAsync(f.First))!.Header.Title);
        await ui.Click("RestoreSummaryVersionButton"); await ui.Click("ConfirmSummaryOperationButton");
        Assert.Equal(f.First, (await f.Snapshot()).State.Node.CurrentVersionId);
        Assert.Equal("My display name", (await f.Snapshot()).State.Node.DisplayName); Assert.True((await f.Snapshot()).State.Node.NameIsManual);
        Assert.Equal(new[] { f.First }, notified);
    }

    private static SummaryViewModel Page(SummaryStorageFixture f, List<Guid>? notified = null) =>
        new(f.Workspace, f.Node, f.Versions, new(f.Versions, f.Node, () => { }), () => false, () => { }, _ => Task.CompletedTask,
            id => { notified?.Add(id); return Task.CompletedTask; });
    private static void Layout() { Dispatcher.UIThread.RunJobs(); Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private sealed class Surface : IDisposable
    {
        public SummaryView View { get; }
        private readonly SummaryViewModel page;
        private readonly Window window;
        public Surface(SummaryViewModel page)
        {
            this.page = page; View = new() { DataContext = page, AutomaticObservation = false };
            window = new() { Content = View, Width = 950, Height = 850 }; window.Show(); Layout();
        }
        public async Task Click(string name)
        {
            Layout(); var button = View.FindControl<Button>(name); Assert.NotNull(button); Assert.True(button.IsVisible); Assert.True(button.IsEnabled);
            Assert.NotNull(button.Command);
            if (button.Command is IAsyncRelayCommand command) await command.ExecuteAsync(null); else button.Command.Execute(null);
            Layout();
        }
        public string Text(string name) { Layout(); return View.FindControl<TextBlock>(name)!.Text!; }
        public async Task SelectDelete(params Guid[] ids)
        {
            await Click("DeleteSummaryVersionsButton"); var list = View.FindControl<ListBox>("SummaryDeletionSelection")!;
            foreach (var id in ids) list.SelectedItems!.Add(page.Versions.Single(h => h.Identity.Id == id));
            Layout(); await Click("PreviewSummaryDeletionButton");
        }
        public void Dispose() { page.Detach(); window.Close(); }
    }
}
