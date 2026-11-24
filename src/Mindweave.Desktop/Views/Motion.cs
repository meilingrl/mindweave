using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

namespace Mindweave.Desktop.Views;

/// <summary>How a panel, card or prompt arrives when it is shown (2026-11-10 polish).</summary>
public enum AppearMotion
{
    /// <summary>No motion.</summary>
    None,

    /// <summary>Opacity only: a page, a status line.</summary>
    Fade,

    /// <summary>Fades in while rising a few pixels: bars and notes at the bottom of an area.</summary>
    Rise,

    /// <summary>Fades in while dropping a few pixels: notices under a toolbar, the composer's notice.</summary>
    Drop,

    /// <summary>A window prompt: the scrim fades, the dialog inside rises a little and grows from 98 %.</summary>
    Dialog,

    /// <summary>A page replacing another in the content area: fades in while rising 4 px.</summary>
    Page,

    /// <summary>The sidebar coming back: fades in while sliding a few pixels from the left.</summary>
    SlideIn,

    /// <summary>A floating card (new-answer hints), each time it is put on screen: rises from a little lower.</summary>
    Toast,

    /// <summary>A menu or suggestion list, each time it is put on screen: fades in while dropping a few pixels.</summary>
    Popup,
}

/// <summary>
/// The app's small motion system (s7-ui-polish.md, 2026-11-10): hover and press colours fade over 120 ms
/// (the transitions live in Styles/Motion.axaml), panels and cards appear over <see cref="Base"/>, prompts over
/// <see cref="Slow"/>, all easing out. Disappearing is instant, and nothing waits for motion: input goes through at once.
/// With <see cref="Enabled"/> off (headless tests and screenshots) Motion.axaml is not loaded and nothing animates, so
/// every captured frame shows a settled state.
/// </summary>
public static class Motion
{
    /// <summary>Panels, bars, notices, cards, menus and the page appearing.</summary>
    public static readonly TimeSpan Base = TimeSpan.FromMilliseconds(160);

    /// <summary>Window prompts appearing over a scrim.</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(200);

    /// <summary>Where the transitions for hover, press and selection are; loaded by <see cref="App"/> only while <see cref="Enabled"/>.</summary>
    public static readonly Uri StylesUri = new("avares://Mindweave/Styles/Motion.axaml");

    /// <summary>How the element appears when it becomes visible (or, for <see cref="AppearMotion.Toast"/> and
    /// <see cref="AppearMotion.Popup"/>, each time it is put on screen).</summary>
    public static readonly AttachedProperty<AppearMotion> AppearProperty =
        AvaloniaProperty.RegisterAttached<Visual, AppearMotion>("Appear", typeof(Motion));

    private static readonly ConditionalWeakTable<Visual, CancellationTokenSource> Running = new();
    private static readonly Easing EaseOut = new CubicEaseOut();

    static Motion()
    {
        AppearProperty.Changed.AddClassHandler<Visual>(OnAppearChanged);
    }

    /// <summary>On in the app; the headless test app turns it off before the application starts.</summary>
    public static bool Enabled { get; set; } = true;

    public static AppearMotion GetAppear(Visual element) => element.GetValue(AppearProperty);

    public static void SetAppear(Visual element, AppearMotion value) => element.SetValue(AppearProperty, value);

    /// <summary>The hover, press and selection transitions, as the app loads them.</summary>
    public static IStyle LoadStyles() => new StyleInclude(new Uri("avares://Mindweave/")) { Source = StylesUri };

    /// <summary>
    /// Plays <paramref name="motion"/> on <paramref name="element"/> now, whether or not <see cref="Enabled"/> is on;
    /// a second call on the same element replaces the first. Completes when the motion ends or is replaced.
    /// </summary>
    internal static Task Play(Visual element, AppearMotion motion)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (Running.TryGetValue(element, out var previous))
        {
            previous.Cancel();
            Running.Remove(element);
        }

        if (motion == AppearMotion.None)
        {
            return Task.CompletedTask;
        }

        var cancel = new CancellationTokenSource();
        Running.Add(element, cancel);
        var tasks = new List<Task>(2);
        switch (motion)
        {
            case AppearMotion.Fade:
                tasks.Add(Run(element, Base, cancel.Token, (Visual.OpacityProperty, 0.0, 1.0)));
                break;
            case AppearMotion.Rise:
                tasks.Add(Run(element, Base, cancel.Token, (Visual.OpacityProperty, 0.0, 1.0), (TranslateTransform.YProperty, 6.0, 0.0)));
                break;
            case AppearMotion.Page:
                tasks.Add(Run(element, Base, cancel.Token, (Visual.OpacityProperty, 0.0, 1.0), (TranslateTransform.YProperty, 4.0, 0.0)));
                break;
            case AppearMotion.Toast:
                tasks.Add(Run(element, Base, cancel.Token, (Visual.OpacityProperty, 0.0, 1.0), (TranslateTransform.YProperty, 10.0, 0.0)));
                break;
            case AppearMotion.Drop:
            case AppearMotion.Popup:
                tasks.Add(Run(element, Base, cancel.Token, (Visual.OpacityProperty, 0.0, 1.0), (TranslateTransform.YProperty, -4.0, 0.0)));
                break;
            case AppearMotion.SlideIn:
                tasks.Add(Run(element, Base, cancel.Token, (Visual.OpacityProperty, 0.0, 1.0), (TranslateTransform.XProperty, -10.0, 0.0)));
                break;
            case AppearMotion.Dialog:
                tasks.Add(Run(element, Base, cancel.Token, (Visual.OpacityProperty, 0.0, 1.0)));
                if (element is Decorator { Child: Visual dialog })
                {
                    tasks.Add(Run(dialog, Slow, cancel.Token,
                        (TranslateTransform.YProperty, 8.0, 0.0), (ScaleTransform.ScaleXProperty, 0.98, 1.0), (ScaleTransform.ScaleYProperty, 0.98, 1.0)));
                }

                break;
        }

        return Finish(element, cancel, Task.WhenAll(tasks));
    }

    private static async Task Finish(Visual element, CancellationTokenSource cancel, Task running)
    {
        try
        {
            await running.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer motion on the same element.
        }
        finally
        {
            if (Running.TryGetValue(element, out var current) && ReferenceEquals(current, cancel))
            {
                Running.Remove(element);
            }

            cancel.Dispose();
        }
    }

    private static Task Run(Visual element, TimeSpan duration, CancellationToken cancel, params (AvaloniaProperty Property, double From, double To)[] values)
    {
        var start = new KeyFrame { Cue = new Cue(0) };
        var end = new KeyFrame { Cue = new Cue(1) };
        foreach (var (property, from, to) in values)
        {
            start.Setters.Add(new Setter(property, from));
            end.Setters.Add(new Setter(property, to));
        }

        // FillMode.None: when it ends the element is back on its own values (bindings, styles), never held by the animation.
        var animation = new Animation { Duration = duration, Easing = EaseOut, FillMode = FillMode.None, Children = { start, end } };
        return animation.RunAsync(element, cancel);
    }

    private static void OnAppearChanged(Visual element, AvaloniaPropertyChangedEventArgs e)
    {
        element.PropertyChanged -= OnElementPropertyChanged;
        element.AttachedToVisualTree -= OnElementAttached;
        if (e.NewValue is AppearMotion.None or null)
        {
            return;
        }

        element.PropertyChanged += OnElementPropertyChanged;
        element.AttachedToVisualTree += OnElementAttached;
    }

    private static void OnElementPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty && e.NewValue is true && sender is Visual element && TopLevel.GetTopLevel(element) is not null)
        {
            PlayIfEnabled(element);
        }
    }

    private static void OnElementAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Visual { IsVisible: true } element && GetAppear(element) is AppearMotion.Toast or AppearMotion.Popup)
        {
            PlayIfEnabled(element);
        }
    }

    private static void PlayIfEnabled(Visual element)
    {
        if (Enabled)
        {
            _ = Play(element, GetAppear(element));
        }
    }
}
