using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Export;
using Mindweave.Application.Languages;
using Mindweave.Application.Models;
using Mindweave.Application.Resources;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Application.Tools;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The share dialog with content, and a shared page saved through it, for looking at (MINDWEAVE_SCREENSHOT_DIR, run with
/// <c>--filter FullyQualifiedName~ShareContentGallery</c>): share-content-*.png and share-content-sample.html.
/// </summary>
[Trait("Category", "Gallery")]
[Collection(Text.LanguageTests.Name)]
public class ShareContentGalleryTests
{
    private const string RootAnswer = """
        It was the other way round. Tracks are about two hundred years older than locomotives: from the 1600s, mines in England and Germany laid wooden wagonways so that a horse could pull a much heavier cart of coal than it could on a road[1].

        The steam engine arrived in mines too, but as a stationary pump. 火车的诞生是**“蒸汽动力”**与**轨道运输**两条线索的汇合。 Starting tractive effort is usually estimated as

        $$F = \frac{0.85\,p\,d^2 s}{D}$$

        where $p$ is the boiler pressure, \(d\) the cylinder diameter and $s$ the stroke; in the same units:

        \[
        \begin{aligned}
        P &= F v \\
        W &= \int_0^T P \, \mathrm{d}t
        \end{aligned}
        \]

        | Year | Engine | Speed |
        | --- | :---: | ---: |
        | 1804 | **Penydarren** | 8 km/h |
        | 1829 | *Rocket* | 47 km/h |

        1. Multi-tube boiler
        2. Blast pipe
           - exhaust steam up the chimney
           - draws the fire

        > The track was as much the problem as the engine.

        ```python
        def effort(p, d, s, D):
            return 0.85 * p * d**2 * s / D  # <b>not markup</b>
        ```

        For twenty years the question of how far apart the rails should be was settled almost by accident. A picture: ![Rocket](https://example.org/rocket.png). <script>alert("never")</script> <img src=x onerror=alert(1)>
        """;

    [AvaloniaFact]
    public async Task The_dialog_and_a_page_with_content()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR")), "Screenshots only.");
        var before = CurrentLanguage.Value;
        try
        {
            foreach (var (theme, language) in new[] { ("Light", UiLanguage.English), ("Dark", UiLanguage.Chinese) })
            {
                CurrentLanguage.Set(language);
                await using var app = DesktopHarness.Create();
                var window = app.ShowWindow(1100, 820, theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light);
                var root = await SeedAsync(app);
                var list = app.ShellPage.Conversations;
                await UiWait.UntilAsync(() => list.Items.Any(i => i.Id == root));
                list.ShareRowCommand.Execute(list.Items.Single(i => i.Id == root));
                var share = app.Root.Share;
                await share.Loading;
                var look = $"{theme.ToLowerInvariant()}-{(language == UiLanguage.English ? "en" : "zh")}";
                var scroller = Find<ScrollViewer>(window, "ShareScroller");
                scroller.Offset = new Avalonia.Vector(0, 10_000);
                await SettleAsync(window);
                Screenshots.Capture(window, "share-structure-dialog-end-" + look);
                scroller.Offset = default;
                share.IncludesContent = true;
                await share.Building;
                await SettleAsync(window);
                Screenshots.Capture(window, "share-content-dialog-" + look);
                scroller.Offset = new Avalonia.Vector(0, 10_000);
                await SettleAsync(window);
                Screenshots.Capture(window, "share-content-dialog-end-" + look);

                var target = Path.Combine(Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR")!, $"share-content-sample-{look}.html");
                app.Shell.NextShare = _ => new ExportDestination("share.html", target, null);
                await share.SaveCommand.ExecuteAsync(null);
                Assert.False(share.NoticeIsError, share.Notice);
                Assert.True(File.Exists(target));
            }
        }
        finally
        {
            CurrentLanguage.Set(before);
        }
    }

    private static async Task SettleAsync(Window window)
    {
        for (var i = 0; i < 5; i++)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }

    private static async Task<ConversationId> SeedAsync(DesktopHarness app)
    {
        await app.StartWithWorkspaceAsync("Trains");
        await app.Services.GetRequiredService<IModelSettings>().SaveAsync(
            new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test", Capabilities: ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = true }), CancellationToken.None);
        await app.Services.GetRequiredService<Application.Preferences.IWebSearchSettings>().SetKeyAsync("tvly-key-1234567890", CancellationToken.None);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var resources = app.Services.GetRequiredService<IResources>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;

        async Task<(ConversationId Conversation, Message Answer)> AskAsync(ConversationId? existing, string? title, string question, string reply, params ResourceId[] attachments)
        {
            if (title is not null)
            {
                app.Client.Titles.Replies.Enqueue(title);
            }

            app.Client.Reply(reply);
            var sent = existing is { } id
                ? await conversations.AskAsync(id, question, attachments, CancellationToken.None)
                : await conversations.StartAsync(workspace, question, attachments, null, new SendOptions(WebSearchMode.Auto), CancellationToken.None);
            Assert.True(sent.Accepted, sent.Rejection?.ToString());
            await UiWait.UntilAsync(() => conversations.LoadAsync(sent.Conversation, CancellationToken.None).Result!.Messages.All(m => m.IsFinished));
            await UiWait.UntilAsync(() => conversations.LoadAsync(sent.Conversation, CancellationToken.None).Result!.Conversation.Title is not null);
            return (sent.Conversation, (await conversations.LoadAsync(sent.Conversation, CancellationToken.None))!.Messages[^1]);
        }

        async Task<ConversationId> BranchAsync(ConversationId parent, Message answer, string quote) =>
            (await conversations.CreateBranchAsync(parent, new BranchSelection(answer.Id, 0, quote, "{}", 1, "test-profile"), CancellationToken.None)).Child;

        app.Client.CallTool(WebSearchTool.ToolName, """{"query":"wagonways"}""");
        var path = Path.Combine(Path.GetTempPath(), "gauge-table.txt");
        await File.WriteAllTextAsync(path, "1435 mm");
        var file = (await resources.AttachFileAsync(DraftSlot.NewExploration(workspace), path, CancellationToken.None)).Resource!.Resource.Id;
        var (root, answer) = await AskAsync(null, "History of trains", "When did trains begin?\nI always assumed the steam engine came first.", RootAnswer, file);
        var (_, second) = await AskAsync(root, null, "So who made it practical?", "George Stephenson, more than anyone. The moment usually taken as the start of the railway age is 1829: the Rainhill trials.");
        var rainhill = await BranchAsync(root, second, "the Rainhill trials");
        await AskAsync(rainhill, "The Rainhill trials", "What happened at Rainhill?", "Five entered. **Rocket** was the only one to complete the trial.");

        app.Client.Reply("Robert Stephenson and his father George, in short.");
        Assert.True((await conversations.RetryAsync(root, second.Id, CancellationToken.None)).Accepted);
        await UiWait.UntilAsync(() => conversations.LoadAsync(root, CancellationToken.None).Result!.Messages.All(m => m.IsFinished));

        var tracks = await BranchAsync(root, answer, "Tracks are about two hundred years older than locomotives");
        var (_, rails) = await AskAsync(tracks, "Why rails before engines", "Why would anyone build rails for a horse?", "Low rolling resistance. Keeping the wheels on the rails was the hard part, and the flanged wheel is what solved it.");
        var flange = await BranchAsync(tracks, rails, "the flanged wheel");
        app.Client.CutOff("Mostly not. The wheels are slightly conical, and that is what steers: the flange only touches the rail in");
        await conversations.AskAsync(flange, "Does the flange do the steering?", CancellationToken.None);
        await UiWait.UntilAsync(() => conversations.LoadAsync(flange, CancellationToken.None).Result!.Messages.All(m => m.IsFinished));
        var gauge = await BranchAsync(root, answer, "the question of how far apart the rails should be");
        await AskAsync(gauge, "Standard gauge", "How far apart are the rails?", "On most railways **1,435 mm**.");

        app.Client.Knowledge.Replies.Enqueue("Title: How railways began\nShort name: History of trains\n\nRailways came before locomotives.\n\n- wooden wagonways\n- $F = ma$ and **steam**");
        Assert.True((await app.Services.GetRequiredService<SummaryService>().SummarizeAsync(root, CancellationToken.None)).Accepted);
        await UiWait.UntilAsync(() => app.Services.GetRequiredService<SummaryService>().ForConversationAsync(root, CancellationToken.None).Result is not null);
        return root;
    }
}
