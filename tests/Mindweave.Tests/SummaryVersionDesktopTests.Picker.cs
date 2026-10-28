using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Mindweave.Core.Summaries;
using Xunit;

namespace Mindweave.Tests;

public sealed partial class SummaryVersionDesktopTests
{
    [AvaloniaTheory]
    [InlineData("save")]
    [InlineData("restore")]
    [InlineData("adopt")]
    public async Task Version_picker_keeps_displayed_selection_when_operation_refreshes_same_version(string operation)
    {
        await using var f = await SummaryStorageFixture.Open();
        var second = operation == "adopt" ? (await f.Generate("Candidate")).VersionId : (await f.Commit("Manual", "manual body")).Header.Identity.Id;
        var target = operation == "restore" ? f.First : second;
        var expectedBody = operation == "restore" ? "# body\nFirst" : operation == "adopt" ? "# body\nCandidate" : "manual body";
        var notified = new List<Guid>(); var page = Page(f, notified);
        await page.LoadAsync(target); using var ui = new Surface(page);
        var picker = ui.View.FindControl<ComboBox>("SummaryVersionPicker")!;
        Assert.Equal(target, Assert.IsType<SummaryVersionHeader>(picker.SelectedItem).Identity.Id);
        var beforeRead = await f.Versions.LoadSummaryReadingAsync(target);
        var selected = new List<Guid>(); page.VersionSelected += selected.Add;

        if (operation == "save")
        {
            await page.EditCommand.ExecuteAsync(null); Layout();
            var save = ui.View.GetVisualDescendants().OfType<Button>().Single(b => b.Command == page.SaveCommand);
            Assert.True(save.IsEnabled); await page.SaveCommand.ExecuteAsync(save.CommandParameter); Layout();
        }
        else
        {
            await ui.Click(operation == "restore" ? "RestoreSummaryVersionButton" : "AdoptCandidateButton");
            await ui.Click("ConfirmSummaryOperationButton");
        }

        Assert.Null(page.ErrorMessage); Assert.False(page.IsLoading);
        Assert.Equal(target, page.Version!.Header.Identity.Id); Assert.Equal(expectedBody, page.SavedBody);
        Assert.Equal(target, (await f.Snapshot()).State.Node.CurrentVersionId);
        Assert.Equal(2, (await f.Versions.ListSummaryVersionsAsync(f.Node)).Length);
        Assert.Equal(beforeRead, await f.Versions.LoadSummaryReadingAsync(target));
        Assert.Equal(new[] { target }, selected); // Refreshing the control must not reenter version loading.
        Assert.Equal(operation == "save" ? Array.Empty<Guid>() : new[] { target }, notified);
        Assert.False(page.Draft.IsDirty);
        Assert.Equal(target, page.SelectedVersion!.Identity.Id);
        Assert.Equal(target, Assert.IsType<SummaryVersionHeader>(picker.SelectedItem).Identity.Id);

        // The repaired binding still permits a user keyboard selection of another saved header.
        var other = target == f.First ? second : f.First;
        Assert.True(picker.Focus());
        var window = Assert.IsType<Window>(TopLevel.GetTopLevel(ui.View));
        var key = target == f.First ? PhysicalKey.ArrowUp : PhysicalKey.ArrowDown;
        window.KeyPressQwerty(key, RawInputModifiers.None); window.KeyReleaseQwerty(key, RawInputModifiers.None);
        await page.WaitForPendingOperationsAsync(); Layout();
        Assert.Equal(other, Assert.IsType<SummaryVersionHeader>(picker.SelectedItem).Identity.Id);
        Assert.Equal(other, page.Version!.Header.Identity.Id);
        Assert.Equal(target, (await f.Snapshot()).State.Node.CurrentVersionId);
        Assert.Equal(new[] { target, other }, selected);
    }

    [AvaloniaFact]
    public async Task Version_picker_preserves_dirty_editor_on_metadata_refresh_then_selects_new_manual_version()
    {
        await using var f = await SummaryStorageFixture.Open(); var page = Page(f);
        await page.LoadAsync(); using var ui = new Surface(page);
        var picker = ui.View.FindControl<ComboBox>("SummaryVersionPicker")!;
        var originalSelection = Assert.IsType<SummaryVersionHeader>(picker.SelectedItem);
        await page.EditCommand.ExecuteAsync(null); Layout();
        ui.View.FindControl<TextBox>("SummaryEditTitle")!.Text = "Manual third";
        ui.View.FindControl<TextBox>("SummaryEditBody")!.Text = "User input retained through refresh";
        page.Draft.StopDebounce(); Layout();
        var beforeRead = await f.Versions.LoadSummaryReadingAsync(f.First);
        var selected = new List<Guid>(); page.VersionSelected += selected.Add;
        var candidate = await f.Generate("Candidate");

        picker.IsDropDownOpen = true; Layout();
        await page.RefreshMetadataAsync(); Layout();
        Assert.Same(originalSelection, picker.SelectedItem); Assert.Empty(selected);
        Assert.True(page.IsEditing); Assert.True(page.Draft.IsDirty);
        Assert.Equal("User input retained through refresh", page.Draft.Markdown);
        Assert.Equal("# body\nFirst", page.SavedBody);
        Assert.Equal(beforeRead, await f.Versions.LoadSummaryReadingAsync(f.First));
        picker.IsDropDownOpen = false; Layout();

        await ui.Click("RenameSummaryNodeButton"); page.DisplayNameInput = "Separate node name";
        await ui.Click("ConfirmSummaryOperationButton");
        Assert.Same(originalSelection, picker.SelectedItem); Assert.Empty(selected);
        Assert.True(page.IsEditing); Assert.Equal("Manual third", page.Draft.Title);
        await page.SaveCommand.ExecuteAsync(null); Layout();
        var manual = Assert.IsType<SummaryVersionHeader>(picker.SelectedItem);
        Assert.Equal(3, manual.Identity.Ordinal); Assert.Equal("Manual third", manual.Title);
        Assert.Equal(manual.Identity.Id, page.Version!.Header.Identity.Id);
        Assert.Equal(manual.Identity.Id, (await f.Snapshot()).State.Node.CurrentVersionId);
        Assert.Equal(candidate.VersionId, (await f.Snapshot()).State.Node.CandidateVersionId);
        Assert.Equal(new[] { 3L, 2L, 1L }, picker.ItemsSource!.Cast<SummaryVersionHeader>().Select(h => h.Identity.Ordinal));
        Assert.Equal("User input retained through refresh", page.SavedBody);
        Assert.Equal("Separate node name", page.NodeDisplayName); Assert.False(page.Draft.IsDirty);
        Assert.Equal(new[] { manual.Identity.Id }, selected);

        await page.EditCommand.ExecuteAsync(null); await page.SaveCommand.ExecuteAsync(null); Layout();
        Assert.Same(manual, picker.SelectedItem);
        Assert.Equal(3, (await f.Versions.ListSummaryVersionsAsync(f.Node)).Length);
        Assert.Equal(new[] { manual.Identity.Id, manual.Identity.Id }, selected);
    }
}
