using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Mindweave.Application.Preferences;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Views;

public sealed partial class ShellView : UserControl
{
    /// <summary>The page always keeps at least this much width: a narrow window narrows the sidebar, not the content.</summary>
    public const double MinContentWidth = 480;

    private KeyBinding? _toggleSidebar;
    private double? _dragStartX;
    private double _dragStartWidth;

    public ShellView()
    {
        InitializeComponent();
        SidebarSplitter.PointerPressed += OnSplitterPressed;
        SidebarSplitter.PointerMoved += OnSplitterMoved;
        SidebarSplitter.PointerReleased += OnSplitterReleased;
        SidebarSplitter.PointerCaptureLost += (_, _) => EndDrag();
    }

    private ShellViewModel? ViewModel => DataContext as ShellViewModel;

    /// <summary>
    /// Caps the sidebar at what the window can spare. Only the shown width is capped: the stored width stays, so the
    /// sidebar widens again when the window does.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        // Set before the children are measured, so a resized window lays out once with the cap in place.
        var cap = double.IsFinite(availableSize.Width) ? Math.Max(SidebarLayout.MinWidth, availableSize.Width - MinContentWidth) : double.PositiveInfinity;
        if (Sidebar.MaxWidth != cap)
        {
            Sidebar.MaxWidth = cap;
        }

        return base.MeasureOverride(availableSize);
    }

    /// <summary>The collapse shortcut works wherever the focus is in the window, so it is bound on the window itself.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            _toggleSidebar = new KeyBinding
            {
                Gesture = new KeyGesture(Key.B, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control),
                Command = new ToggleSidebar(this),
            };
            top.KeyBindings.Add(_toggleSidebar);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_toggleSidebar is not null && TopLevel.GetTopLevel(this) is { } top)
        {
            top.KeyBindings.Remove(_toggleSidebar);
        }

        _toggleSidebar = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSplitterPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not { } shell || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragStartX = e.GetPosition(this).X;

        // From the width shown, which a narrow window may cap below the stored one.
        _dragStartWidth = Math.Min(shell.SidebarWidth, Sidebar.MaxWidth);
        e.Pointer.Capture(SidebarSplitter);
        SidebarSplitter.Classes.Set("dragging", true);
        e.Handled = true;
    }

    private void OnSplitterMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStartX is { } start && ViewModel is { } shell)
        {
            // The view model keeps the width within its bounds; the window's cap applies as well.
            shell.SidebarWidth = Math.Min(_dragStartWidth + (e.GetPosition(this).X - start), Sidebar.MaxWidth);
            e.Handled = true;
        }
    }

    private void OnSplitterReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragStartX is not null)
        {
            e.Pointer.Capture(null);
            EndDrag();
            e.Handled = true;
        }
    }

    private void EndDrag()
    {
        if (_dragStartX is null)
        {
            return;
        }

        _dragStartX = null;
        SidebarSplitter.Classes.Set("dragging", false);
        ViewModel?.CommitSidebarWidth();
    }

    /// <summary>Routes the window's shortcut to whichever shell view model this view shows.</summary>
    private sealed class ToggleSidebar(ShellView view) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => view.ViewModel is not null && view.IsEffectivelyVisible;

        public void Execute(object? parameter) => view.ViewModel?.ToggleSidebarCommand.Execute(null);
    }
}
