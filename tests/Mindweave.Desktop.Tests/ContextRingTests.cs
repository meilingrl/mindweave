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
/// 0.8 and when the conversation no longer fits, and hovering (or a screen reader) gets the full context sentence.
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

    [AvaloniaFact]
    public void Hovering_shows_the_context_sentence_which_is_also_its_name()
    {
        var ring = new ContextRing();
        Assert.Equal(UiText.ContextRingName, AutomationProperties.GetName(ring));
        Assert.Null(ToolTip.GetTip(ring));

        var usage = Usage(8_200, 10_000);
        ring.Usage = usage;
        Assert.Equal(UiText.ContextUsage(usage), ToolTip.GetTip(ring));
        Assert.Equal(UiText.ContextUsage(usage), AutomationProperties.GetName(ring));
        Assert.Contains("接近上限", (string)ToolTip.GetTip(ring)!, StringComparison.Ordinal);

        ring.Usage = null;
        Assert.Equal(UiText.ContextRingName, AutomationProperties.GetName(ring));
    }

    [AvaloniaFact]
    public void Every_colour_is_defined_in_both_themes()
    {
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Assert.All(ContextRing.Keys, key => Assert.True(
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
        Assert.Equal(page.ContextUsageText, ToolTip.GetTip(ring));
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
        Assert.False(Find<ContextRing>(window, "ContextRing").IsEffectivelyVisible);
        window.Close();
    }
}
