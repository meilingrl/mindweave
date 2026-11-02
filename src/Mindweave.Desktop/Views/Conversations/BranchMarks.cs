using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
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

    private readonly Dictionary<MessageItemViewModel, Button> _marks = [];
    private ScrollViewer? _scroller;
    private ItemsControl? _list;
    private ScrollBar? _bar;

    public BranchMarks()
    {
        ClipToBounds = false;
    }

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
        var previousBottom = double.NegativeInfinity;
        foreach (var answer in answers)
        {
            var mark = MarkFor(answer);
            var top = scrolls && Section(answer) is { } section ? ContentTop(section) : null;
            mark.IsVisible = top is not null;
            if (top is not { } y || track is not { } rect)
            {
                continue;
            }

            // Where the scroll thumb's top would be with this list at the top of the viewport; kept on the track and
            // never overlapping the previous mark, so each one stays its own target.
            var centre = rect.Top + (Math.Clamp(y / extent, 0, 1) * rect.Height);
            var markTop = Math.Clamp(centre - (MarkHeight / 2), rect.Top, Math.Max(rect.Top, rect.Bottom - MarkHeight));
            markTop = Math.Max(markTop, previousBottom + 2);
            previousBottom = markTop + MarkHeight;
            Place(mark, rect.Left - Gap - MarkWidth, markTop);
        }
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
