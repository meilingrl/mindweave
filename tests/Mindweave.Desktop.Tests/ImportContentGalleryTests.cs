using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Exchange;
using Mindweave.Application.Languages;
using Mindweave.Application.Share;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Infrastructure.Share;
using Mindweave.Testing;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Importing a share with content, for looking at (MINDWEAVE_SCREENSHOT_DIR, run with <c>--filter FullyQualifiedName~ImportContentGallery</c>):
/// the dialog, a received conversation with its stand-ins and its input box, the browser question, a continuation and its way
/// back, and a newer file: import-content-*.png, and import-content-sample-*.html (first the outline, then with content, then newer) to import by hand.
/// </summary>
[Trait("Category", "Gallery")]
[Collection(Text.LanguageTests.Name)]
public class ImportContentGalleryTests
{
    [AvaloniaTheory]
    [InlineData("Light", UiLanguage.English)]
    [InlineData("Dark", UiLanguage.English)]
    [InlineData("Light", UiLanguage.Chinese)]
    [InlineData("Dark", UiLanguage.Chinese)]
    public async Task Import_with_content_as_it_looks(string theme, UiLanguage language)
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR")), "Screenshots only.");
        var before = CurrentLanguage.Value;
        CurrentLanguage.Set(language);
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1100, 760, theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light);
            var english = language == UiLanguage.English;
            var look = $"{theme.ToLowerInvariant()}-{(english ? "en" : "zh")}";
            string T(string en, string zh) => english ? en : zh;
            await app.StartWithWorkspaceAsync(T("History", "历史"));
            await app.ConfigureModelAsync();
            var file = System.IO.Path.Combine(app.Paths.DataDirectory, "share.html");
            static ConversationId Id(int n) => ConversationId.Parse($"0190f000-0000-7000-8000-{n:d12}");
            static MessageId Said(int n) => MessageId.Parse($"0190f000-0000-7000-8001-{n:d12}");

            var asked = SharedParts.Question(0, T("What do these say about where the railway gauge comes from?", "这些材料怎么说轨距的由来？"), T("gauge-history.pdf", "轨距史.pdf"), null) with { Id = Said(1) };
            var told = SharedParts.Answer(1, T(
                "The first public lines copied the **colliery wagonways** they grew out of [1]. Those were built for carts about five feet wide, as the attachment says [2].\n\nStephenson kept the width for the Liverpool and Manchester Railway, and 1435 mm spread with his engines [3].",
                "最早的公共铁路沿用了它们所脱胎的**煤矿轨道车** [1]。那些轨道是按约五英尺宽的马车修的，附件里也这么说 [2]。\n\n斯蒂芬森在利物浦—曼彻斯特铁路上保留了这个宽度，1435 毫米随着他的机车传开 [3]。")) with
            {
                Id = Said(2),
                Activity = new(1, 0, 2, 1, 0),
                Citations =
                [
                    new(1, CitationKind.Web, T("Wagonway - an encyclopedia", "马拉轨道车 - 百科"), new Uri("https://example.org/wagonway?edition=2026")),
                    new(2, CitationKind.Attachment),
                    new(3, CitationKind.Summary, T("Rails before engines", "先有轨道，后有机车"), Summary: Id(2)),
                ],
            };
            var askedMore = SharedParts.Question(2, T("Why did other gauges lose?", "为什么别的轨距输了？")) with { Id = Said(3) };
            var toldMore = SharedParts.Answer(3, T("Because through trains were worth more than a better width: once most lines shared one gauge, every other one meant changing trains", "因为直通列车比更好的宽度更值钱：一旦大多数线路用同一种轨距，其他轨距就意味着换车"), MessageState.Stopped) with { Id = Said(4) };
            var root = SharedParts.Conversation(null, T("Why 1435 mm", "为什么是 1435 毫米"), null, asked, told, askedMore, toldMore) with { Id = Id(1) };
            var branch = SharedParts.Conversation(root, T("Colliery wagonways", "煤矿轨道车"), SharedParts.From(told, T("colliery wagonways", "煤矿轨道车")),
                SharedParts.Question(0, T("What ran on them?", "上面跑什么？")) with { Id = Said(5) },
                SharedParts.Answer(1, T("Horse-drawn carts with coal.", "马拉的运煤车。")) with { Id = Said(6) }) with { Id = Id(2) };
            branch = branch.With(SharedParts.Summary(T("Rails before engines", "先有轨道，后有机车"), 1));
            var document = SharedParts.Document(root, branch) with { Title = T("Where the gauge comes from", "轨距的由来") };
            var further = SharedParts.Conversation(branch, T("The first iron rails", "最早的铁轨"), SharedParts.From(branch.Content!.Messages[1], T("carts", "运煤车")),
                SharedParts.Question(0, T("When did rails become iron?", "轨道什么时候变成铁的？")), SharedParts.Answer(1, T("In the 1760s.", "十八世纪六十年代。"))) with { Id = Id(3) };
            var newer = SharedParts.Document(
                root.And(SharedParts.Question(4, T("And Brunel?", "布鲁内尔呢？")), SharedParts.Answer(5, T("He kept seven feet until 1892.", "他的七英尺轨距用到 1892 年。"))), branch, further) with { Title = document.Title };

            async Task SaveAsync(ExchangeDocument shared, string path) =>
                await File.WriteAllBytesAsync(path, SharePage.Encode(shared, app.Services.GetRequiredService<IShareMarkdown>()), CancellationToken.None);
            async Task OpenAsync(ExchangeDocument shared)
            {
                await SaveAsync(shared, file);
                await app.Root.Import.OpenAsync(file);
                window.UpdateLayout();
            }

            async Task<ConversationViewModel> ShowAsync(ConversationId id)
            {
                app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(id)));
                return await ShownAsync();
            }

            async Task<ConversationViewModel> ShownAsync()
            {
                var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
                await page.Loaded;
                await UiWait.AnswersRenderedAsync(window);
                window.UpdateLayout();
                return page;
            }

            foreach (var (shared, name) in new[] { (document.Structure(), "1-outline"), (document, "2-content"), (newer, "3-newer") })
            {
                await SaveAsync(shared, System.IO.Path.Combine(Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR")!, $"import-content-sample-{name}-{(english ? "en" : "zh")}.html"));
            }

            await OpenAsync(document);
            Screenshots.Capture(window, $"import-content-1-dialog-{look}");
            app.Root.Import.CloseCommand.Execute(null);

            // The outline first, then what was said: the dialog tells what gains messages.
            await OpenAsync(document.Structure());
            app.Root.Import.Targets.Single(t => t.Workspace is not null).IsChosen = true;
            await app.Root.Import.ImportCommand.ExecuteAsync(null);
            await OpenAsync(document);
            Screenshots.Capture(window, $"import-content-2-dialog-gains-{look}");
            await app.Root.Import.ImportCommand.ExecuteAsync(null);
            var page = await ShownAsync();
            await UiWait.UntilAsync(() => page.Messages.Count == 4);
            await ShownAsync();
            page.Messages[1].IsCitationsExpanded = true;
            await Task.Delay(200, CancellationToken.None);
            window.UpdateLayout();
            UiTree.Scroller(window).Offset = default;
            window.UpdateLayout();
            Screenshots.Capture(window, $"import-content-3-received-{look}");
            UiTree.Scroller(window).ScrollToEnd();
            window.UpdateLayout();
            Screenshots.Capture(window, $"import-content-4-received-end-{look}");
            page.Messages[1].OpenCitation(1);
            await Task.Delay(300, CancellationToken.None);
            window.UpdateLayout();
            Screenshots.Capture(window, $"import-content-5-open-in-browser-{look}");
            page.AttachmentViewer.CloseCommand.Execute(null);

            await ShowAsync(Id(2));
            Screenshots.Capture(window, $"import-content-6-received-branch-{look}");

            page = await ShowAsync(Id(1));
            app.Client.Reply(T("It stayed because changing it would have meant relaying everything at once.", "它留下来，是因为要改就得一次重铺全部轨道。"));
            app.Client.Titles.Replies.Enqueue(T("Why it stayed", "为什么没有改"));
            page.Composer.Text = T("Why did nobody change it later?", "后来为什么没人改？");
            await page.Composer.SendCommand.ExecuteAsync(null);
            var own = await ShownAsync();
            await UiWait.UntilAsync(() => own.Messages.Count == 2 && own.RunningAnswer is null && own.Title == T("Why it stayed", "为什么没有改"));
            await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Any(i => i.Id == own.Id && i.Title == own.Title));
            await ShownAsync();
            Screenshots.Capture(window, $"import-content-7-continuation-{look}");
            own.ReturnToSourceCommand.Execute(null);
            await ShownAsync();
            await UiTree.CurrentView(window).Restored;
            window.UpdateLayout();
            Screenshots.Capture(window, $"import-content-8-back-at-the-end-{look}");

            await OpenAsync(newer);
            Screenshots.Capture(window, $"import-content-9-dialog-newer-{look}");
            await app.Root.Import.ImportCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 4);
            var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
            network.SetViewModeCommand.Execute(SummaryNetworkViewMode.Timeline);
            window.UpdateLayout();
            Screenshots.Capture(window, $"import-content-10-timeline-{look}");
        }
        finally
        {
            CurrentLanguage.Set(before);
        }
    }
}
