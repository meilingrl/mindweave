using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Exchange;
using Mindweave.Application.Languages;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Infrastructure.Share;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Importing a structure-only share, for looking at (MINDWEAVE_SCREENSHOT_DIR, run with <c>--filter FullyQualifiedName~ImportGallery</c>):
/// the menu, the dialog, the muted rows, the hollow dots in both views and an empty conversation: import-*.png, and
/// import-sample-*.html to import by hand.
/// </summary>
[Trait("Category", "Gallery")]
[Collection(Text.LanguageTests.Name)]
public class ImportGalleryTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 18, 3, 42, 0, TimeSpan.Zero);

    private static readonly (int Id, int? Parent, string English, string Chinese, int Hours)[] Path =
    [
        (1, null, "How trains came to be", "火车是怎么来的", 0),
        (2, 1, "Rails before engines", "先有轨道，后有机车", 1),
        (3, 2, "Why 1435 mm", "为什么是 1435 毫米", 2),
        (4, 2, "Horse-drawn wagonways", "马拉轨道车", 3),
        (5, 1, "The Rocket at Rainhill", "雨山竞赛上的火箭号", 4),
        (6, 5, "Multi-tube boilers", "多管锅炉", 5),
        (7, null, "High-speed rail", "高速铁路", 30),
        (8, 7, "Why rails need slab track", "为什么要用无砟轨道", 31),
    ];

    [AvaloniaTheory]
    [InlineData("Light", UiLanguage.English)]
    [InlineData("Dark", UiLanguage.English)]
    [InlineData("Light", UiLanguage.Chinese)]
    [InlineData("Dark", UiLanguage.Chinese)]
    public async Task Import_as_it_looks(string theme, UiLanguage language)
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR")), "Screenshots only.");
        var before = CurrentLanguage.Value;
        CurrentLanguage.Set(language);
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1100, 720, theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light);
            var look = $"{theme.ToLowerInvariant()}-{(language == UiLanguage.English ? "en" : "zh")}";
            var english = language == UiLanguage.English;
            await app.StartWithWorkspaceAsync(english ? "History" : "历史");
            await app.ConfigureModelAsync();
            var file = System.IO.Path.Combine(app.Paths.DataDirectory, "share.html");
            static ConversationId Id(int number) => ConversationId.Parse($"0190f000-0000-7000-8000-{number:d12}");
            var share = new ExchangeDocument(
                ExchangeDocument.StructureKind, english ? "How trains came to be" : "火车是怎么来的", Day.AddDays(7), "0.7.0",
                [.. Path.Select(c => new ExchangeConversation(Id(c.Id), c.Parent is { } p ? Id(p) : null, english ? c.English : c.Chinese, Day.AddHours(c.Hours), 2, false))], []);
            async Task OpenAsync(ExchangeDocument document)
            {
                await File.WriteAllBytesAsync(file, SharePage.Encode(document), CancellationToken.None);
                await app.Root.Import.OpenAsync(file);
                window.UpdateLayout();
            }

            // The shared file itself, to import by hand.
            await File.WriteAllBytesAsync(System.IO.Path.Combine(Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR")!, $"import-sample-{(english ? "en" : "zh")}.html"), SharePage.Encode(share), CancellationToken.None);
            app.ShellPage.IsWorkspaceMenuOpen = true;
            await Task.Delay(400, CancellationToken.None); // The menu fades in.
            Screenshots.Capture(window, $"import-1-menu-{look}");
            app.ShellPage.IsWorkspaceMenuOpen = false;
            await OpenAsync(share);
            Screenshots.Capture(window, $"import-2-dialog-{look}");
            await app.Root.Import.ImportCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == Path.Length);
            await Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage).Loaded;
            app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(Id(3))));
            await Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage).Loaded;
            window.UpdateLayout();
            Screenshots.Capture(window, $"import-3-empty-conversation-{look}");

            app.Client.Reply(english ? "Because the first lines copied the colliery wagonways." : "因为最早的线路沿用了煤矿轨道车的轨距。");
            app.Client.Titles.Replies.Enqueue(english ? "Where the gauge comes from" : "轨距的由来");
            var sent = await app.Services.GetRequiredService<ConversationService>().AskAsync(Id(3), english ? "Where does the gauge come from?" : "轨距是怎么来的？", CancellationToken.None);
            app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(sent.Conversation)));
            var own = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
            await own.Loaded;
            await UiWait.UntilAsync(() => own.Messages.Count == 2 && own.RunningAnswer is null && own.Title == (english ? "Where the gauge comes from" : "轨距的由来"));
            await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Any(i => i.Id == sent.Conversation && i.Title == own.Title));
            window.UpdateLayout();
            Screenshots.Capture(window, $"import-4-own-branch-{look}");

            var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
            network.Select(network.Nodes.Single(n => n.DisplayName == (english ? "Why 1435 mm" : "为什么是 1435 毫米")).Id);
            foreach (var (mode, name) in new[] { (SummaryNetworkViewMode.Timeline, "5-timeline"), (SummaryNetworkViewMode.Spatial, "6-spatial") })
            {
                network.SetViewModeCommand.Execute(mode);
                window.UpdateLayout();
                Screenshots.Capture(window, $"import-{name}-{look}");
            }

            await OpenAsync(share);
            Screenshots.Capture(window, $"import-7-all-here-{look}");
            app.Root.Import.CloseCommand.Execute(null);
            await OpenAsync(share with { Conversations = [.. share.Conversations, new(Id(20), Id(1), english ? "Cable haulage" : "缆索牵引", Day.AddHours(40), 1, false)] });
            Screenshots.Capture(window, $"import-8-newer-{look}");
            app.Root.Import.CloseCommand.Execute(null);
            await File.WriteAllTextAsync(file, "<!doctype html>", CancellationToken.None);
            await app.Root.Import.OpenAsync(file);
            window.UpdateLayout();
            Screenshots.Capture(window, $"import-9-refused-{look}");
            app.Root.Import.CloseCommand.Execute(null);
        }
        finally
        {
            CurrentLanguage.Set(before);
        }
    }
}
