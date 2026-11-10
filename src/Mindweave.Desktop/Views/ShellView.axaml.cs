using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mindweave.Application.Preferences;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;

namespace Mindweave.Desktop.Views;

public sealed partial class ShellView : UserControl
{
    /// <summary>The page always keeps at least this much width: a narrow window narrows the sidebar, not the content.</summary>
    public const double MinContentWidth = 480;

    /// <summary>The badge's one pulse when the count grows (2026-11-10): short, once, never looping.</summary>
    internal static readonly TimeSpan PulseDuration = TimeSpan.FromMilliseconds(600);

    private KeyBinding? _toggleSidebar;
    private double? _dragStartX;
    private double _dragStartWidth;
    private WorkspaceNewsViewModel? _news;
    private ConversationListViewModel? _list;
    private int _pulses;

    public ShellView()
    {
        InitializeComponent();
        SidebarSplitter.PointerPressed += OnSplitterPressed;
        SidebarSplitter.PointerMoved += OnSplitterMoved;
        SidebarSplitter.PointerReleased += OnSplitterReleased;
        SidebarSplitter.PointerCaptureLost += (_, _) => EndDrag();

        // Multi-select (requirements v0.12 §8.2): a modified click on a row is taken before the row's button sees it,
        // so it selects instead of opening. Esc is listened for on the window (see OnAttachedToVisualTree).
        ConversationList.AddHandler(PointerPressedEvent, OnRowPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ConversationList.AddHandler(KeyDownEvent, OnRowKeyDown, Avalonia.Interactivity.RoutingStrategies.Bubble);
        DataContextChanged += (_, _) =>
        {
            WatchNews(ViewModel?.WorkspaceNews);
            WatchList(ViewModel?.Conversations);
        };
    }

    /// <summary>Set while the badge's pulse runs (tests).</summary>
    internal bool IsPulsing => OtherWorkspacesNews.Classes.Contains("pulsing");

    private void WatchNews(WorkspaceNewsViewModel? news)
    {
        if (ReferenceEquals(news, _news))
        {
            return;
        }

        if (_news is not null)
        {
            _news.CountRose -= OnNewsCountRose;
        }

        _news = news;
        if (news is not null)
        {
            news.CountRose += OnNewsCountRose;
        }
    }

    private void OnNewsCountRose(object? sender, EventArgs e) => Pulse();

    private void WatchList(ConversationListViewModel? list)
    {
        if (ReferenceEquals(list, _list))
        {
            return;
        }

        if (_list is not null)
        {
            _list.RenameStarted -= OnRenameStarted;
            _list.RenameEnded -= OnRenameEnded;
        }

        _list = list;
        if (list is not null)
        {
            list.RenameStarted += OnRenameStarted;
            list.RenameEnded += OnRenameEnded;
        }
    }

    // ── A row's context menu and its inline rename (2026-11-10) ──────────────────────────────────────────────────────

    /// <summary>Not while rows are selected (the bar acts on them), and not on a row being renamed.</summary>
    private void OnRowMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (MenuRow(sender)?.DataContext is not ConversationItemViewModel || _list?.OffersRowMenu != true)
        {
            e.Cancel = true;
        }
    }

    /// <summary>While its menu is open the row keeps a hover tint, so it is clear which conversation the menu acts on.</summary>
    private void OnRowMenuOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => MenuRow(sender)?.Classes.Set("menu-open", true);

    private void OnRowMenuClosed(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => MenuRow(sender)?.Classes.Set("menu-open", false);

    /// <summary>
    /// Shift+F10 asks for the focused row's menu, as on Windows and Linux; the platform's own gesture (the menu key) is
    /// handled by Avalonia. Only Shift+F10 is added here, so a platform that has both does not open the menu twice.
    /// </summary>
    private void OnRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.F10 || e.KeyModifiers != KeyModifiers.Shift || e.Source is not Button { ContextMenu: not null } row
            || row.DataContext is not ConversationItemViewModel)
        {
            return;
        }

        if (Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.OpenContextMenu.Any(g => g.Matches(e)) == true)
        {
            return;
        }

        row.RaiseEvent(new ContextRequestedEventArgs());
        e.Handled = true;
    }

    /// <summary>The row's box takes the focus with its text selected, once the menu that started it has closed.</summary>
    private void OnRenameStarted(object? sender, ConversationItemViewModel item) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (item.IsRenaming && RowPart<TextBox>(item, "RowRenameBox") is { } box)
                {
                    // Focused as by Tab, the box selects its whole text: typing replaces the old name.
                    box.Focus(NavigationMethod.Tab);
                }
            },
            DispatcherPriority.Background);

    /// <summary>Back to the row, so the keyboard user can go on from where they were (only if the box had the focus).</summary>
    private void OnRenameEnded(object? sender, ConversationItemViewModel item)
    {
        if (RowPart<TextBox>(item, "RowRenameBox") is { IsKeyboardFocusWithin: true })
        {
            Dispatcher.UIThread.Post(() => RowButton(item)?.Focus(NavigationMethod.Tab), DispatcherPriority.Background);
        }
    }

    /// <summary>Enter saves and Esc cancels; both stop here, so the row's button and the window's Esc never see them.</summary>
    private void OnRenameBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ConversationItemViewModel item || _list is null || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            _list.CommitRenameRowCommand.Execute(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _list.CancelRenameRowCommand.Execute(item);
            e.Handled = true;
        }
    }

    /// <summary>Leaving the box (a click elsewhere, Tab) saves, as file lists do.</summary>
    private void OnRenameBoxLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not ConversationItemViewModel { IsRenaming: true } item)
        {
            return;
        }

        // The box's own cut/copy/paste menu took the focus: still editing (to paste a name, say). The focus comes back
        // to the box when that menu closes, and leaving the box after that saves as usual.
        if (box.ContextFlyout is { IsOpen: true })
        {
            return;
        }

        _list?.CommitRenameRowCommand.Execute(item);
    }

    /// <summary>The row that holds <paramref name="menu"/> (asked before the menu is attached to it, too).</summary>
    private Button? MenuRow(object? menu) =>
        ConversationList.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.ContextMenu is { } held && ReferenceEquals(held, menu));

    private T? RowPart<T>(ConversationItemViewModel item, string name)
        where T : Control =>
        ConversationList.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name && ReferenceEquals(c.DataContext, item));

    private Button? RowButton(ConversationItemViewModel item) =>
        ConversationList.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("nav") && ReferenceEquals(b.DataContext, item));

    /// <summary>
    /// One brief swell and fade of the badge from its centre: the "pulsing" class runs the style's animation once
    /// (Sidebar.axaml) and is taken off after it. While one runs, a further increase only lengthens it.
    /// </summary>
    private void Pulse()
    {
        var pulse = ++_pulses;
        OtherWorkspacesNews.Classes.Set("pulsing", true);
        _ = Task.Delay(PulseDuration).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (pulse == _pulses)
                {
                    OtherWorkspacesNews.Classes.Set("pulsing", false);
                }
            }),
            TaskScheduler.Default);
    }

    /// <summary>A hint under the pointer stays; it gets its full time again once the pointer leaves.</summary>
    private void OnToastPointerEntered(object? sender, PointerEventArgs e) => HoldToast(sender, held: true);

    private void OnToastPointerExited(object? sender, PointerEventArgs e) => HoldToast(sender, held: false);

    private void HoldToast(object? sender, bool held)
    {
        if ((sender as StyledElement)?.DataContext is WorkspaceToastViewModel toast)
        {
            _news?.Hold(toast, held);
        }
    }

    /// <summary>The modifier that adds a row to the selection: Cmd on macOS (where Ctrl click is a right click), Ctrl elsewhere.</summary>
    internal static KeyModifiers ToggleModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel?.Conversations is not { } list
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || (e.Source as StyledElement)?.DataContext is not ViewModels.Conversations.ConversationItemViewModel item)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            list.SelectRange(item);
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(ToggleModifier))
        {
            list.Toggle(item);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Esc clears the sidebar's selection wherever the focus is in the shell, or when nothing has it (a Ctrl/Cmd click
    /// does not move the focus). Focus in a window dialog (the delete confirmation) leaves Esc to that dialog's 取消.
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || !IsEffectivelyVisible
            || ViewModel?.Conversations is not { ShowsSelectionBar: true } list)
        {
            return;
        }

        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        if (focused is null || ReferenceEquals(focused, TopLevel.GetTopLevel(this)) || this.IsVisualAncestorOf(focused))
        {
            list.ClearSelection();
            e.Handled = true;
        }
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
            top.AddHandler(KeyDownEvent, OnWindowKeyDown, Avalonia.Interactivity.RoutingStrategies.Bubble);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_toggleSidebar is not null && TopLevel.GetTopLevel(this) is { } top)
        {
            top.KeyBindings.Remove(_toggleSidebar);
            top.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
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
