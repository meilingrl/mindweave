using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Languages;
using Mindweave.Application.Models;
using Mindweave.Application.Preferences;
using Mindweave.Application.Tools;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The owner's UI points of 2026-11-23 in both themes and both languages, for looking at (MINDWEAVE_SCREENSHOT_DIR, run with
/// <c>--filter Category=Gallery</c>): folded citations, a follow-up at the top, the context card, the text width, the
/// network's details and legend, and the settings row.
/// </summary>
[Trait("Category", "Gallery")]
public class UiPointsGalleryTests
{
    public static TheoryData<string, UiLanguage> Looks => new()
    {
        { "Light", UiLanguage.Chinese },
        { "Dark", UiLanguage.Chinese },
        { "Light", UiLanguage.English },
        { "Dark", UiLanguage.English },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Looks))]
    public async Task The_points_as_they_look(string theme, UiLanguage language)
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR")), "Screenshots only.");
        CurrentLanguage.Set(language);
        try
        {
            await using var app = SessionNetworkUiTests.Gated();
            var window = app.ShowWindow(1100, 720, theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light);
            var look = $"{theme.ToLowerInvariant()}-{(language == UiLanguage.English ? "en" : "zh")}";
            var page = await AskWithSourcesAsync(app, window);
            Capture(window, $"points-1-citations-closed-{look}");
            page.Messages[1].ToggleCitationsCommand.Execute(null);
            Capture(window, $"points-1-citations-open-{look}");

            var held = app.Client.Hold("瓦特改进了蒸汽机的效率。");
            page.Composer.Text = "再讲讲瓦特？";
            await page.Composer.SendCommand.ExecuteAsync(null);
            await held.Started.Task;
            for (var i = 1; i <= 24; i++)
            {
                held.Push($"\n\n第{i}段：分离冷凝器让汽缸一直保持高温，蒸汽不再白白冷却，煤的消耗大约降到原来的四分之一，矿井、纺织厂和后来的火车都因此受益。");
            }

            await UiWait.UntilAsync(() => page.Messages[^1].Body.Contains("第24段", StringComparison.Ordinal));
            await SettleAsync(window);
            Capture(window, $"points-2-follow-up-at-top-{look}");
            held.Finish();
            await UiWait.UntilAsync(() => page.Messages[^1] is { State: MessageState.Complete, IsLive: false });
            await SettleAsync(window);

            var ring = Find<Button>(window, "ContextRingButton");
            ring.Flyout!.ShowAt(ring);
            await SettleAsync(window);
            Capture(window, $"points-5-context-parts-{look}");
            ring.Flyout.Hide();

            app.Services.GetRequiredService<TextWidth>().Pixels = 620;
            await SettleAsync(window);
            Capture(window, $"points-6-text-width-{look}");

            app.Workbench.Navigate(new NavigationRequest(MainSection.Settings));
            await SettleAsync(window);
            Capture(window, $"points-6-settings-{look}");

            app.Workbench.Navigate(new NavigationRequest(MainSection.SummaryNetwork));
            var network = Assert.IsType<SummaryNetworkHomeViewModel>(app.Workbench.CurrentPage);
            await network.Loaded;
            await UiWait.UntilAsync(() => network.Nodes.Count > 0);
            network.Select(network.Session.Projection.Nodes[0].Id);
            LegendCard.Toggle(window);
            await SettleAsync(window);
            Capture(window, $"points-3-network-{look}");
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    private static async Task<ConversationViewModel> AskWithSourcesAsync(DesktopHarness app, Window window)
    {
        await app.StartWithWorkspaceAsync("火车历史");
        var capabilities = ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = true };
        await app.Services.GetRequiredService<ModelSettingsService>().SaveAsync(
            new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test", null, capabilities), CancellationToken.None);
        await app.Services.GetRequiredService<IWebSearchSettings>().SetKeyAsync("tvly-ui-test-key", CancellationToken.None);
        app.Client.CallTool(WebSearchTool.ToolName, JsonSerializer.Serialize(new { query = "瓦特 蒸汽机" }));
        app.Client.Reply("1769 年，瓦特取得了分离冷凝器的专利[1]。第一条公共铁路在 1825 年通车[2]。");
        app.ShellPage.Conversations.NewCommand.Execute(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is NewConversationViewModel);
        var draft = (NewConversationViewModel)app.Workbench.CurrentPage!;
        await draft.Composer.WebSearch!.Loaded;
        draft.Composer.Text = "瓦特对蒸汽机做了什么？";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel { Messages.Count: 2 } p && p.Messages[1] is { State: MessageState.Complete, IsLive: false }, timeoutMs: 10_000);
        await SettleAsync(window);
        return (ConversationViewModel)app.Workbench.CurrentPage!;
    }

    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        Screenshots.Capture(window, name);
    }

    private static async Task SettleAsync(Window window)
    {
        for (var settle = 0; settle < 4; settle++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
        }

        await Task.Delay(250);
        window.UpdateLayout();
    }
}
