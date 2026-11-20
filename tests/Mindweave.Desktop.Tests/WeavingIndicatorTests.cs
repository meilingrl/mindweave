using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Mindweave.Desktop.Views;

namespace Mindweave.Desktop.Tests;

/// <summary>The weaving indicator on screen: it moves only while in sight, and a change of motion glides rather than jumps.</summary>
public class WeavingIndicatorTests
{
    [AvaloniaFact]
    public async Task It_receives_frames_only_while_in_sight()
    {
        var (window, indicator) = Show();
        WeavingIndicator.Animates = true;
        try
        {
            indicator.IsVisible = false;
            indicator.IsVisible = true;
            await AssertFramesAsync(indicator, arrive: true);

            indicator.IsVisible = false;
            await AssertFramesAsync(indicator, arrive: false);
            Assert.False(indicator.IsMoving);
        }
        finally
        {
            WeavingIndicator.Animates = false;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_change_of_motion_starts_from_the_dots_as_they_were()
    {
        var (window, indicator) = Show();
        WeavingIndicator.Animates = true;
        try
        {
            using (window.CaptureRenderedFrame())
            {
            }

            var before = indicator.Drawn!.Dots;
            indicator.Motion = WeavingMotion.Thread;
            using (window.CaptureRenderedFrame())
            {
            }

            Assert.True(indicator.IsGliding);
            Assert.Equal(before, indicator.Drawn!.Dots);
            Assert.NotEqual(WeavingPaths.At(WeavingMotion.Thread, 0).Dots, indicator.Drawn.Dots);
        }
        finally
        {
            WeavingIndicator.Animates = false;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Still_it_draws_its_own_motion_at_once()
    {
        var (window, indicator) = Show();
        indicator.Motion = WeavingMotion.Joining;
        using (window.CaptureRenderedFrame())
        {
        }

        Assert.False(indicator.IsGliding);
        Assert.NotEmpty(indicator.Drawn!.Links);
        Assert.Equal(44, indicator.Bounds.Width);
        Assert.Equal(20, indicator.Bounds.Height);
        window.Close();
    }

    private static (Window Window, WeavingIndicator Indicator) Show()
    {
        var indicator = new WeavingIndicator { Foreground = Brushes.RoyalBlue, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var window = new Window { Content = new StackPanel { Children = { indicator } }, Width = 200, Height = 100 };
        window.Show();
        window.UpdateLayout();
        return (window, indicator);
    }

    private static async Task AssertFramesAsync(WeavingIndicator indicator, bool arrive)
    {
        for (var settle = 0; settle < 3; settle++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        var before = indicator.FramesReceived;
        if (arrive)
        {
            await UiWait.UntilAsync(() =>
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                return indicator.FramesReceived > before;
            });
            return;
        }

        for (var tick = 0; tick < 5; tick++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(before, indicator.FramesReceived);
    }
}
