using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Preferences;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The text's width (the owner, 2026-11-23): chosen in 设置 › 外观 or dragged at the text's edge on a conversation page; the
/// page keeps its width, and the sentence being read stays where it is.
/// </summary>
public class TextWidthTests
{
    private static readonly string LongAnswer = string.Join("\n\n", Enumerable.Range(1, 30)
        .Select(i => $"第{i}段：铁路的出现改变了城市之间的距离，也改变了人们对时间的理解；这一段写得足够长，好让它在不同的宽度下折成不同的行数，从而检验读到的句子是否留在原处。"));

    public static TheoryData<string> Themes => new() { "Light", "Dark" };

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task Dragging_the_edge_changes_the_text_and_not_the_page_and_is_remembered(string theme)
    {
        await using var app = DesktopHarness.Create();
        var (window, page) = await OpenAsync(app, theme, whole: true);
        var view = CurrentView(window);
        var list = Find<ItemsControl>(view, "MessageList");
        var left = Find<EdgeGrip>(view, "LeftTextGrip");
        var pageWidth = view.Bounds.Width;
        var whole = list.Bounds.Width;
        Assert.False(Find<EdgeGrip>(view, "RightTextGrip").IsVisible);
        Screenshots.Capture(window, $"text-width-whole-{theme.ToLowerInvariant()}");

        // Both edges move: the column is centred, and the edge dragged stays under the pointer.
        var from = left.TranslatePoint(new Point(4, 200), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Vector(60, 0));
        window.UpdateLayout();
        Assert.Equal(whole - 120, list.Bounds.Width, 1);
        Assert.Equal(pageWidth, view.Bounds.Width);
        window.MouseUp(from + new Vector(60, 0), MouseButton.Left);
        Screenshots.Capture(window, $"text-width-dragged-{theme.ToLowerInvariant()}");

        // The input goes with the text; the right edge can be dragged too now.
        var composer = Find<StackPanel>(view, "ComposerColumn");
        Assert.Equal(list.TranslatePoint(default, view)!.Value.X, composer.TranslatePoint(default, view)!.Value.X, 1);
        Assert.Equal(list.Bounds.Width, composer.Bounds.Width, 1);
        var right = Find<EdgeGrip>(view, "RightTextGrip");
        Assert.True(right.IsVisible);
        from = right.TranslatePoint(new Point(4, 200), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from - new Vector(40, 0));
        window.MouseUp(from - new Vector(40, 0), MouseButton.Left);
        window.UpdateLayout();
        Assert.Equal(whole - 200, list.Bounds.Width, 1);

        var text = app.Services.GetRequiredService<TextWidth>();
        await text.Saving;
        Assert.Equal(whole - 200, (await app.Services.GetRequiredService<ITextWidth>().GetAsync(CancellationToken.None))!.Value, 1);

        // The keyboard moves it, Home gives the default back; never narrower than the least.
        Assert.Equal(UiText.TextWidthLabel, AutomationProperties.GetName(right));
        Assert.True(right.Focus());
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        window.UpdateLayout();
        Assert.Equal(whole - 200 - (2 * EdgeGrip.KeyStep), list.Bounds.Width, 1);
        for (var i = 0; i < 40; i++)
        {
            window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        }

        window.UpdateLayout();
        Assert.Equal(TextWidthService.Min, list.Bounds.Width, 1);
        window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
        await text.Saving;
        Assert.Equal(TextWidthService.Default, await app.Services.GetRequiredService<ITextWidth>().GetAsync(CancellationToken.None));

        // Dragged out to the page's width, it is the whole page, and stays it after a restart.
        text.Pixels = 600;
        window.UpdateLayout();
        from = left.TranslatePoint(new Point(4, 200), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from - new Vector(400, 0));
        window.MouseUp(from - new Vector(400, 0), MouseButton.Left);
        window.UpdateLayout();
        Assert.Equal(whole, list.Bounds.Width, 1);
        await text.Saving;
        Assert.Null(await app.Services.GetRequiredService<ITextWidth>().GetAsync(CancellationToken.None));
        Assert.NotNull(page);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_sentence_being_read_stays_while_the_text_is_rewrapped()
    {
        await using var app = DesktopHarness.Create();
        var (window, _) = await OpenAsync(app, "Light");
        var scroller = Scroller(window);
        scroller.Offset = new Vector(0, 700);
        await SettleAsync(window);
        await Task.Delay(300); // The reading position is captured once scrolling has been still for a moment.
        await SettleAsync(window);
        var before = LineAtTop(window);

        app.Services.GetRequiredService<TextWidth>().Pixels = 480;
        await SettleAsync(window);
        Assert.Equal(before, LineAtTop(window));
        app.Services.GetRequiredService<TextWidth>().Pixels = null;
        await SettleAsync(window);
        Assert.Equal(before, LineAtTop(window));
        Assert.InRange(scroller.Offset.Y, 674, 726);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_reader_who_scrolls_while_the_text_is_rewrapped_is_not_pulled_back()
    {
        await using var app = DesktopHarness.Create();
        var (window, _) = await OpenAsync(app, "Light");
        var scroller = Scroller(window);
        app.Services.GetRequiredService<TextWidth>().Pixels = 480;
        window.UpdateLayout();
        scroller.Offset = new Vector(0, 700);
        await SettleAsync(window);
        Assert.Equal(700, scroller.Offset.Y, 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task On_a_wide_window_the_text_is_a_centred_column_unless_the_whole_page_is_chosen()
    {
        await using var app = DesktopHarness.Create();
        var (window, _) = await OpenAsync(app, "Light");
        window.Width = 1600;
        await SettleAsync(window);
        var view = CurrentView(window);
        var list = Find<ItemsControl>(view, "MessageList");
        Assert.Equal(TextWidthService.Default, list.Bounds.Width, 1);
        Assert.Equal((view.Bounds.Width - TextWidthService.Default) / 2, list.TranslatePoint(default, view)!.Value.X, 1);
        Screenshots.Capture(window, "text-width-wide-window");
        app.Services.GetRequiredService<TextWidth>().Pixels = null;
        await SettleAsync(window);
        Assert.True(list.Bounds.Width > 1_200);
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task A_summary_follows_the_width_and_its_edge_is_dragged_too(string theme)
    {
        await using var app = DesktopHarness.Create();
        var (window, page) = await OpenAsync(app, theme);
        window.Width = 1600;
        app.Client.Knowledge.Replies.Enqueue("标题：铁路\n\n" + LongAnswer);
        await page.Summary.SummarizeCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.Summary.HasSummary && page.Summary.RunningSummary is null);
        page.Summary.OpenSummaryCommand.Execute(null);
        var summary = Assert.IsType<SummaryViewModel>(app.Workbench.CurrentPage);
        await summary.Loaded;
        await SettleAsync(window);
        var view = window.GetVisualDescendants().OfType<Mindweave.Desktop.Views.Pages.SummaryView>().Single();
        var body = Find<ContentControl>(view, "BodyHost");
        var header = Find<StackPanel>(view, "Header");
        Assert.InRange(body.Bounds.Width, TextWidthService.Default - 1, TextWidthService.Default + 1);
        Assert.InRange(header.Bounds.Width, TextWidthService.Default - 1, TextWidthService.Default + 1);
        Screenshots.Capture(window, $"text-width-summary-{theme.ToLowerInvariant()}");

        // Left-aligned: the edge dragged follows the pointer, and the text only.
        var grip = Find<EdgeGrip>(view, "TextGrip");
        var from = grip.TranslatePoint(new Point(4, 60), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from - new Vector(150, 0));
        window.MouseUp(from - new Vector(150, 0), MouseButton.Left);
        await SettleAsync(window);
        Assert.InRange(body.Bounds.Width, TextWidthService.Default - 151, TextWidthService.Default - 149);
        Assert.Equal(0, body.TranslatePoint(default, Find<Grid>(view, "TextColumnArea"))!.Value.X, 1);
        Assert.Equal(TextWidthService.Default - 150, app.Services.GetRequiredService<TextWidth>().Pixels!.Value, 1);

        // The whole page: the grip stays reachable.
        app.Services.GetRequiredService<TextWidth>().Pixels = null;
        await SettleAsync(window);
        Assert.True(body.Bounds.Width > 1_100);
        Assert.True(grip.IsEffectivelyVisible);
        Assert.True(grip.TranslatePoint(new Point(grip.Bounds.Width, 0), view)!.Value.X <= view.Bounds.Width);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Settings_offers_the_widths_and_names_a_dragged_one()
    {
        await using var app = DesktopHarness.Create();
        var (window, _) = await OpenAsync(app, "Light");
        var text = app.Services.GetRequiredService<TextWidth>();
        app.Workbench.Navigate(new NavigationRequest(MainSection.Settings));
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        window.UpdateLayout();
        Assert.True(settings.IsTextMedium);
        Assert.Equal(UiText.TextWidthHint, settings.TextWidthHint);
        Screenshots.Capture(window, "text-width-settings");

        UiInput.Click(window, Find<RadioButton>(window, "TextWidthNarrow"));
        Assert.Equal(SettingsHomeViewModel.NarrowText, text.Pixels);
        Assert.True(settings.IsTextNarrow);
        Assert.False(settings.IsTextMedium);
        await text.Saving;
        Assert.Equal(SettingsHomeViewModel.NarrowText, await app.Services.GetRequiredService<ITextWidth>().GetAsync(CancellationToken.None));

        text.Pixels = 731;
        Assert.Contains("731", settings.TextWidthHint, StringComparison.Ordinal);
        Assert.False(settings.IsTextWhole || settings.IsTextWide || settings.IsTextMedium || settings.IsTextNarrow);
        window.Close();
    }

    [Fact]
    public void No_or_a_bad_stored_width_means_the_default_and_others_are_kept_in_bounds()
    {
        Assert.Equal(TextWidthService.Default, TextWidthService.Parse(null));
        Assert.Equal(TextWidthService.Default, TextWidthService.Parse("wide"));
        Assert.Equal(TextWidthService.Default, TextWidthService.Parse("NaN"));
        Assert.Null(TextWidthService.Parse(TextWidthService.Whole));
        Assert.Equal(TextWidthService.Min, TextWidthService.Parse("3"));
        Assert.Equal(TextWidthService.Max, TextWidthService.Parse("99999"));
        Assert.Equal(812.5, TextWidthService.Parse("812.5"));
    }

    private static async Task<(Window Window, ConversationViewModel Page)> OpenAsync(DesktopHarness app, string theme, bool whole = false)
    {
        var window = app.ShowWindow(1100, 720, theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light);
        app.Client.Reply(LongAnswer);
        var page = await app.StartWithConversationAsync(waitForTitle: true);
        if (whole)
        {
            app.Services.GetRequiredService<TextWidth>().Pixels = null;
        }

        await CurrentView(window).Restored;
        await UiWait.UntilAsync(() => page.Messages[^1] is { State: MessageState.Complete, IsLive: false });
        await UiWait.AnswersRenderedAsync(window);
        await SettleAsync(window);
        return (window, page);
    }

    /// <summary>The paragraph at the reading line, by its first characters.</summary>
    private static string LineAtTop(Window window)
    {
        var scroller = Scroller(window);
        var blocks = window.GetVisualDescendants().OfType<AnswerView>().Last(a => a.IsRendered && a.DataContext is MessageItemViewModel { IsAnswer: true })
            .GetVisualDescendants().OfType<LiveMarkdown.Avalonia.MarkdownTextBlock>();
        return blocks.First(b => b.TranslatePoint(new Point(0, b.Bounds.Height), scroller)!.Value.Y > 8).Inlines!.Text![..4];
    }

    private static async Task SettleAsync(Window window)
    {
        for (var settle = 0; settle < 4; settle++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }
}
