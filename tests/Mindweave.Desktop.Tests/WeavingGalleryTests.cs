using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Mindweave.Desktop.Views;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Frames of the weaving indicator going through every stage, gliding between them, for reviewing the motion
/// (docs/design/generating-indicator.md, reasoning-and-status.md). Runs only when MINDWEAVE_SCREENSHOT_DIR is set (--filter Category=Gallery);
/// writes weaving/frame-NNN.png at 30 frames a second, three times the real size, ready to be joined into an animation.
/// </summary>
[Trait("Category", "Gallery")]
public class WeavingGalleryTests
{
    private const int FramesPerSecond = 30;

    [AvaloniaFact]
    public void G40_weaving_indicator_through_every_stage()
    {
        var target = Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        var directory = Path.Combine(target, "weaving" + (Environment.GetEnvironmentVariable("MINDWEAVE_GALLERY_THEME") is "Dark" ? "-dark" : string.Empty));
        Directory.CreateDirectory(directory);
        var theme = Environment.GetEnvironmentVariable("MINDWEAVE_GALLERY_THEME") is "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        var indicator = new WeavingIndicator { PinnedSeconds = 0 };
        indicator.Classes.Add("generating");
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        label.Classes.Add("caption");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { indicator, label } };
        var window = new Window
        {
            RequestedThemeVariant = theme,
            Width = 600,
            Height = 120,
            Content = new LayoutTransformControl
            {
                LayoutTransform = new ScaleTransform(3, 3),
                Margin = new Avalonia.Thickness(24, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = row,
            },
        };
        window.Bind(TemplatedControl.BackgroundProperty, window.GetResourceObservable("WindowBackground"));
        window.Show();

        (WeavingMotion Motion, string Label, double Seconds)[] stages =
        [
            // The stages of a run in the order they usually come (docs/design/reasoning-and-status.md §2).
            (WeavingMotion.Braid, UiText.WaitingForAnswer, 2.8),
            (WeavingMotion.Tightening, UiText.CompressingContext, 1.8),
            (WeavingMotion.Joining, UiText.ModelThinking, 4.8),
            (WeavingMotion.Reaching, UiText.AnswerStatus(Application.Conversations.AnswerStage.Tool, "web_search", "网络搜索：蒸汽机 发明"), 2.8),
            (WeavingMotion.Reaching, UiText.AnswerStatus(Application.Conversations.AnswerStage.Tool, "fetch_page", "取网页：https://zh.wikipedia.org/wiki/蒸汽机"), 2.8),
            (WeavingMotion.Holding, UiText.ApprovalWaiting, 2.4),
            (WeavingMotion.Braid, UiText.AnswerRetrying, 1.6),
            (WeavingMotion.Braid, UiText.AnswerFinishing, 1.6),
            (WeavingMotion.Thread, UiText.AnswerWriting, 2.4),
        ];

        var frame = 0;
        foreach (var (motion, text, seconds) in stages)
        {
            indicator.Motion = motion;
            label.Text = text;
            for (var i = 0; i < seconds * FramesPerSecond; i++, frame++)
            {
                indicator.PinnedSeconds = (double)frame / FramesPerSecond;
                window.UpdateLayout();
                using var bitmap = window.CaptureRenderedFrame();
                bitmap?.Save(Path.Combine(directory, $"frame-{frame:D3}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }

        window.Close();
    }
}
