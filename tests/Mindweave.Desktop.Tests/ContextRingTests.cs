using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views.Conversations;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The composer's context ring (2026-11-17): the arc is the share of the model's context in use, the colour turns at
/// 0.8 and when the conversation no longer fits. Three levels (2026-11-23): the ring, the figures on hover, the parts on a press.
/// </summary>
public class ContextRingTests
{
    private static ContextUsage Usage(int tokens, int budget, bool compressed = false, bool tooLong = false) =>
        new(tokens, tooLong ? budget * 2 : tokens, budget, compressed, tooLong);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 8)]          // a little use still shows as a short arc
    [InlineData(2_500, 90)]
    [InlineData(5_000, 180)]
    [InlineData(7_500, 270)]
    [InlineData(10_000, 360)]
    [InlineData(14_000, 360)]    // never more than a full turn
    public void The_arc_is_the_fraction_of_a_full_turn(int tokens, double degrees) =>
        Assert.Equal(degrees, ContextRing.SweepOf(Usage(tokens, 10_000)), 6);

    [Fact]
    public void A_conversation_that_no_longer_fits_is_a_full_ring()
    {
        Assert.Equal(360, ContextRing.SweepOf(Usage(3_000, 10_000, tooLong: true)));
        Assert.Equal(1, new ContextUsage(0, 0, 0, false, false).Fraction);
    }

    [Theory]
    [InlineData(0, false, false, ContextRingLevel.Normal)]
    [InlineData(7_999, false, false, ContextRingLevel.Normal)]
    [InlineData(4_000, true, false, ContextRingLevel.Normal)]   // summarized and roomy again: the tooltip says so
    [InlineData(8_000, false, false, ContextRingLevel.Near)]
    [InlineData(9_900, true, false, ContextRingLevel.Near)]
    [InlineData(2_000, true, true, ContextRingLevel.TooLong)]
    public void The_colour_turns_near_the_limit_and_when_it_no_longer_fits(int tokens, bool compressed, bool tooLong, ContextRingLevel level)
    {
        Assert.Equal(level, ContextRing.LevelOf(Usage(tokens, 10_000, compressed, tooLong)));
        Assert.Equal(
            level switch { ContextRingLevel.Normal => "ContextRingNormal", ContextRingLevel.Near => "ContextRingNear", _ => "ContextRingTooLong" },
            ContextRing.BrushKeyOf(level));
    }

    [Fact]
    public void The_arc_runs_clockwise_from_the_top()
    {
        var centre = new Point(9, 9);
        Assert.Equal(new Point(9, 1), Round(ContextRing.PointOnCircle(centre, 8, 0)));
        Assert.Equal(new Point(17, 9), Round(ContextRing.PointOnCircle(centre, 8, 90)));
        Assert.Equal(new Point(9, 17), Round(ContextRing.PointOnCircle(centre, 8, 180)));
        Assert.Equal(new Point(1, 9), Round(ContextRing.PointOnCircle(centre, 8, 270)));

        static Point Round(Point p) => new(Math.Round(p.X, 6), Math.Round(p.Y, 6));
    }

    [Fact]
    public void The_figures_are_short_and_marked_as_estimates()
    {
        Assert.Equal("1.2K", UiText.CompactTokens(1_234));
        Assert.Equal("820", UiText.CompactTokens(820));
        Assert.Equal("1M", UiText.CompactTokens(999_950));
        Assert.Equal("~8.2K / 10K", UiText.ContextTotal(Usage(8_200, 10_000)));
        Assert.Equal("82%", UiText.ContextPercent(Usage(8_200, 10_000)));
        Assert.Equal("<1%", UiText.ContextPercent(Usage(20, 10_000)));
        Assert.Equal("0%", UiText.ContextPercent(Usage(0, 10_000)));
        Assert.Equal(">100%", UiText.ContextPercent(Usage(30_000, 10_000, tooLong: true)));
        Assert.Equal("上下文已用 82% · ~8.2K / 10K", UiText.ContextUsageShort(Usage(8_200, 10_000)));
        Assert.Null(UiText.ContextUsageNote(Usage(100, 10_000)));
        Assert.Contains("接近上限", UiText.ContextUsageNote(Usage(8_200, 10_000)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_bar_keeps_every_part_visible_and_never_overflows()
    {
        var usage = Usage(10_000, 10_000) with
        {
            Parts = ContextUsage.Share([.. Enum.GetValues<ContextUsageKind>().Select((k, i) => (k, i == 0 ? 100_000 : 1))], 10_000),
        };
        Assert.Equal(10_000, usage.Parts.Sum(p => p.Tokens));
        var segments = ContextUsageBar.Segments(usage, 228);
        Assert.Equal(6, segments.Count);
        Assert.All(segments, s => Assert.True(s.Width > 2));
        Assert.True(segments[^1].Start + segments[^1].Width <= 228 + 0.01);
        Assert.Empty(ContextUsageBar.Segments(Usage(0, 10_000), 228));
    }

    [AvaloniaFact]
    public void Every_colour_is_defined_in_both_themes()
    {
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Assert.All(ContextRing.Keys.Concat(Enum.GetValues<ContextUsageKind>().Select(ContextUsageBar.BrushKeyOf)), key => Assert.True(
                Avalonia.Application.Current!.Resources.TryGetResource(key, theme, out var value) && value is Avalonia.Media.IBrush, $"{key} in {theme}"));
        }
    }

    [AvaloniaFact]
    public async Task The_conversation_page_shows_the_ring_beside_send_and_the_line_only_when_it_no_longer_fits()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        app.Client.Reply("回答");
        var started = await conversations.StartAsync(app.Workbench.CurrentWorkspace!.Id, "问题", CancellationToken.None);
        await UiWait.UntilAsync(() => conversations.LoadAsync(started.Conversation, CancellationToken.None).Result is { } c && c.Messages.Count == 2 && c.Messages[^1].IsFinished);

        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(started.Conversation)));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        await UiWait.UntilAsync(() => page.ContextUsage is not null);
        window.UpdateLayout();

        var ring = Find<ContextRing>(window, "ContextRing");
        Assert.True(ring.IsEffectivelyVisible);
        Assert.Same(page.ContextUsage, ring.Usage);

        // Hover: the figures. A press: what they are made of. A screen reader: the whole sentence.
        var button = Find<Button>(window, "ContextRingButton");
        Assert.Equal(UiText.ContextUsageShort(page.ContextUsage!), ToolTip.GetTip(button));
        Assert.Equal(page.ContextUsageText, AutomationProperties.GetName(button));
        UiInput.Click(window, button);
        await UiWait.UntilAsync(() => button.Flyout!.IsOpen);
        var card = Assert.IsType<ContextUsageView>(((Flyout)button.Flyout!).Content);
        var details = Assert.IsType<ContextUsageDetails>(card.DataContext);
        Assert.Equal(page.ContextUsage!.Tokens, details.Rows.Sum(r => r.Tokens));
        Assert.Contains(details.Rows, r => r.Kind == ContextUsageKind.Conversation);
        button.Flyout!.Hide();
        Assert.Equal(ContextRingLevel.Normal, ContextRing.LevelOf(ring.Usage!));
        Assert.False(page.ShowsContextLine);
        Assert.False(Find<TextBlock>(window, "ContextUsageLine").IsEffectivelyVisible);

        // Left of send, inside the composer, and centred on the send button's height.
        var send = Find<Button>(window, "SendButton");
        var ringBox = ring.TranslatePoint(new Point(0, 0), window)!.Value;
        var sendBox = send.TranslatePoint(new Point(0, 0), window)!.Value;
        Assert.True(ringBox.X + ring.Bounds.Width < sendBox.X);
        Assert.InRange((ringBox.Y + (ring.Bounds.Height / 2)) - (sendBox.Y + (send.Bounds.Height / 2)), -2, 2);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_new_conversation_has_no_ring()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        await app.StartWithWorkspaceAsync("火车");
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        window.UpdateLayout();
        Assert.False(draft.Composer.ShowsContextRing);
        Assert.False(Find<Button>(window, "ContextRingButton").IsEffectivelyVisible);
        window.Close();
    }
}
