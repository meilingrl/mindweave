using System.ComponentModel;
using Avalonia;
using Avalonia.Interactivity;
using Mindweave.Application.Preferences;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.Views.Conversations;

// The text's width (the owner, 2026-11-23): the messages and the input share one centred column of at most the width chosen,
// the page keeps its own, and the column's edges are dragged to change it.
public sealed partial class ConversationView
{
    /// <summary>The list starts this far right of the scroll viewer's edge (Conversation.axaml).</summary>
    private const double ListInset = 8;

    /// <summary>A grip's width, and how far from the text it stands, so it never takes a press meant for the text.</summary>
    private const double GripWidth = 9;

    private const double GripGap = 5;

    private TextWidth? _textWidth;
    private double _widthAtStart;

    private void AttachTextColumn()
    {
        SizeChanged += (_, _) => ArrangeTextColumn();
        AttachGrip(LeftTextGrip, -1);
        AttachGrip(RightTextGrip, 1);
    }

    private void AttachGrip(EdgeGrip grip, int side)
    {
        grip.MoveStarted += (_, _) => _widthAtStart = TextColumn().Width;

        // The column is centred: its other edge moves as far the other way, so the edge dragged stays under the pointer.
        grip.Moved += (_, delta) =>
        {
            if (_textWidth is { } width)
            {
                var wanted = Math.Max(TextWidthService.Min, _widthAtStart + (2 * side * delta));
                width.Pixels = wanted >= WholeWidth() - 1 ? null : wanted;
            }
        };
        grip.MoveEnded += (_, _) => _textWidth?.Save();
        grip.ResetRequested += (_, _) =>
        {
            if (_textWidth is { } width)
            {
                width.Pixels = null;
                width.Save();
            }
        };
    }

    private void FollowTextWidth(TextWidth? width)
    {
        if (_textWidth is not null)
        {
            _textWidth.PropertyChanged -= OnTextWidthChanged;
        }

        _textWidth = width;
        if (width is not null && this.IsLoaded)
        {
            width.PropertyChanged += OnTextWidthChanged;
        }

        ArrangeTextColumn();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (_textWidth is not null)
        {
            _textWidth.PropertyChanged -= OnTextWidthChanged;
            _textWidth.PropertyChanged += OnTextWidthChanged;
        }

        ArrangeTextColumn();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (_textWidth is not null)
        {
            _textWidth.PropertyChanged -= OnTextWidthChanged;
        }

        base.OnUnloaded(e);
    }

    private void OnTextWidthChanged(object? sender, PropertyChangedEventArgs e) => ArrangeTextColumn();

    /// <summary>Room on the right for the scroll bar, and for the branch marks' gutter on a page with branches.</summary>
    private double RightInset => _page?.HasBranches == true ? 28 : 12;

    /// <summary>The widest the text can be on this page.</summary>
    private double WholeWidth() => Math.Max(0, Bounds.Width - RightInset);

    /// <summary>The column on this page: the width chosen, centred, clear of the scroll bar; the whole page when none is chosen or it is narrower.</summary>
    private (double Left, double Width) TextColumn()
    {
        var whole = WholeWidth();
        if (_textWidth?.Pixels is not { } chosen || chosen >= whole)
        {
            return (0, whole);
        }

        return (Math.Min((Bounds.Width - chosen) / 2, whole - chosen), chosen);
    }

    private void ArrangeTextColumn()
    {
        if (Bounds.Width <= 0)
        {
            return;
        }

        var (left, width) = TextColumn();
        var isWhole = width >= WholeWidth();
        if (isWhole)
        {
            // The styles' own margins (the branch marks' gutter follows the page's branches).
            MessageList.ClearValue(MarginProperty);
            ComposerColumn.Margin = new Thickness(0, 10, 0, 0);
        }
        else
        {
            var right = Bounds.Width - left - width;
            MessageList.Margin = new Thickness(left + ListInset, 0, right, 0);
            ComposerColumn.Margin = new Thickness(left, 10, right, 0);
        }

        LeftTextGrip.Margin = new Thickness(left - GripGap - GripWidth, 0, 0, 0);
        RightTextGrip.Margin = new Thickness(left + width + GripGap, 0, 0, 0);

        // At the whole width the right edge is the scroll bar's; the left one is there to drag.
        RightTextGrip.IsVisible = !isWhole;
    }
}
