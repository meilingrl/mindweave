using Avalonia.Automation.Peers;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Mindweave.Desktop.Views;

/// <summary>
/// A thin vertical grip on a boundary that can be moved: the resize pointer and a quiet line say so on hover, as at the
/// sidebar. Dragging, or the arrow keys while it has the focus, reports how far it moved; a double click asks for the default.
/// </summary>
public sealed class EdgeGrip : Border
{
    /// <summary>How far an arrow key moves the boundary.</summary>
    public const double KeyStep = 16;

    private double? _start;

    public EdgeGrip()
    {
        Classes.Add("edge-grip");
        Focusable = true;
        Child = new Border { Classes = { "edge-grip-line" }, HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetControlTypeOverride(this, AutomationControlType.Thumb);
    }

    /// <summary>A drag or a key move begins: what is being resized is measured from here.</summary>
    public event EventHandler? MoveStarted;

    /// <summary>The boundary is this far to the right of where the move began (negative: to the left).</summary>
    public event EventHandler<double>? Moved;

    /// <summary>The move is over: the size can be kept.</summary>
    public event EventHandler? MoveEnded;

    /// <summary>A double click, or Home: back to the default.</summary>
    public event EventHandler? ResetRequested;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            End();
            e.Pointer.Capture(null);
            ResetRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Measured against the window: the grip itself moves with the boundary.
        _start = e.GetPosition(null).X;
        e.Pointer.Capture(this);
        Classes.Set("dragging", true);
        MoveStarted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        if (_start is { } start)
        {
            Moved?.Invoke(this, e.GetPosition(null).X - start);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerReleased(e);
        if (_start is not null)
        {
            e.Pointer.Capture(null);
            End();
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        End();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        var step = e.Key switch { Key.Left => -KeyStep, Key.Right => KeyStep, _ => 0 };
        if (step != 0)
        {
            MoveStarted?.Invoke(this, EventArgs.Empty);
            Moved?.Invoke(this, step);
            MoveEnded?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.Key == Key.Home)
        {
            ResetRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void End()
    {
        if (_start is null)
        {
            return;
        }

        _start = null;
        Classes.Set("dragging", false);
        MoveEnded?.Invoke(this, EventArgs.Empty);
    }
}
