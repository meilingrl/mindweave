using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Exchange;
using Mindweave.Application.Import;
using Mindweave.Application.Share;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views.Conversations;
using Mindweave.Infrastructure.Share;
using Mindweave.Testing;
using static Mindweave.Desktop.Tests.UiTree;

using TextBlock = Avalonia.Controls.TextBlock;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Importing a share with content (docs/design/sharing-import.md §3 to §5, §9 scenarios 6 to 13) as the user does it: the
/// dialog's counts, a received conversation's page with its stand-ins, continuing from it and the way back.
/// </summary>
public sealed class ImportContentUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mindweave-import-content-ui", Guid.NewGuid().ToString("N"));

    public ImportContentUiTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly ExchangeMessage Asked = SharedParts.Question(0, "这些里面有什么？", "计划.pdf", null);

    private static readonly ExchangeMessage Told = SharedParts.Answer(1, "标准轨距是 1435 毫米[1]，见附件[2]和总结[3]。") with
    {
        Activity = new(1, 0, 2, 1, 3),
        Citations = [new(1, CitationKind.Web, "轨距", new Uri("https://example.org/gauge")), new(2, CitationKind.Attachment), new(3, CitationKind.Summary, "轨道")],
    };

    private static readonly ExchangeConversation Root = SharedParts.Conversation(null, "火车起源", null, Asked, Told);
    private static readonly ExchangeConversation Branch = SharedParts.Conversation(Root, "标准轨距", SharedParts.From(Told, "1435 毫米"), SharedParts.Question(0, "为什么？"), SharedParts.Answer(1, "因为马车。", MessageState.Failed));

    private static ExchangeDocument Share => SharedParts.Document(Root, Branch);

    [AvaloniaFact]
    public async Task The_dialog_says_what_is_new_what_gains_and_what_is_skipped()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("历史");
        var import = await OpenAsync(app, Share.Structure());
        import.Targets.Single(t => t.Workspace is not null).IsChosen = true;
        await import.ImportCommand.ExecuteAsync(null);

        // The same with content, and one more: the two gain their messages.
        var more = SharedParts.Conversation(Root, "另一支", SharedParts.From(Told, "见附件"), SharedParts.Question(0, "附件？"), SharedParts.Answer(1, "没有。"));
        await OpenAsync(app, SharedParts.Document(Root, Branch, more));
        Assert.Equal(UiText.ImportKindContent, import.KindText);
        Assert.Equal([UiText.ImportNew(1), UiText.ImportGainsMessages(2)], import.Counts);
        Assert.True(import.ImportCommand.CanExecute(null));
        await import.ImportCommand.ExecuteAsync(null);
        Assert.Equal(more.Id, Assert.IsType<ConversationRoute>(app.Workbench.CurrentPage!.Route).Id);

        // Again: nothing to import.
        await OpenAsync(app, SharedParts.Document(Root, Branch, more));
        Assert.True(import.IsAllHere);
        import.CloseCommand.Execute(null);

        // A summary for one that is here, and a file that does not fit another.
        var clash = Branch with { Content = Branch.Content! with { Messages = [Branch.Content.Messages[0], SharedParts.Answer(1, "别的回答。")] } };
        await OpenAsync(app, SharedParts.Document(Root.With(SharedParts.Summary("起源", 1)), clash, more));
        Assert.Equal([UiText.ImportGainsMore(1), UiText.ImportHere(1), UiText.ImportUnfit(1)], import.Counts);
        import.CloseCommand.Execute(null);

        // Only what does not fit: not "everything is here", and nothing to import.
        await OpenAsync(app, SharedParts.Document(Root, clash, more));
        Assert.False(import.IsAllHere);
        Assert.False(import.ImportCommand.CanExecute(null));
        Assert.Contains(UiText.ImportUnfit(1), import.Counts);
    }

    /// <summary>Acceptance 7, as a page that is open sees it: an empty conversation fills while it is shown.</summary>
    [AvaloniaFact]
    public async Task An_open_empty_conversation_fills_and_its_row_is_no_longer_muted()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("历史");
        await ImportAsync(app, Share.Structure());
        var page = await OpenConversationAsync(app, Branch.Id);
        Assert.True(page.IsUnexplored);

        await app.Services.GetRequiredService<IImports>().ImportAsync(Share, ImportTarget.Existing(app.Workbench.CurrentWorkspace!.Id), CancellationToken.None);

        await UiWait.UntilAsync(() => page.Messages.Count == 2 && !page.IsUnexplored);
        Assert.Equal("1435 毫米", page.SourceQuote);
        Assert.True(page.CanReturnToSource);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.All(i => !i.IsUnexplored));
    }

    /// <summary>I4 and §3: no retry anywhere, the input box says where the question goes, and an answer's details say it was imported.</summary>
    [AvaloniaFact]
    public async Task A_received_conversation_is_read_only_and_its_answers_say_they_were_imported()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("历史");
        await app.ConfigureModelAsync();
        await ImportAsync(app, Share);

        foreach (var id in new[] { Root.Id, Branch.Id })
        {
            var page = await OpenConversationAsync(app, id);
            await UiWait.AnswersRenderedAsync(window);
            window.UpdateLayout();
            Assert.True(page.IsReceived);
            Assert.Equal(UiText.ContinueReceivedPlaceholder, page.Composer.Placeholder);
            Assert.All(page.Messages, m => Assert.False(m.CanRetry || m.CanRetryQuietly || m.CanRetryInNotice));
            Assert.DoesNotContain(Parts<Button>(window), b => b.Name is "RetryButton" or "ErrorRetryButton" && b.IsEffectivelyVisible);
            Assert.EndsWith(UiText.ImportedAnswer, Parts<TextBlock>(window).Single(t => t.Name == "AnswerImported" && t.IsEffectivelyVisible).Text!, StringComparison.Ordinal);
            Assert.Null(page.Messages[1].DurationText);
            Assert.True(page.Messages[1].CanBranch);

            // A model chosen or a reload changes nothing of it.
            await app.ConfigureModelAsync();
            await Task.Delay(150);
            Assert.False(page.Messages[1].CanRetry);
        }
    }

    /// <summary>Acceptance 12.</summary>
    [AvaloniaFact]
    public async Task What_did_not_travel_is_shown_by_name_and_only_a_web_citation_opens_in_the_browser()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("历史");
        await ImportAsync(app, Share);
        var page = await OpenConversationAsync(app, Root.Id);
        await UiWait.AnswersRenderedAsync(window);
        var answer = page.Messages[1];

        var chips = page.Messages[0].Attachments;
        Assert.Equal([("计划.pdf", UiText.NotIncluded, false), (UiText.PastedTextName, UiText.NotIncluded, false)], chips.Select(c => (c.Name, c.Detail, c.CanOpen)));
        chips[0].OpenCommand.Execute(null);
        Assert.False(page.AttachmentViewer.IsOpen);

        Assert.False(answer.HasActivity);
        Assert.Equal("检索了 1 次附件 · 搜索了 2 次 · 读了 1 个网页 · 调用了 3 次其他工具", answer.ActivityCountText);
        Assert.Contains(Parts<StackPanel>(window), p => p.Name == "ActivityCount" && p.IsEffectivelyVisible);
        Assert.DoesNotContain(Parts<StackPanel>(window), p => p.Name == "RunActivityPanel" && p.IsEffectivelyVisible);

        answer.ToggleCitationsCommand.Execute(null);
        window.UpdateLayout();
        Assert.Equal(
            [("轨距", "example.org", true), ("一个附件", UiText.NotIncluded, false), ("总结《轨道》", UiText.CitationPlace(CitationKind.Summary), false)],
            answer.Citations.Select(c => (c.Title, c.Meta, c.CanOpen)));
        Assert.Equal(2, Parts<Grid>(window).Count(g => g.Name == "PlainCitation" && g.IsEffectivelyVisible));
        Assert.Equal([1], answer.CitationNumbers);
        Assert.False(answer.OpenCitation(2));
        Assert.False(page.AttachmentViewer.IsOpen);

        // The address is shown in full and asked about; there is no page to view.
        var viewer = page.AttachmentViewer;
        var opened = new List<Uri>();
        viewer.OpenRequested += (_, url) => opened.Add(url);
        Assert.True(answer.OpenCitation(1));
        window.UpdateLayout();
        Assert.True(viewer is { IsOpen: true, IsConfirmingOpen: true, HasText: false, HasNote: false });
        Assert.Equal("https://example.org/gauge", Find<SelectableTextBlock>(window, "ViewerConfirmAddress").Text);
        Assert.False(Find<Button>(window, "ViewerOpenInBrowser").IsEffectivelyVisible);
        viewer.CancelOpenCommand.Execute(null);
        Assert.False(viewer.IsOpen);
        Assert.Empty(opened);
        answer.OpenCitation(1);
        viewer.ConfirmOpenCommand.Execute(null);
        Assert.False(viewer.IsOpen);
        Assert.Equal([new Uri("https://example.org/gauge")], opened);
    }

    /// <summary>Acceptance 6 and I4: the question continues in a conversation of the user's own, and both show how they hang together.</summary>
    [AvaloniaFact]
    public async Task A_question_at_the_end_opens_a_continuation_that_leads_back_to_the_end()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1000, 600);
        await app.StartWithWorkspaceAsync("历史");
        await app.ConfigureModelAsync();
        var lines = string.Join("\n\n", Enumerable.Range(1, 60).Select(i => $"第{i}段。"));
        var root = SharedParts.Conversation(null, "火车起源", null, SharedParts.Question(0, "火车？"), SharedParts.Answer(1, lines), SharedParts.Question(2, "然后呢？"), SharedParts.Answer(3, lines));
        await ImportAsync(app, SharedParts.Document(root));
        var received = await OpenConversationAsync(app, root.Id);
        app.Client.Reply("接着是电力。");
        app.Client.Titles.Replies.Enqueue("电力机车");
        received.Composer.Text = "再然后呢？";

        await received.Composer.SendCommand.ExecuteAsync(null);

        var own = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await own.Loaded;
        Assert.NotEqual(root.Id, own.Id);
        await UiWait.UntilAsync(() => own.Messages.Count == 2 && own.RunningAnswer is null && own.Messages[1].CanRetry && own.Title == "电力机车");
        Assert.Equal(string.Empty, received.Composer.Text);
        window.UpdateLayout();
        Assert.False(own.IsReceived);
        Assert.Equal(UiText.AskPlaceholder, own.Composer.Placeholder);
        Assert.Equal(["火车起源"], own.Lineage.Select(l => l.Title));
        Assert.True(Find<Button>(window, "ReturnButton").IsEffectivelyVisible);
        Assert.Equal(UiText.ReturnToEndHint, own.ReturnHint);
        Assert.False(Find<TextBlock>(window, "QuoteText").IsEffectivelyVisible);

        own.ReturnToSourceCommand.Execute(null);
        var back = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await back.Loaded;
        Assert.Equal(root.Id, back.Id);
        await CurrentView(window).Restored;
        await UiWait.AnswersRenderedAsync(window);
        window.UpdateLayout();
        Assert.Null(back.SourceNotice);
        var scroller = Scroller(window);
        Assert.InRange(scroller.Offset.Y, scroller.Extent.Height - scroller.Viewport.Height - 80, scroller.Extent.Height);

        // Listed under the last answer by its title; opening it is navigation.
        var entry = Assert.Single(back.Messages[^1].Branches);
        Assert.Equal((own.Id, "电力机车", true, "电力机车"), (entry.Child, entry.Text, entry.IsContinuation, entry.Label));
        Assert.Equal(4, back.Messages.Count);
        Assert.All(back.Messages.SkipLast(1), m => Assert.Empty(m.Branches));
        back.OpenConversationCommand.Execute(entry.Child);
        Assert.Equal(own.Id, Assert.IsType<ConversationRoute>(app.Workbench.CurrentPage!.Route).Id);
    }

    /// <summary>A received question nobody answered: the continuation is listed under the answer before it.</summary>
    [AvaloniaFact]
    public async Task A_continuation_of_an_unanswered_question_is_listed_under_the_answer_before_it()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1000, 600);
        await app.StartWithWorkspaceAsync("历史");
        await app.ConfigureModelAsync();
        var root = SharedParts.Conversation(null, "火车起源", null, SharedParts.Question(0, "火车？"), SharedParts.Answer(1, "蒸汽。"), SharedParts.Question(2, "然后呢？"));
        await ImportAsync(app, SharedParts.Document(root));
        var sent = await app.Services.GetRequiredService<ConversationService>().AskAsync(root.Id, "有人答吗？", CancellationToken.None);

        var page = await OpenConversationAsync(app, root.Id);

        await UiWait.UntilAsync(() => page.Messages[1].Branches is [{ IsContinuation: true }]);
        Assert.Equal(sent.Conversation, page.Messages[1].Branches[0].Child);
    }

    /// <summary>Acceptance 6: the file's anchor marks the sentence when this app can use it; otherwise the way back leads to the answer.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Returning_to_a_received_source_marks_the_sentence_only_with_an_anchor_this_app_can_use(bool usable)
    {
        ExchangeDocument document;
        ConversationId child;
        await using (var sharer = DesktopHarness.Create())
        {
            var window = sharer.ShowWindow(1000, 700);
            sharer.Client.Reply("蒸汽机车由**斯蒂芬森**改良。\n\n铁路随后在英国普及。");
            var page = await sharer.StartWithConversationAsync(waitForTitle: true);
            await UiWait.AnswersRenderedAsync(window);
            await UiWait.UntilAsync(() => Answers(window) is [{ IsLayoutCurrent: true }]);
            window.UpdateLayout();
            var answer = Answers(window)[0];
            var block = SourceAnchors.Blocks(answer.Renderer).Single(b => b.LayoutText.Contains("铁路随后", StringComparison.Ordinal));
            (block.SelectionStart, block.SelectionEnd) = (0, "铁路随后".Length);
            await page.CreateBranchAsync(answer.CaptureSelection()!);
            child = Assert.IsType<ConversationViewModel>(sharer.Workbench.CurrentPage).Id;
            var workspace = sharer.Workbench.CurrentWorkspace!.Id;
            var source = await sharer.Services.GetRequiredService<IShares>().ReadAsync(workspace, CancellationToken.None);
            document = await sharer.Services.GetRequiredService<IShareContents>().BuildAsync("火车", workspace, source, ShareSelection.Exploration(source.Parents, page.Id), SharedParts.Day, "0.7.0", CancellationToken.None);
            window.Close();
        }

        var sent = document.Conversations.Single(c => c.Id == child);
        Assert.NotNull(sent.Content!.Source!.Anchor);
        Assert.NotNull(sent.Content.Source.Position);
        if (!usable)
        {
            document = document with { Conversations = [.. document.Conversations.Select(c => c.Id != child ? c : c with { Content = c.Content! with { Source = c.Content.Source! with { Anchor = c.Content.Source.Anchor! with { Profile = "another" } } } })] };
        }

        await using var app = DesktopHarness.Create();
        var shown = app.ShowWindow(1000, 700);
        await app.StartWithWorkspaceAsync("历史");
        await ImportAsync(app, document);
        var branch = await OpenConversationAsync(app, child);
        Assert.Equal("铁路随后", branch.SourceQuote);

        branch.ReturnToSourceCommand.Execute(null);
        var root = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await root.Loaded;
        await CurrentView(shown).Restored;

        var marked = CurrentView(shown).GetVisualDescendants().OfType<MarkdownTextBlock>().Where(b => b.Highlights.TryGetValue("branch-source", out _)).ToList();
        Assert.Equal(usable ? 1 : 0, marked.Count);
        Assert.Equal(usable, root.SourceNotice is null);
        Assert.Equal("铁路随后", Assert.Single(root.Messages[1].Branches).Text);

        // The same place as before travels on when it is shared again.
        var again = await app.Services.GetRequiredService<IShares>().ReadAsync(app.Workbench.CurrentWorkspace!.Id, CancellationToken.None);
        var back = await app.Services.GetRequiredService<IShareContents>().BuildAsync("火车", app.Workbench.CurrentWorkspace!.Id, again, ShareSelection.Exploration(again.Parents, root.Id), SharedParts.Day, "0.7.0", CancellationToken.None);
        Assert.Equal(document.Conversations.Single(c => c.Id == child).Content!.Source, back.Conversations.Single(c => c.Id == child).Content!.Source);
    }

    /// <summary>§4: an anchor from a file can say anything; returning then goes to the answer, and sharing again works.</summary>
    [AvaloniaTheory]
    [InlineData("{\"version\":1,\"sourceLength\":0,\"sourceHash\":\"x\",\"segments\":[null]}")]
    [InlineData("{\"version\":1,\"sourceLength\":-1,\"sourceHash\":null,\"segments\":null}")]
    [InlineData("{\"version\":1,\"sourceLength\":99999,\"sourceHash\":\"x\",\"segments\":[{\"sourceStart\":-5,\"sourceEnd\":2147483647,\"ordinal\":0,\"layoutHash\":null,\"start\":0,\"length\":1,\"quote\":null}]}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task A_planted_anchor_only_fails_to_mark(string anchor)
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1000, 700);
        await app.StartWithWorkspaceAsync("历史");
        var branch = Branch with { Content = Branch.Content! with { Source = new(Told.Id, "1435 毫米", null, new(SourceAnchors.FormatVersion, SourceAnchors.RenderProfile, anchor)) } };
        await ImportAsync(app, SharedParts.Document(Root, branch));
        var page = await OpenConversationAsync(app, Branch.Id);

        page.ReturnToSourceCommand.Execute(null);
        var root = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await root.Loaded;
        await CurrentView(window).Restored;

        Assert.NotNull(root.SourceNotice);
        var source = await app.Services.GetRequiredService<IShares>().ReadAsync(app.Workbench.CurrentWorkspace!.Id, CancellationToken.None);
        var back = await app.Services.GetRequiredService<IShareContents>().BuildAsync("火车", app.Workbench.CurrentWorkspace!.Id, source, ShareSelection.Exploration(source.Parents, Root.Id), SharedParts.Day, "0.7.0", CancellationToken.None);
        Assert.NotEmpty(SharePage.Encode(back, app.Services.GetRequiredService<IShareMarkdown>()));
    }

    /// <summary>§4: what is written in a received answer is text. Nothing in it runs, loads or opens.</summary>
    [AvaloniaFact]
    public async Task A_script_in_a_received_answer_cannot_run()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1000, 700);
        await app.StartWithWorkspaceAsync("历史");
        var trap = Path.Combine(_dir, "ran");
        var answer = $"""
            前文。<script>require('fs').writeFileSync('{trap}', '1')</script>

            <script src="file://{trap}.js"></script>
            <iframe src="file://{trap}"></iframe><img src="file://{trap}.png" onerror="alert(1)">

            ![图](file://{trap}.png) [打开](javascript:alert(1)) [文件](file://{trap}) [网页](https://example.org/x)

            后文。
            """;
        await ImportAsync(app, SharedParts.Document(SharedParts.Conversation(null, "<script>alert(1)</script>", null, SharedParts.Question(0, "<script>alert(1)</script>?"), SharedParts.Answer(1, answer))));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        await UiWait.AnswersRenderedAsync(window);
        window.UpdateLayout();

        var view = Assert.Single(Answers(window));
        var shown = string.Concat(SourceAnchors.Blocks(view.Renderer).Select(b => b.LayoutText));
        Assert.Contains("前文", shown, StringComparison.Ordinal);
        Assert.Contains("后文", shown, StringComparison.Ordinal);
        Assert.DoesNotContain(view.GetVisualDescendants(), v => v is NativeControlHost);
        Assert.All(view.GetVisualDescendants().OfType<Image>(), i => Assert.Null(i.Source));
        Assert.Equal("<script>alert(1)</script>?", page.Messages[0].Body);
        Assert.Equal("<script>alert(1)</script>", page.Title);

        Assert.False(page.AttachmentViewer.IsOpen);
        Assert.False(File.Exists(trap));
    }

    private async Task<ImportViewModel> OpenAsync(DesktopHarness app, ExchangeDocument document)
    {
        var path = Path.Combine(_dir, "share.html");
        await File.WriteAllBytesAsync(path, SharePage.Encode(document, app.Services.GetRequiredService<IShareMarkdown>()), CancellationToken.None);
        await app.Root.Import.OpenAsync(path);
        Assert.Null(app.Root.Import.Problem);
        return app.Root.Import;
    }

    /// <summary>Into the current workspace, through the dialog.</summary>
    private async Task ImportAsync(DesktopHarness app, ExchangeDocument document)
    {
        var import = await OpenAsync(app, document);
        import.Targets.Single(t => t.Workspace == app.Workbench.CurrentWorkspace!.Id).IsChosen = true;
        await import.ImportCommand.ExecuteAsync(null);
        Assert.False(import.IsOpen);
    }

    private static async Task<ConversationViewModel> OpenConversationAsync(DesktopHarness app, ConversationId id)
    {
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(id)));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        Assert.Equal(id, page.Id);
        await page.Loaded;
        return page;
    }

    private static IEnumerable<T> Parts<T>(Window window) => window.GetVisualDescendants().OfType<T>();
}
