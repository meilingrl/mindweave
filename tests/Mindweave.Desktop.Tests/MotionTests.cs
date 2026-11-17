using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views;
using Mindweave.Testing;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// 2026-11-10 polish (s7-ui-polish.md): the app's motion system — hover, press and selection colours fade, panels and
/// prompts appear with a short fade and a few pixels of movement — is on in the app and off in headless tests and
/// screenshots; the keyboard focus ring, the pressed states, the shared corner radius and a composer that keeps its width.
/// </summary>
public class MotionTests
{
    /// <summary>Tests that turn motion on do it without awaiting in between, so no other test runs while it is on.</summary>
    private static T WithMotion<T>(Func<T> body)
    {
        Motion.Enabled = true;
        try
        {
            return body();
        }
        finally
        {
            Motion.Enabled = false;
        }
    }

    [AvaloniaFact]
    public void The_app_loads_the_transitions_only_while_motion_is_on()
    {
        static bool HasMotion(Avalonia.Application app) =>
            app.Styles.OfType<StyleInclude>().Any(s => s.Source == Motion.StylesUri);

        // The headless application runs without them, so captured frames are settled states.
        Assert.False(Motion.Enabled);
        Assert.False(HasMotion(Avalonia.Application.Current!));
        var off = new App();
        off.Initialize();
        Assert.False(HasMotion(off));

        // The real app (motion on, the default) loads them after its own styles.
        var on = WithMotion(() =>
        {
            var app = new App();
            app.Initialize();
            return app;
        });
        Assert.True(HasMotion(on));
        Assert.Same(on.Styles.OfType<StyleInclude>().Last(), on.Styles.Last());
    }

    [AvaloniaFact]
    public void Hover_press_and_selection_colours_switch_at_once_and_scroll_bars_fade()
    {
        Button Make(string classes, string? name = null)
        {
            var button = new Button { Content = "按钮", Name = name };
            button.Classes.AddRange(classes.Split(' '));
            return button;
        }

        var buttons = new[] { Make("nav"), Make("quiet"), Make("quiet small"), Make("primary"), Make("secondary"), Make("entry"), Make("crumb"), Make("list-name"), Make("toast-body") };
        var segment = new RadioButton { Content = "浅色", Classes = { "segment" } };
        var scroller = new ScrollViewer { Height = 60, Classes = { "autohide" }, Content = new Border { Height = 400 } };
        var panel = new StackPanel();
        panel.Children.AddRange(buttons);
        panel.Children.AddRange([segment, scroller]);

        // Without Motion.axaml (as in every other test): nothing is animated.
        var plain = new Window { Width = 400, Height = 700, Content = panel };
        plain.Show();
        plain.UpdateLayout();
        Assert.All(buttons, b => Assert.Null(BrushTransition(Presenter(b), ContentPresenter.BackgroundProperty)));
        plain.Content = null;
        plain.Close();

        // With it, as the app loads it: hover, press and selection colours still switch at once (a fade from
        // Transparent flashed pale grey on the way in and out, user feedback 2026-11-10); auto-hiding scroll bars fade.
        var window = new Window { Width = 400, Height = 700 };
        window.Styles.Add(Motion.LoadStyles());
        window.Content = panel;
        window.Show();
        window.UpdateLayout();
        foreach (var button in buttons)
        {
            foreach (var property in new AvaloniaProperty[] { ContentPresenter.BackgroundProperty, ContentPresenter.ForegroundProperty, ContentPresenter.BorderBrushProperty })
            {
                Assert.True(BrushTransition(Presenter(button), property) is null, $"{string.Join(' ', button.Classes)}: {property.Name}");
            }
        }

        var segmentBorder = segment.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Segment");
        Assert.Null(BrushTransition(segmentBorder, Border.BackgroundProperty));
        var bar = scroller.GetVisualDescendants().OfType<ScrollBar>().First(b => b.Name == "PART_VerticalScrollBar");
        Assert.Contains(bar.Transitions!, t => t is DoubleTransition { Property.Name: "Opacity" });
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_panel_that_is_shown_fades_and_rises_into_place_only_while_motion_is_on()
    {
        var note = new Border { Width = 120, Height = 40, Background = Brushes.Gray, IsVisible = false };
        Motion.SetAppear(note, AppearMotion.Rise);
        var window = new Window { Width = 300, Height = 200, Content = new Panel { Children = { note } } };
        window.Show();
        window.UpdateLayout();

        // Off (tests, screenshots): shown at once, fully opaque, in place.
        note.IsVisible = true;
        FirstFrames();
        Assert.Equal(1, note.Opacity);
        Assert.Equal(0, OffsetY(note));
        note.IsVisible = false;

        // On: the first frame is still faint and low (it starts transparent, 6 px down); then it settles in place.
        var (opacity, offset) = WithMotion(() =>
        {
            note.IsVisible = true;
            FirstFrames();
            return (note.Opacity, OffsetY(note));
        });
        Assert.True(opacity < 0.95, $"opacity {opacity}");
        Assert.True(offset > 0.3, $"offset {offset}");
        await SettledAsync(() => note.Opacity == 1 && OffsetY(note) == 0);

        // Hiding is instant: nothing waits for motion.
        note.IsVisible = false;
        Assert.False(note.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_prompt_fades_its_scrim_and_grows_its_dialog_and_a_toast_rises_when_put_on_screen()
    {
        var dialog = new Border { Width = 200, Height = 100, Background = Brushes.White };
        var scrim = new Border { IsVisible = false, Child = dialog, Classes = { "dialog-scrim" } };
        var toasts = new StackPanel();
        var window = new Window { Width = 400, Height = 300, Content = new Panel { Children = { scrim, toasts } } };
        window.Show();
        window.UpdateLayout();
        // The style gives every window prompt the dialog motion.
        Assert.Equal(AppearMotion.Dialog, Motion.GetAppear(scrim));

        var toast = new Border { Width = 100, Height = 30, Classes = { "news-toast" } };
        var (scrimOpacity, scale, toastOffset) = WithMotion(() =>
        {
            scrim.IsVisible = true;
            toasts.Children.Add(toast);
            FirstFrames();
            return (scrim.Opacity, dialog.RenderTransform?.Value.M11 ?? 1, OffsetY(toast));
        });
        Assert.True(scrimOpacity < 0.95, $"scrim {scrimOpacity}");
        Assert.True(scale < 1, $"scale {scale}");
        Assert.True(toastOffset > 0.5, $"toast {toastOffset}");
        await SettledAsync(() => scrim.Opacity == 1 && (dialog.RenderTransform?.Value.M11 ?? 1) == 1 && OffsetY(toast) == 0);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_new_page_fades_in_while_motion_is_on_and_is_shown_at_once_otherwise()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        window.UpdateLayout();
        var host = Find<ContentControl>(window, "PageHost");

        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        FirstFrames();
        Assert.Equal(1, host.Opacity);

        var opacity = WithMotion(() =>
        {
            app.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
            FirstFrames();
            return host.Opacity;
        });
        Assert.True(opacity < 0.95, $"opacity {opacity}");
        await SettledAsync(() => host.Opacity == 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Keyboard_focus_draws_one_ring_inside_rows_and_outside_filled_buttons()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        window.UpdateLayout();
        var ring = Colour(window, "FocusRing");

        var row = Find<Button>(window, "NavExplore");
        row.Focus(NavigationMethod.Tab);
        window.UpdateLayout();
        var inside = FocusAdorner(row);
        Assert.Equal(ring, Assert.IsAssignableFrom<ISolidColorBrush>(inside.BorderBrush).Color);
        Assert.Equal(new Thickness(2), inside.BorderThickness);
        Assert.Equal(default, inside.Margin);
        Assert.Equal(new CornerRadius(6), inside.CornerRadius);

        app.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
        window.UpdateLayout();
        var primary = Find<Button>(window, "StartConversation");
        primary.Focus(NavigationMethod.Tab);
        window.UpdateLayout();
        var outside = FocusAdorner(primary);
        Assert.Equal(ring, Assert.IsAssignableFrom<ISolidColorBrush>(outside.BorderBrush).Color);
        Assert.Equal(new Thickness(-3), outside.Margin);
        // Drawn past the button's own edge, so not clipped to it.
        Assert.False(AdornerLayer.GetIsClipEnabled(outside));

        // The ring stands out on both themes' surfaces (non-text contrast ≥ 3:1).
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Assert.True(Contrast(ThemeColour("FocusRing", theme), ThemeColour("WindowBackground", theme)) >= 3, theme.ToString());
            Assert.True(Contrast(ThemeColour("FocusRing", theme), ThemeColour("SidebarBackground", theme)) >= 3, theme.ToString());
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task Buttons_share_one_corner_and_show_a_pressed_state_stronger_than_hover()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        window.UpdateLayout();

        Assert.Equal(new CornerRadius(6), Find<Button>(window, "SaveModel").CornerRadius);
        Assert.Equal(new CornerRadius(6), Find<Button>(window, "TestModel").CornerRadius);
        Assert.Equal(new CornerRadius(6), Find<TextBox>(window, "BaseUrl").CornerRadius);
        Assert.Equal(new CornerRadius(6), Find<ComboBox>(window, "ProviderPicker").CornerRadius);
        var toggle = Find<Button>(window, "CollapseSidebar");
        Assert.Equal(new CornerRadius(6), toggle.CornerRadius);

        // Pressed is one step stronger than hover, on the quiet and the outlined level alike.
        var hover = Colour(window, "HoverRow");
        var pressed = Colour(window, "SelectedRow");
        foreach (var button in new[] { toggle, Find<Button>(window, "TestModel") })
        {
            var pseudo = (IPseudoClasses)button.Classes;
            pseudo.Set(":pointerover", true);
            window.UpdateLayout();
            Assert.Equal(hover, Background(button));
            pseudo.Set(":pressed", true);
            window.UpdateLayout();
            Assert.Equal(pressed, Background(button));
            pseudo.Set(":pressed", false);
            pseudo.Set(":pointerover", false);
        }

        // Settings and the other long pages hide their scroll bar until pointed at, as the answers and the sidebar do.
        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.FindAncestorOfType<Views.Pages.SettingsHomeView>() is not null);
        Assert.Contains("autohide", scroller.Classes);
        window.Close();
    }

    /// <summary>
    /// Review (2026-11-10): a mouse press is always also a hover, and a hover style declared after the pressed style
    /// (breadcrumbs, the new-answer card) used to win, so pressing looked exactly like hovering on those levels.
    /// </summary>
    [AvaloniaFact]
    public void Pressing_under_the_pointer_is_stronger_than_hover_on_every_level_that_has_a_pressed_state()
    {
        var levels = new[] { "quiet", "quiet small", "secondary", "crumb", "list-name", "toast-body", "entry" };
        var buttons = levels.Select(classes =>
        {
            var button = new Button { Content = "按钮" };
            button.Classes.AddRange(classes.Split(' '));
            return button;
        }).ToArray();
        var panel = new StackPanel();
        panel.Children.AddRange(buttons);
        var window = new Window { Width = 300, Height = 400, Content = panel };
        window.Show();
        window.UpdateLayout();
        var pressed = Colour(window, "SelectedRow");

        foreach (var button in buttons)
        {
            var pseudo = (IPseudoClasses)button.Classes;
            pseudo.Set(":pointerover", true);
            pseudo.Set(":pressed", true);
            window.UpdateLayout();
            Assert.True(pressed == Background(button), string.Join(' ', button.Classes));
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_question_box_keeps_its_width_when_stop_replaces_send_and_hanging_buttons_are_not_cut()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var held = app.Client.Hold("开头");
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = "问";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        // The context ring arrives with the page's context usage, read in the background: measured once it is there, so
        // only stop and send differ between the two measurements.
        await UiWait.UntilAsync(() => page.Composer.IsAnswerRunning && page.Composer.ShowsContextRing);
        window.UpdateLayout();
        var stop = Find<Button>(window, "StopButton");
        Assert.True(stop.IsEffectivelyVisible);
        var running = Find<TextBox>(window, "Input").Bounds.Width;
        // Stop sits at the slot's right edge, where send's right edge is.
        var slot = Find<Panel>(window, "SendSlot");
        Assert.Equal(slot.Bounds.Width, stop.Bounds.Right, 0.5);

        held.Finish("。");
        await UiWait.UntilAsync(() => !page.Composer.IsAnswerRunning);
        window.UpdateLayout();
        var send = Find<Button>(window, "SendButton");
        Assert.True(send.IsEffectivelyVisible);
        Assert.Equal(running, Find<TextBox>(window, "Input").Bounds.Width);
        Assert.Equal(slot.Bounds.Width, send.Bounds.Width, 0.5);

        // The toolbar's first button and an answer's copy hang left of the text edge; nothing cuts their tint or ring.
        await UiWait.UntilAsync(() => { window.UpdateLayout(); return window.GetVisualDescendants().OfType<Control>().Any(c => c.Name == "AnswerCopy" && c.IsEffectivelyVisible); });
        var copy = window.GetVisualDescendants().OfType<Control>().Last(c => c.Name == "AnswerCopy" && c.IsEffectivelyVisible);
        foreach (var hanging in new Control[] { Find<Button>(window, "RenameButton"), copy })
        {
            var text = Find<ContentControl>(window, "PageHost").TranslatePoint(default, window)!.Value.X;
            Assert.True(hanging.TranslatePoint(default, window)!.Value.X < text, hanging.Name);
            AssertNotClipped(hanging, window);
        }

        window.Close();
    }

    /// <summary>No clipping ancestor cuts any part of <paramref name="control"/>'s own bounds.</summary>
    private static void AssertNotClipped(Control control, Window window)
    {
        foreach (var ancestor in control.GetVisualAncestors().OfType<Visual>().TakeWhile(v => v != window))
        {
            if (!ancestor.ClipToBounds)
            {
                continue;
            }

            var topLeft = control.TranslatePoint(default, ancestor)!.Value;
            var area = new Rect(topLeft, control.Bounds.Size);
            Assert.True(new Rect(ancestor.Bounds.Size).Contains(area), $"{control.Name} cut by {ancestor.GetType().Name} {(ancestor as Control)?.Name}");
        }
    }

    private static ContentPresenter Presenter(Button button) =>
        button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");

    private static TransitionBase? BrushTransition(Animatable target, AvaloniaProperty property) =>
        target.Transitions?.OfType<TransitionBase>().FirstOrDefault(t => t is Avalonia.Animation.BrushTransition && t.Property == property);

    private static Color Background(Button button) => Assert.IsAssignableFrom<ISolidColorBrush>(Presenter(button).Background).Color;

    private static double OffsetY(Visual visual) => visual.RenderTransform?.Value.M32 ?? 0;

    private static Border FocusAdorner(Control control)
    {
        var layer = Assert.IsType<AdornerLayer>(AdornerLayer.GetAdornerLayer(control));
        return Assert.Single(layer.Children.OfType<Border>(), b => ReferenceEquals(AdornerLayer.GetAdornedElement(b), control));
    }

    /// <summary>The first frames of a motion just started: the clock starts on one, the values are applied on the next.</summary>
    private static void FirstFrames()
    {
        for (var i = 0; i < 2; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Delivers frames (motion runs in real time) until <paramref name="settled"/> holds.</summary>
    private static Task SettledAsync(Func<bool> settled) =>
        UiWait.UntilAsync(() =>
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            return settled();
        });

    private static Color Colour(Control control, string key) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(control.FindResource(control.ActualThemeVariant, key)).Color;

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte c) => c / 255.0 is var v && v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        static double Luminance(Color c) => (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
        var (high, low) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (high + 0.05) / (low + 0.05);
    }
}
