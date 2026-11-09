using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using Mindweave.Testing;

namespace Mindweave.Desktop.Tests;

/// <summary>S4 as the reader sees it: summarize from the conversation, read, candidates, edits, history and deletion.</summary>
public class SummaryUiTests
{
    [AvaloniaFact]
    public async Task A_summary_made_midway_is_listed_unread_and_reading_it_long_enough_marks_it_read()
    {
        var clock = ManualTime.Running();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<TimeProvider>(clock));
        var window = app.ShowWindow();
        var page = await app.StartWithConversationAsync(waitForTitle: false);

        await page.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.HasSummary && page.RunningSummary is null);
        Assert.Equal(UiText.SummaryEntry(FakeChatClient.DefaultSummaryTitle), page.SummaryEntryText);
        Assert.Equal(UiText.MarkerUnread, page.SummaryMarkersText);
        Assert.Equal(UiText.Resummarize, page.SummarizeLabel);

        // The network section lists it; opening the list does not read it.
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Network);
        var list = Assert.IsType<NetworkHomeViewModel>(app.Workbench.CurrentPage);
        await list.Loaded;
        var item = Assert.Single(list.Items);
        Assert.Equal(UiText.MarkerUnread, item.Markers);

        list.OpenCommand.Execute(item.Node);
        var summary = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await summary.Loaded;
        window.UpdateLayout();
        var view = window.GetVisualDescendants().OfType<SummaryView>().Single();
        Assert.Contains("总结了", string.Concat(SourceAnchors.Blocks(view.Renderer).Select(b => b.LayoutText)), StringComparison.Ordinal);
        Screenshots.Capture(window, "06-summary");

        // Fully in view at once, but opened only just now: not read yet.
        await summary.ReportProgressAsync(1.0, null);
        Assert.False(summary.ShownIsRead);
        clock.Advance(SummaryRules.MinimumReadTime + TimeSpan.FromSeconds(1));
        await summary.ReportProgressAsync(1.0, null);
        Assert.True(summary.ShownIsRead);
        await UiWait.UntilAsync(() => summary.MarkersText.Length == 0);

        await summary.ToggleReadCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => summary.MarkersText == UiText.MarkerUnread);
        Assert.False(summary.ShownIsRead);

        summary.ReturnToConversationCommand.Execute(null);
        Assert.Equal(page.Id, Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage).Id);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_regenerated_summary_waits_as_a_candidate_beside_the_edited_current_version()
    {
        await using var app = DesktopHarness.Create();
        var page = await app.StartWithConversationAsync(waitForTitle: false);
        await page.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.HasSummary && page.RunningSummary is null);
        var summary = await OpenSummaryAsync(app, page);

        // Edit the current version; the draft survives leaving the page.
        await summary.BeginEditCommand.ExecuteAsync(null);
        summary.EditBody = "我自己的笔记";
        await UiWait.UntilAsync(() => Detail(app, summary.Node).Draft is { Body: "我自己的笔记" }, 4000);
        summary.ReturnToConversationCommand.Execute(null);
        summary = await OpenSummaryAsync(app, Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage));
        await summary.BeginEditCommand.ExecuteAsync(null);
        Assert.Equal("我自己的笔记", summary.EditBody);
        await summary.SaveEditCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => summary.Shown?.Origin == SummaryOrigin.Edited && summary.ShownIsRead);

        // Regenerate: the edit stays current and the new text is a candidate, clearly labelled.
        app.Client.Knowledge.Replies.Enqueue($"标题：{FakeChatClient.DefaultSummaryTitle}\n\n新生成的正文");
        summary.ReturnToConversationCommand.Execute(null);
        page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.SummaryMarkersText?.Contains(UiText.MarkerCandidate, StringComparison.Ordinal) == true);
        summary = await OpenSummaryAsync(app, page);

        // The page may be the one opened before, refreshed by the change notification rather than rebuilt.
        await UiWait.UntilAsync(() => summary.HasCandidate);
        Assert.Equal("我自己的笔记", summary.Shown!.Body);

        // The read tag follows the version on screen and says which one; on the current version it is the node's own
        // marker, the one the network and the list show.
        Assert.Equal(UiText.MarkerRead, summary.ShownReadMarker);
        Assert.StartsWith(UiText.CurrentVersionRead(true), summary.ShownMarkersText, StringComparison.Ordinal);
        Assert.Contains(UiText.MarkerCandidate, summary.ShownMarkersText, StringComparison.Ordinal);

        summary.ShowCandidateCommand.Execute(null);
        Assert.True(summary.ShownIsCandidate);
        Assert.Equal("新生成的正文", summary.Shown!.Body);
        Assert.Equal(UiText.ThisVersionRead(false), summary.ShownReadMarker);
        Assert.StartsWith(UiText.ThisVersionRead(false), summary.ShownMarkersText, StringComparison.Ordinal);

        await summary.AdoptCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => !summary.HasCandidate);
        Assert.Equal("新生成的正文", Detail(app, summary.Node).Current.Body);
        Assert.Contains(summary.History, h => h.Version.Body == "我自己的笔记" && !h.IsCurrent);
    }

    [AvaloniaFact]
    public async Task Deleting_versions_asks_first_says_what_becomes_current_and_the_last_one_takes_the_summary_away()
    {
        await using var app = DesktopHarness.Create();
        var page = await app.StartWithConversationAsync(waitForTitle: false);
        await page.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.HasSummary && page.RunningSummary is null);
        var summary = await OpenSummaryAsync(app, page);
        await summary.BeginEditCommand.ExecuteAsync(null);
        summary.EditBody = "第二版";
        await summary.SaveEditCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => summary.History.Count == 2);

        summary.History.Single(h => h.IsCurrent).IsSelected = true;
        summary.DeleteSelectedCommand.Execute(null);
        Assert.Equal(UiText.DeleteVersionsConfirm(1, summary.History.Single(h => !h.IsCurrent).Version), summary.Confirmation!.Message);
        await summary.ConfirmCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => summary.History.Count == 1 && summary.Shown?.Seq == 1);

        Assert.Single(summary.History).IsSelected = true;
        summary.DeleteSelectedCommand.Execute(null);
        Assert.Contains("总结本身也会被删除", summary.Confirmation!.Message, StringComparison.Ordinal);
        await summary.ConfirmCommand.ExecuteAsync(null);

        var conversation = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await conversation.Loaded;
        await UiWait.UntilAsync(() => !conversation.HasSummary);
        Assert.Equal(2, conversation.Messages.Count);
        Assert.Equal(UiText.Summarize, conversation.SummarizeLabel);
    }

    [AvaloniaFact]
    public async Task A_failed_summary_says_so_in_the_conversation_and_a_retry_succeeds()
    {
        await using var app = DesktopHarness.Create();
        var page = await app.StartWithConversationAsync(waitForTitle: false);
        app.Client.Knowledge.TruncateNext();

        await page.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.SummaryProblem is not null);
        Assert.Contains("截断", page.SummaryProblem, StringComparison.Ordinal);
        Assert.False(page.HasSummary);

        await page.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.HasSummary && page.SummaryProblem is null);
    }

    [AvaloniaFact]
    public async Task Summarizing_while_an_answer_is_generated_is_refused_with_a_reason()
    {
        await using var app = DesktopHarness.Create();
        var page = await app.StartWithConversationAsync(waitForTitle: false);
        var held = app.Client.Hold("部分");
        page.Composer.Text = "继续";
        await page.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task;

        await page.SummarizeCommand.ExecuteAsync(null);

        Assert.Equal(UiText.SummarizeRejected(Application.Summaries.SummarizeRejection.AnswerInProgress), page.SummaryNotice);
        held.Release.TrySetResult();
    }

    /// <summary>
    /// A summary body goes through the same pipeline and styles as answers: bold covers exactly its words, and a
    /// paragraph holding only a one-line <c>$$…$$</c> is a centred formula block.
    /// </summary>
    [AvaloniaFact]
    public async Task A_summary_draws_bold_and_a_one_line_display_formula_like_an_answer()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        var page = await app.StartWithConversationAsync(waitForTitle: false);
        app.Client.Knowledge.Replies.Enqueue($"标题：{FakeChatClient.DefaultSummaryTitle}\n\n缆索牵引**适合陡坡**，但缆绳易磨损。\n\n$$P = F\\,v$$\n\n其中 $v$ 是缆绳速度。");
        await page.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.HasSummary && page.RunningSummary is null);
        await OpenSummaryAsync(app, page);
        window.UpdateLayout();
        var renderer = window.GetVisualDescendants().OfType<SummaryView>().Single().Renderer;

        var formula = Assert.Single(renderer.GetVisualDescendants().OfType<RaTeX.Avalonia.FormulaControl>(), f => f.Classes.Contains("MathBlock"));
        var centre = formula.TranslatePoint(new Point(formula.Bounds.Width / 2, 0), renderer)!.Value.X;
        Assert.InRange(centre, (renderer.Bounds.Width / 2) - 2, (renderer.Bounds.Width / 2) + 2);
        Assert.DoesNotContain(SourceAnchors.Blocks(renderer), b => b.LayoutText.Contains("$$", StringComparison.Ordinal));

        var runs = SourceAnchors.Blocks(renderer).SelectMany(b => b.TextLayout.TextLines).SelectMany(l => l.TextRuns)
            .OfType<Avalonia.Media.TextFormatting.ShapedTextRun>().ToList();
        Assert.All(runs.Where(r => r.Text.Span.ToString() is "适合陡坡"), r => Assert.Equal(Avalonia.Media.FontWeight.Bold, r.Properties.Typeface.Weight));
        Assert.Contains(runs, r => r.Text.Span.ToString() == "适合陡坡");
        Assert.All(runs.Where(r => r.Text.Span.ToString().Contains("缆绳易磨损", StringComparison.Ordinal)), r => Assert.True(r.Properties.Typeface.Weight < Avalonia.Media.FontWeight.SemiBold));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_page_has_one_primary_action_and_adopt_takes_it_only_while_the_candidate_is_on_screen()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        var page = await app.StartWithConversationAsync(waitForTitle: false);
        await page.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.HasSummary && page.RunningSummary is null);
        app.Client.Knowledge.Replies.Enqueue($"标题：{FakeChatClient.DefaultSummaryTitle}\n\n新生成的正文");
        await page.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.SummaryMarkersText?.Contains(UiText.MarkerCandidate, StringComparison.Ordinal) == true);
        var summary = await OpenSummaryAsync(app, page);
        await UiWait.UntilAsync(() => summary.HasCandidate);
        window.UpdateLayout();
        var view = window.GetVisualDescendants().OfType<SummaryView>().Single();
        Avalonia.Controls.Button Named(string name) => view.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Name == name);
        string[] Primaries() =>
            [.. view.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Where(b => b.Classes.Contains("primary") && b.IsEffectivelyVisible).Select(b => b.Name ?? string.Empty)];

        Assert.Equal(["ReturnButton"], Primaries());
        Assert.Contains("secondary", Named("AdoptButton").Classes);

        summary.ShowCandidateCommand.Execute(null);
        window.UpdateLayout();
        Assert.Equal(["AdoptButton"], Primaries());
        Assert.Contains("secondary", Named("ReturnButton").Classes);

        // Renaming takes the bar's place and the candidate steps aside, so the name's save is the one primary.
        Avalonia.Controls.Control Part(string name) => view.GetVisualDescendants().OfType<Avalonia.Controls.Control>().Single(c => c.Name == name);
        summary.BeginRenameCommand.Execute(null);
        window.UpdateLayout();
        Assert.False(Part("ActionBar").IsEffectivelyVisible);
        Assert.True(Part("RenameRow").IsEffectivelyVisible);
        Assert.False(Part("CandidatePanel").IsEffectivelyVisible);
        Assert.Equal(["CommitRenameButton"], Primaries());
        summary.CancelRenameCommand.Execute(null);
        window.UpdateLayout();
        Assert.True(Part("ActionBar").IsEffectivelyVisible);
        Assert.False(Part("RenameRow").IsEffectivelyVisible);
        Assert.Equal(["AdoptButton"], Primaries());

        // The candidate's note is muted inside the notice, not primary text.
        var note = view.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Single(t => t.Name == "CandidateNote");
        Assert.True(Avalonia.Controls.ResourceNodeExtensions.TryFindResource(view, "MutedText", view.ActualThemeVariant, out var muted));
        Assert.Equal(((Avalonia.Media.ISolidColorBrush)muted!).Color, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(note.Foreground).Color);

        // Confirming a discard is a danger step; with no question open the button is plain again.
        summary.DiscardCommand.Execute(null);
        window.UpdateLayout();
        Assert.True(summary.ConfirmationIsDestructive);
        Assert.True(Part("ConfirmationPanel").IsEffectivelyVisible);
        Assert.Contains("danger", Named("ConfirmButton").Classes);
        summary.CancelConfirmationCommand.Execute(null);
        window.UpdateLayout();
        Assert.DoesNotContain("danger", Named("ConfirmButton").Classes);

        // Editing hides the view's bar and the candidate; save is then the one primary.
        summary.ShowCurrentCommand.Execute(null);
        await summary.BeginEditCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Assert.False(Named("ReturnButton").IsEffectivelyVisible);
        Assert.False(view.GetVisualDescendants().OfType<Avalonia.Controls.Border>().Single(b => b.Name == "CandidatePanel").IsEffectivelyVisible);
        Assert.Equal(["SaveEditButton"], Primaries());
        Assert.Equal(UiText.EditingVersion(summary.Shown!.Seq), summary.EditingHeading);
        await summary.DiscardEditCommand.ExecuteAsync(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task History_rows_show_their_parts_the_shown_version_and_delete_waits_for_a_tick()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        var page = await app.StartWithConversationAsync(waitForTitle: false);
        await page.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.HasSummary && page.RunningSummary is null);
        var summary = await OpenSummaryAsync(app, page);
        window.UpdateLayout();
        var view = window.GetVisualDescendants().OfType<SummaryView>().Single();
        var history = view.GetVisualDescendants().OfType<Avalonia.Controls.Expander>().Single(e => e.Name == "HistoryPanel");
        Assert.True(history.IsEffectivelyVisible);

        // The history steps aside while editing: "set current", delete or view would change the version under the open edit.
        await summary.BeginEditCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Assert.False(history.IsEffectivelyVisible);
        summary.EditBody = "第二版";
        await summary.SaveEditCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => summary.History.Count == 2);
        window.UpdateLayout();
        Assert.True(history.IsEffectivelyVisible);

        Assert.Equal(UiText.HistoryHeadingCount(2), summary.HistoryHeader);
        var current = summary.History[0];
        var first = summary.History[1];
        Assert.Equal((UiText.VersionNumber(2), "当前", true), (current.Number, current.Role, current.IsEdited));
        Assert.Equal((UiText.VersionNumber(1), "历史", false), (first.Number, first.Role, first.IsEdited));
        Assert.Equal(UiText.VersionCoverage(first.Version), first.Coverage);
        Assert.True(current.IsShown);
        Assert.False(first.IsShown);

        summary.ShowVersionCommand.Execute(first.Id);
        Assert.True(first.IsShown);
        Assert.False(current.IsShown);

        // The shown row's neutral badges (已读, 手改) get the page surface and an outline against the row highlight.
        history.IsExpanded = true;
        summary.ShowVersionCommand.Execute(current.Id);
        window.UpdateLayout();
        var shownRow = view.GetVisualDescendants().OfType<Avalonia.Controls.Border>().Single(b => b.Classes.Contains("history-row") && b.Classes.Contains("shown"));
        var neutral = shownRow.GetVisualDescendants().OfType<Avalonia.Controls.Border>()
            .Where(b => b.Classes.Contains("badge") && !b.Classes.Contains("accent") && !b.Classes.Contains("warning") && b.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(neutral);
        Assert.True(Avalonia.Controls.ResourceNodeExtensions.TryFindResource(view, "OutlineBrush", view.ActualThemeVariant, out var outline));
        Assert.All(neutral, b =>
        {
            Assert.Equal(new Thickness(1), b.BorderThickness);
            Assert.Equal(((Avalonia.Media.ISolidColorBrush)outline!).Color, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(b.BorderBrush).Color);
        });
        var otherRow = view.GetVisualDescendants().OfType<Avalonia.Controls.Border>().Single(b => b.Classes.Contains("history-row") && !b.Classes.Contains("shown"));
        Assert.All(otherRow.GetVisualDescendants().OfType<Avalonia.Controls.Border>().Where(b => b.Classes.Contains("badge")), b => Assert.Equal(default, b.BorderThickness));

        // Delete waits for a tick, in the model and on the button itself.
        var delete = view.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Name == "DeleteSelectedButton");
        Assert.False(summary.HasSelectedVersions);
        Assert.False(delete.IsEffectivelyEnabled);
        first.IsSelected = true;
        Assert.True(summary.HasSelectedVersions);
        Assert.True(delete.IsEffectivelyEnabled);
        first.IsSelected = false;
        Assert.False(summary.HasSelectedVersions);
        Assert.False(delete.IsEffectivelyEnabled);

        // Deleting is a destructive question; its confirm button is a danger one.
        first.IsSelected = true;
        summary.DeleteSelectedCommand.Execute(null);
        window.UpdateLayout();
        Assert.True(summary.ConfirmationIsDestructive);
        Assert.Contains("danger", view.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Name == "ConfirmButton").Classes);
        summary.CancelConfirmationCommand.Execute(null);
        window.Close();
    }

    [Fact]
    public void Times_are_shown_as_a_chinese_date_and_a_24_hour_clock_in_local_time()
    {
        var local = new DateTime(2026, 3, 14, 9, 30, 0, DateTimeKind.Local);
        var at = new DateTimeOffset(local);
        Assert.Equal("2026年3月14日 09:30", UiText.FormatTime(at));
        Assert.Equal("2026年3月14日 09:30", UiText.FormatTime(at.ToUniversalTime()));
        Assert.Equal("2026年11月5日 21:05", UiText.FormatTime(new DateTimeOffset(new DateTime(2026, 11, 5, 21, 5, 0, DateTimeKind.Local))));
        Assert.Equal("生成于 2026年3月14日 09:30", UiText.NodeCreated(at));
        Assert.DoesNotContain("PM", UiText.FormatTime(at.AddHours(5)), StringComparison.Ordinal);
    }

    private static Application.Storage.SummaryDetail Detail(DesktopHarness app, SummaryNodeId node) =>
        app.Services.GetRequiredService<Application.Summaries.SummaryService>().GetAsync(node, CancellationToken.None).Result!;

    private static async Task<SummaryViewModel> OpenSummaryAsync(DesktopHarness app, ConversationViewModel page)
    {
        await UiWait.UntilAsync(() => page.HasSummary);
        page.OpenSummaryCommand.Execute(null);
        var summary = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await summary.Loaded;
        return summary;
    }
}
