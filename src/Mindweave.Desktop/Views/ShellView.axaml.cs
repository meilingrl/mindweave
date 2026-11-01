using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Views;

public sealed partial class ShellView : UserControl
{
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
        _dragStartWidth = shell.SidebarWidth;
        e.Pointer.Capture(SidebarSplitter);
        SidebarSplitter.Classes.Set("dragging", true);
        e.Handled = true;
    }

    private void OnSplitterMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStartX is { } start && ViewModel is { } shell)
        {
            // The view model keeps the width within its bounds.
            shell.SidebarWidth = _dragStartWidth + (e.GetPosition(this).X - start);
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
