using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Mindweave.Application.Preferences;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.Views.Pages;

// The summary's text follows the text width chosen for conversations (D-037): its column stays left-aligned, the header,
// history and editor keep to it, and its right edge is dragged to change it.
public sealed partial class SummaryView
{
    /// <summary>How far from the text the grip stands, so it never takes a press meant for the text.</summary>
    private const double GripGap = 5;

    /// <summary>The sentence at this distance below the top of the page stays there while the text is rewrapped.</summary>
    private const double ReadingLine = 8;

    private TextWidth? _textWidth;
    private double _widthAtStart;

    private void AttachTextColumn()
    {
        TextColumnArea.SizeChanged += (_, _) => PlaceGrip();
        TextGrip.MoveStarted += (_, _) => _widthAtStart = TextColumn();
        TextGrip.Moved += (_, delta) =>
        {
            if (_textWidth is { } width)
            {
                var wanted = Math.Max(TextWidthService.Min, _widthAtStart + delta);
                width.Pixels = wanted >= WholeWidth() - 1 ? null : wanted;
            }
        };
        TextGrip.MoveEnded += (_, _) => _textWidth?.Save();
        TextGrip.ResetRequested += (_, _) =>
        {
            if (_textWidth is { } width)
            {
                width.Pixels = TextWidthService.Default;
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
        if (width is not null && IsLoaded)
        {
            width.PropertyChanged += OnTextWidthChanged;
        }

        ApplyTextWidth();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (_textWidth is not null)
        {
            _textWidth.PropertyChanged -= OnTextWidthChanged;
            _textWidth.PropertyChanged += OnTextWidthChanged;
        }

        ApplyTextWidth();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (_textWidth is not null)
        {
            _textWidth.PropertyChanged -= OnTextWidthChanged;
        }

        base.OnUnloaded(e);
    }

    private void OnTextWidthChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The sentence being read stays in place: where it is now, then where it went.
        var locator = Scroller.Offset.Y > 0 && Scroller.IsEffectivelyVisible ? SourceAnchors.LocatorAt(_renderer, Scroller, ReadingLine) : null;
        ApplyTextWidth();
        if (locator is null)
        {
            return;
        }

        UpdateLayout();
        if (SourceAnchors.ReadingLineOf(locator, _renderer, Scroller) is { } line)
        {
            Scroller.Offset = new Vector(0, Math.Clamp(Scroller.Offset.Y + line - ReadingLine, 0, Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height)));
            UpdateLayout();
        }
    }

    private void ApplyTextWidth()
    {
        // Without the preference (a page made for a test) the width is the default.
        var width = _textWidth is null ? TextWidthService.Default : _textWidth.Pixels ?? double.PositiveInfinity;
        foreach (var grid in (Grid[])[HeaderColumn, HistoryColumn, EditorColumn, BodyColumn])
        {
            grid.ColumnDefinitions[0].MaxWidth = width;
        }

        PlaceGrip();
    }

    /// <summary>The widest the text can be on this page: the scroll bar's room is kept free.</summary>
    private double WholeWidth() => Math.Max(0, TextColumnArea.Bounds.Width - BodyColumn.Margin.Right);

    private double TextColumn() => Math.Min(_textWidth?.Pixels ?? WholeWidth(), WholeWidth());

    private void PlaceGrip()
    {
        var column = TextColumn();
        TextGrip.Margin = new Thickness(column >= WholeWidth() ? Math.Max(0, column - GripGap - TextGrip.Width) : column + GripGap, 0, 0, 0);
    }
}
