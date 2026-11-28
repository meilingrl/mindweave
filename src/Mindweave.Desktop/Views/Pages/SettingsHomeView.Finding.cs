using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace Mindweave.Desktop.Views.Pages;

// Finding a setting (D-037): the category list jumps to a section and follows the scrolling; the find box leaves only the
// settings whose words match. Nothing is hidden behind a category, so scrolling through the page still meets every setting.
public sealed partial class SettingsHomeView
{
    /// <summary>Below this the category list would crowd the settings: only the find box stays, above them.</summary>
    internal const double NarrowWidth = 640;

    /// <summary>A section counts as reached when its heading is this far below the top.</summary>
    private const double ReachedAt = 48;

    private IReadOnlyList<(RadioButton Category, StackPanel Section)> _categories = [];
    private double? _jumped;
    private bool? _narrow;

    private void AttachFinding()
    {
        _categories =
        [
            (CategoryAppearance, AppearanceSection), (CategoryHints, HintsSection), (CategoryModel, ModelSection),
            (CategoryNote, NoteSection), (CategoryTools, ToolsArea), (CategoryData, DataSection),
        ];
        foreach (var (category, section) in _categories)
        {
            category.Click += (_, _) => JumpTo(section);
        }

        SegmentKeys.Attach(BoldColourPicker);
        SegmentKeys.Attach(TextWidthPicker);
        Scroller.ScrollChanged += (_, _) => FollowScroll();
        SizeChanged += (_, _) => ArrangeNav();
        FindSettingsBox.TextChanged += (_, _) => FilterSettings();
        FindSettingsBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(FindSettingsBox.Text))
            {
                FindSettingsBox.Text = string.Empty;
                e.Handled = true;
            }
        };
    }

    private void JumpTo(StackPanel section)
    {
        FindSettingsBox.Text = string.Empty;
        UpdateLayout();
        if (section.TranslatePoint(default, Scroller)?.Y is { } y)
        {
            _jumped = Math.Clamp(Scroller.Offset.Y + y, 0, Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height));
            Scroller.Offset = new Vector(0, _jumped.Value);
        }
    }

    /// <summary>The category whose section is at the top is marked; one just chosen stays marked, even near the end where it cannot reach the top.</summary>
    private void FollowScroll()
    {
        if (_jumped is { } target && Math.Abs(Scroller.Offset.Y - target) < 1)
        {
            return;
        }

        _jumped = null;
        var atEnd = Scroller.Extent.Height > Scroller.Viewport.Height && Scroller.Offset.Y >= Scroller.Extent.Height - Scroller.Viewport.Height - 1;
        var shown = _categories.Where(c => c.Section.IsVisible && c.Category.IsVisible).ToList();
        var current = atEnd ? shown.LastOrDefault() : shown.LastOrDefault(c => c.Section.TranslatePoint(default, Scroller)?.Y <= ReachedAt);
        (current.Category ?? CategoryAppearance).IsChecked = true;
    }

    private void ArrangeNav()
    {
        var narrow = Bounds.Width < NarrowWidth;
        if (_narrow == narrow)
        {
            return;
        }

        _narrow = narrow;
        Categories.IsVisible = !narrow;
        Grid.SetRowSpan(SettingsNav, narrow ? 1 : 2);
        Grid.SetColumnSpan(SettingsNav, narrow ? 2 : 1);
        Grid.SetColumn(Scroller, narrow ? 0 : 1);
        Grid.SetColumnSpan(Scroller, narrow ? 2 : 1);
        SettingsNav.Width = narrow ? 240 : 172;
        SettingsNav.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        SettingsNav.Margin = narrow ? new Thickness(0, 4, 0, 4) : new Thickness(0, 4, 24, 0);
    }

    private void FilterSettings()
    {
        var words = (FindSettingsBox.Text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var any = false;
        foreach (var (_, section) in _categories)
        {
            var heading = section.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("section-heading"))?.Text;

            // A category's own name shows all of it.
            var all = words.Length == 0 || Matches(words, [heading]);
            var found = all;
            foreach (var item in section.GetLogicalDescendants().OfType<Control>().Where(c => c is SettingRow || c.Classes.Contains("settings-block")))
            {
                var shows = all || Matches(words, WordsOf(item));
                found |= shows;
                if (item is SettingRow row)
                {
                    row.IsFound = shows;
                }
                else
                {
                    item.IsVisible = shows;
                }
            }

            section.IsVisible = found;
            any |= found;
        }

        AppVersion.IsVisible = words.Length == 0;
        NothingFound.IsVisible = !any;
        NothingFound.Text = any ? null : UiText.NoSettingFound(FindSettingsBox.Text!.Trim());
        if (words.Length > 0)
        {
            Scroller.Offset = default;
        }
    }

    private static bool Matches(string[] words, IEnumerable<string?> texts)
    {
        var all = string.Join('\n', texts);
        return words.All(w => all.Contains(w, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Every word a setting says: its name, its lines, its choices, and what a screen reader is told.</summary>
    private static IEnumerable<string?> WordsOf(Control item)
    {
        foreach (var control in item.GetLogicalDescendants().OfType<Control>().Prepend(item))
        {
            yield return AutomationProperties.GetName(control);
            switch (control)
            {
                case SettingRow row:
                    yield return row.Label;
                    yield return row.Caption;
                    yield return row.Info;
                    break;
                case TextBlock text:
                    yield return text.Text;
                    break;
                case ContentControl { Content: string content }:
                    yield return content;
                    break;
            }
        }
    }
}
