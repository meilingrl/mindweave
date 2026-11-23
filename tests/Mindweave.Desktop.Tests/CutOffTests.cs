using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace Mindweave.Desktop.Tests;

/// <summary>The finder itself: what it reports and what it leaves alone.</summary>
public class CutOffTests
{
    [AvaloniaFact]
    public void A_button_past_the_edge_of_what_cuts_it_is_found() =>
        Assert.Contains("Button Late", Assert.Single(Cut(Row(scrolls: false))), StringComparison.Ordinal);

    [AvaloniaFact]
    public void A_button_that_scrolling_brings_into_view_is_left_alone() => Assert.Empty(Cut(Row(scrolls: true)));

    [AvaloniaFact]
    public void A_text_squeezed_by_its_place_is_found() =>
        Assert.Contains("needs", Assert.Single(Cut(new TextBlock { Text = "A text that is longer than this", Width = 60 })), StringComparison.Ordinal);

    [AvaloniaFact]
    public void A_text_that_wraps_or_ends_in_an_ellipsis_is_left_alone()
    {
        Assert.Empty(Cut(new TextBlock { Text = "A text that is longer than this", Width = 60, TextWrapping = TextWrapping.Wrap }));
        Assert.Empty(Cut(new TextBlock { Text = "A text that is longer than this", Width = 60, TextTrimming = TextTrimming.CharacterEllipsis }));
    }

    [AvaloniaFact]
    public void Something_hidden_is_left_alone()
    {
        var row = Row(scrolls: false);
        ((StackPanel)row.Content!).Children[1].IsVisible = false;
        Assert.Empty(Cut(row));
    }

    [AvaloniaFact]
    public void A_button_that_lies_over_another_is_found()
    {
        var row = new DockPanel
        {
            Width = 120,
            Children = { new CheckBox { Name = "Wide", Width = 110, [DockPanel.DockProperty] = Dock.Right }, new Button { Name = "Under", Width = 60, Height = 30, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left } },
        };
        Assert.Contains(Cut(row), p => p.Contains("overlap", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Buttons_beside_each_other_are_left_alone() =>
        Assert.Empty(Cut(new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { new Button { Width = 60, Height = 30 }, new Button { Width = 60, Height = 30 } } }));

    private static ScrollViewer Row(bool scrolls) => new()
    {
        HorizontalScrollBarVisibility = scrolls ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        Content = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children = { new Border { Width = 180 }, new Button { Name = "Late", Width = 80, Height = 30 } },
        },
    };

    private static IReadOnlyList<string> Cut(Control content)
    {
        var window = new Window { Width = 200, Height = 100, Content = content };
        window.Show();
        var cut = CutOff.In(window);
        window.Close();
        return cut;
    }
}
