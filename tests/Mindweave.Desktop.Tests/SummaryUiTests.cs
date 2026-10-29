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
        var clock = new ManualTime();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<TimeProvider>(clock));
        var window = new MainWindow { DataContext = app.Root, Width = 1000, Height = 700 };
        window.Show();
        var page = await ConversationAsync(app);

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
        ViewTests.Capture(window, "06-summary");

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
        var page = await ConversationAsync(app);
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
        app.Client.SummaryReplies.Enqueue($"标题：{FakeChatClient.DefaultSummaryTitle}\n\n新生成的正文");
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
        var page = await ConversationAsync(app);
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
        var page = await ConversationAsync(app);
        app.Client.TruncateKnowledge = 1;

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
        var page = await ConversationAsync(app);
        var held = app.Client.Hold("部分");
        page.Composer.Text = "继续";
        await page.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task;

        await page.SummarizeCommand.ExecuteAsync(null);

        Assert.Equal(UiText.SummarizeRejected(Application.Summaries.SummarizeRejection.AnswerInProgress), page.SummaryNotice);
        held.Release.TrySetResult();
    }

    private static Application.Storage.SummaryDetail Detail(DesktopHarness app, SummaryNodeId node) =>
        app.Services.GetRequiredService<Application.Summaries.SummaryService>().GetAsync(node, CancellationToken.None).Result!;

    private static async Task<ConversationViewModel> ConversationAsync(DesktopHarness app)
    {
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = "火车是怎么发明的？";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].CanBranch);
        return page;
    }

    private static async Task<SummaryViewModel> OpenSummaryAsync(DesktopHarness app, ConversationViewModel page)
    {
        await UiWait.UntilAsync(() => page.HasSummary);
        page.OpenSummaryCommand.Execute(null);
        var summary = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await summary.Loaded;
        return summary;
    }

    /// <summary>Real time plus an offset the test moves forward, for the minimum reading time.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _offset;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _offset, (long)(by.TotalSeconds * TimestampFrequency));

        public override long GetTimestamp() => System.GetTimestamp() + Interlocked.Read(ref _offset);

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + TimeSpan.FromSeconds((double)Interlocked.Read(ref _offset) / TimestampFrequency);
    }
}
