using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Mindweave.Desktop.ViewModels.Conversations;

namespace Mindweave.Desktop.Views.Conversations;

/// <summary>
/// Marks in the margin (S7 round 4, your Windows test, finding 5): a narrow gutter just left of the conversation's
/// vertical scroll bar, with one small mark per answer that has branches, at that answer's branch list's relative
/// place in the whole conversation (the same mapping the scroll thumb uses). Clicking a mark scrolls its
/// "从这里分出的探索" cards into view. The layer covers the page but has no background, so only the marks take
/// pointer input; they never overlap the scroll bar. Shown only while the content scrolls.
/// </summary>
internal sealed class BranchMarks : Canvas
{
    /// <summary>Width and height of a mark's hit target; the visible bar inside is smaller.</summary>
    internal const double MarkWidth = 10;

    internal const double MarkHeight = 10;

    /// <summary>Space between the gutter and the scroll bar.</summary>
    internal const double Gap = 1;

    /// <summary>Space kept above a revealed branch list when it is taller than half the viewport.</summary>
    internal const double RevealMargin = 24;

    /// <summary>Space between neighbouring marks while the track has room for all of them.</summary>
    internal const double MarkSpacing = 2;

    /// <summary>Width of the faint rail the marks sit on, which ties them to the scroll bar rather than to the text beside them.</summary>
    internal const double RailWidth = 2;

    /// <summary>Pixels scrolled per wheel notch, as the scroll viewer's own content presenter does.</summary>
    internal const double WheelStep = 50;

    private readonly Dictionary<MessageItemViewModel, Button> _marks = [];
    private ScrollViewer? _scroller;
    private ItemsControl? _list;
    private readonly Border _rail = new() { Classes = { "branch-mark-rail" }, Width = RailWidth, IsVisible = false, IsHitTestVisible = false };
    private ScrollBar? _bar;

    public BranchMarks()
    {
        // The layer covers exactly the scroll viewer: nothing it draws may reach the composer below it.
        ClipToBounds = true;
        Children.Add(_rail);

        // The layer is not inside the scroll viewer, so a wheel over a mark is passed on to it here.
        AddHandler(PointerWheelChangedEvent, OnWheel, handledEventsToo: false);
    }

    /// <summary>The rail behind the marks, shown while any mark is.</summary>
    internal Border Rail => _rail;

    /// <summary>The marks now shown, top to bottom, with the answer each belongs to.</summary>
    internal IReadOnlyList<(MessageItemViewModel Answer, Button Mark)> Marks =>
        [.. _marks.Where(m => m.Value.IsVisible).OrderBy(m => GetTop(m.Value)).Select(m => (m.Key, m.Value))];

    /// <summary>Follows <paramref name="scroller"/> and the answers in <paramref name="list"/>; called once by the page.</summary>
    internal void Attach(ScrollViewer scroller, ItemsControl list)
    {
        _scroller = scroller;
        _list = list;

        // Whatever moves an answer's branch list relative to the whole conversation (text streaming in, a branch list
        // appearing, a resize, a theme with other metrics) changes the extent or the viewport, which raises this after
        // the layout pass. Nothing is touched when the places are the same.
        scroller.ScrollChanged += (_, _) => Refresh();
        scroller.TemplateApplied += (_, e) => _bar = e.NameScope.Find<ScrollBar>("PART_VerticalScrollBar");
    }

    /// <summary>Scrolls so that this answer's branch list is in view: centred when it fits, else its top near the top.</summary>
    internal void Reveal(MessageItemViewModel answer)
    {
        if (_scroller is not { } scroller || Section(answer) is not { } section || ContentTop(section) is not { } top)
        {
            return;
        }

        var viewport = scroller.Viewport.Height;
        var height = section.Bounds.Height;
        var target = height <= viewport / 2 ? top + (height / 2) - (viewport / 2) : top - RevealMargin;
        scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(target, 0, Math.Max(0, scroller.Extent.Height - viewport)));
    }

    /// <summary>Re-places, adds and removes the marks to match the answers and the layout now.</summary>
    internal void Refresh()
    {
        if (_scroller is not { } scroller || _list is null)
        {
            return;
        }

        var answers = _list.Items.OfType<MessageItemViewModel>().Where(m => m.IsAnswer && m.Branches.Count > 0).ToList();
        foreach (var stale in _marks.Keys.Except(answers).ToList())
        {
            Children.Remove(_marks[stale]);
            _marks.Remove(stale);
        }

        var extent = scroller.Extent.Height;
        var track = TrackRect(scroller);
        var scrolls = extent > scroller.Viewport.Height + 0.5 && track is not null;
        var placed = new List<(Button Mark, double Top)>();
        foreach (var answer in answers)
        {
            var mark = MarkFor(answer);
            var top = scrolls && Section(answer) is { } section ? ContentTop(section) : null;
            mark.IsVisible = top is not null;
            if (top is { } y && track is { } rect)
            {
                // Where the scroll thumb's top would be with this list at the top of the viewport.
                var centre = rect.Top + (Math.Clamp(y / extent, 0, 1) * rect.Height);
                placed.Add((mark, centre - (MarkHeight / 2)));
            }
        }

        if (track is { } bounds && placed.Count > 0)
        {
            var tops = Spread([.. placed.Select(p => p.Top)], bounds.Top, bounds.Bottom);
            var x = bounds.Left - Gap - MarkWidth;
            for (var i = 0; i < placed.Count; i++)
            {
                Place(placed[i].Mark, x, tops[i]);
            }

            Place(_rail, x + ((MarkWidth - RailWidth) / 2), bounds.Top);
            if (Math.Abs(_rail.Height - bounds.Height) > 0.25 || double.IsNaN(_rail.Height))
            {
                _rail.Height = bounds.Height;
            }
        }

        _rail.IsVisible = placed.Count > 0;
    }

    /// <summary>
    /// Final tops for marks wanted at <paramref name="wanted"/> (in order, top to bottom) on a track from
    /// <paramref name="top"/> to <paramref name="bottom"/>: each on the track, in order and apart, as near its wanted
    /// place as that allows. Marks crowded near the end are pushed up from the track's bottom, not past it; when even
    /// that is too little room, they share the track evenly.
    /// </summary>
    internal static double[] Spread(IReadOnlyList<double> wanted, double top, double bottom)
    {
        var count = wanted.Count;
        var result = new double[count];
        if (count == 0)
        {
            return result;
        }

        var last = Math.Max(top, bottom - MarkHeight);
        var pitch = MarkHeight + MarkSpacing;
        if (count > 1 && (count - 1) * pitch > last - top)
        {
            pitch = (last - top) / (count - 1);
        }

        for (var i = 0; i < count; i++)
        {
            var at = Math.Clamp(wanted[i], top, last);
            result[i] = i == 0 ? at : Math.Max(at, result[i - 1] + pitch);
        }

        for (var i = count - 1; i >= 0; i--)
        {
            result[i] = i == count - 1 ? Math.Min(result[i], last) : Math.Min(result[i], result[i + 1] - pitch);
        }

        for (var i = 0; i < count; i++)
        {
            result[i] = Math.Max(result[i], top);
        }

        return result;
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_scroller is not { } scroller || e.Delta.Y == 0)
        {
            return;
        }

        var max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(scroller.Offset.Y - (e.Delta.Y * WheelStep), 0, max));
        e.Handled = true;
    }

    private Button MarkFor(MessageItemViewModel answer)
    {
        if (!_marks.TryGetValue(answer, out var mark))
        {
            mark = new Button
            {
                Classes = { "branch-mark" },
                Width = MarkWidth,
                Height = MarkHeight,
                IsTabStop = false,
                // A click leads to the branch list without taking the keyboard from the composer.
                Focusable = false,
                Content = new Border { Classes = { "branch-mark-bar" } },
            };
            mark.Click += (_, _) => Reveal(answer);
            _marks[answer] = mark;
            Children.Add(mark);
        }

        Describe(mark, answer);
        return mark;
    }

    private static void Describe(Button mark, MessageItemViewModel answer)
    {
        var label = answer.Branches.Count > 0 ? UiText.BranchMark(answer.Branches.Count, answer.Branches[0].Quote) : null;
        if (!Equals(ToolTip.GetTip(mark), label))
        {
            ToolTip.SetTip(mark, label);
            AutomationProperties.SetName(mark, label);
        }
    }

    private static void Place(Control mark, double x, double y)
    {
        if (Math.Abs(GetLeft(mark) - x) > 0.25 || double.IsNaN(GetLeft(mark)))
        {
            SetLeft(mark, x);
        }

        if (Math.Abs(GetTop(mark) - y) > 0.25 || double.IsNaN(GetTop(mark)))
        {
            SetTop(mark, y);
        }
    }

    /// <summary>The vertical scroll bar's track in this layer's coordinates; null while the bar is not laid out.</summary>
    private Rect? TrackRect(ScrollViewer scroller)
    {
        // The bar is found once (from the template, else by a search of the scroll viewer's own parts only).
        _bar ??= scroller.GetTemplateDescendants().OfType<ScrollBar>().FirstOrDefault(b => b.Orientation == Orientation.Vertical);
        var bar = _bar;
        if (bar is not { IsVisible: true } || bar.Bounds.Height <= 0 || bar.TranslatePoint(default, this) is not { } barOrigin)
        {
            return null;
        }

        // The track excludes the bar's arrow buttons, so a mark sits where the thumb would be for that place.
        var track = bar.GetVisualDescendants().OfType<Track>().FirstOrDefault();
        var top = track?.TranslatePoint(default, this)?.Y ?? barOrigin.Y;
        var height = track is { Bounds.Height: > 0 } ? track.Bounds.Height : bar.Bounds.Height;
        return new Rect(barOrigin.X, top, bar.Bounds.Width, height);
    }

    /// <summary>
    /// The answer's branch list: a direct part of the message template (its root panel, then the answer's panel), so it
    /// is found without walking through the answer's rendered text.
    /// </summary>
    private Border? Section(MessageItemViewModel answer) =>
        _list?.ContainerFromItem(answer) is Avalonia.Controls.Presenters.ContentPresenter { Child: Panel root }
            ? root.Children.OfType<Panel>().SelectMany(p => p.Children).OfType<Border>().FirstOrDefault(b => b.Name == "BranchSection" && b.IsVisible)
            : null;

    /// <summary>A control's top measured from the top of the whole scrollable content.</summary>
    private double? ContentTop(Visual visual) =>
        _scroller is { } scroller && visual.TranslatePoint(default, scroller) is { } point ? point.Y + scroller.Offset.Y : null;
}
