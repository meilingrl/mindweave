using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Views;

/// <summary>
/// The sidebar's top (2026-11-10): the workspace switcher, its menu and the inline new-workspace form. The view owns
/// only focus and keys; what each entry does is in <see cref="ShellViewModel"/>.
/// </summary>
public sealed partial class SidebarHeader : UserControl
{
    private ShellViewModel? _shell;
    private WorkspaceNewsViewModel? _news;
    private int _pulses;
    private bool _openedByKeyboard;

    public SidebarHeader()
    {
        InitializeComponent();
        WorkspacePicker.AddHandler(KeyDownEvent, OnSwitcherKeyDown, RoutingStrategies.Tunnel);
        WorkspaceMenuCard.AddHandler(KeyDownEvent, OnMenuKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => Watch(DataContext as ShellViewModel);
    }

    /// <summary>Set while the badge's pulse runs (tests).</summary>
    internal bool IsPulsing => OtherWorkspacesNews.Classes.Contains("pulsing");

    /// <summary>The menu's rows in order: every workspace, then 新建工作区… and 管理工作区… (what the arrow keys move between).</summary>
    internal IReadOnlyList<Button> MenuRows =>
        [.. WorkspaceMenuCard.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("switcher-row") && b.IsEffectivelyVisible)];

    private void Watch(ShellViewModel? shell)
    {
        if (ReferenceEquals(shell, _shell))
        {
            return;
        }

        if (_shell is not null)
        {
            _shell.PropertyChanged -= OnShellChanged;
        }

        if (_news is not null)
        {
            _news.CountRose -= OnNewsCountRose;
        }

        _shell = shell;
        _news = shell?.WorkspaceNews;
        if (shell is not null)
        {
            shell.PropertyChanged += OnShellChanged;
        }

        if (_news is not null)
        {
            _news.CountRose += OnNewsCountRose;
        }
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsWorkspaceMenuOpen) && _shell?.IsWorkspaceMenuOpen == true)
        {
            FitMenu();
        }

        if (e.PropertyName == nameof(ShellViewModel.IsCreatingWorkspace) && _shell?.IsCreatingWorkspace == true)
        {
            // Once the form is shown (and the menu that opened it has closed), its box takes the focus.
            Dispatcher.UIThread.Post(() => NewWorkspaceName.Focus(NavigationMethod.Tab), DispatcherPriority.Background);
        }
    }

    // ── The switcher and its menu ────────────────────────────────────────────────────────────────────────────────────

    private void OnSwitcherClick(object? sender, RoutedEventArgs e)
    {
        if (_shell is null)
        {
            return;
        }

        _openedByKeyboard = false;
        _shell.IsWorkspaceMenuOpen = !_shell.IsWorkspaceMenuOpen;
    }

    /// <summary>Enter, Space, Down or Alt+Down open the menu with the open workspace's row focused.</summary>
    private void OnSwitcherKeyDown(object? sender, KeyEventArgs e)
    {
        if (_shell is null || _shell.IsWorkspaceMenuOpen)
        {
            return;
        }

        var opens = e.Key switch
        {
            Key.Enter or Key.Space or Key.Down => e.KeyModifiers is KeyModifiers.None or KeyModifiers.Alt,
            _ => false,
        };
        if (opens)
        {
            _openedByKeyboard = true;
            _shell.IsWorkspaceMenuOpen = true;
            e.Handled = true;
        }
    }

    /// <summary>The card is at least as wide as the switcher (the popup adds the margin its shadow is drawn in).</summary>
    private void FitMenu() => WorkspaceMenu.MinWidth = WorkspacePicker.Bounds.Width + WorkspaceMenuCard.Margin.Left + WorkspaceMenuCard.Margin.Right;

    private void OnMenuOpened(object? sender, EventArgs e)
    {
        FitMenu();
        var keyboard = _openedByKeyboard;
        WorkspaceMenuCard.Classes.Set("shown", false);
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_shell?.IsWorkspaceMenuOpen != true)
                {
                    return;
                }

                // Faded and slid in from 4 px above (Styles/WorkspaceSwitcher.axaml), never a jump.
                WorkspaceMenuCard.Classes.Set("shown", true);
                var rows = MenuRows;
                var start = rows.FirstOrDefault(r => r.Classes.Contains("current")) ?? (rows.Count > 0 ? rows[0] : null);
                if (start is not null)
                {
                    // Focusing scrolls it into view. From the keyboard the focus ring shows where the arrows start;
                    // from a click only the hover tint does.
                    start.Focus(keyboard ? NavigationMethod.Directional : NavigationMethod.Unspecified);
                }
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The popup gives the focus back to what had it before it opened — the switcher (a click on it focuses it too) —
    /// however it closes: Esc, a choice, or a click outside.
    /// </summary>
    private void OnMenuClosed(object? sender, EventArgs e) => WorkspaceMenuCard.Classes.Set("shown", false);

    /// <summary>Up/Down move between rows (wrapping), Home/End go to the first/last, Tab stays in the menu, Esc closes it.</summary>
    private void OnMenuKeyDown(object? sender, KeyEventArgs e)
    {
        if (_shell is null)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            _shell.IsWorkspaceMenuOpen = false;
            e.Handled = true;
            return;
        }

        var rows = MenuRows;
        if (rows.Count == 0)
        {
            return;
        }

        var focused = TopLevel.GetTopLevel(WorkspaceMenuCard)?.FocusManager?.GetFocusedElement();
        var at = focused is Button b ? rows.ToList().IndexOf(b) : -1;
        int? target = e.Key switch
        {
            Key.Down => at < 0 ? 0 : (at + 1) % rows.Count,
            Key.Up => at < 0 ? rows.Count - 1 : (at - 1 + rows.Count) % rows.Count,
            Key.Tab when e.KeyModifiers == KeyModifiers.Shift => at < 0 ? rows.Count - 1 : (at - 1 + rows.Count) % rows.Count,
            Key.Tab when e.KeyModifiers == KeyModifiers.None => at < 0 ? 0 : (at + 1) % rows.Count,
            Key.Home => 0,
            Key.End => rows.Count - 1,
            _ => null,
        };
        if (target is { } index)
        {
            // Focusing scrolls the row into view when the list is long.
            rows[index].Focus(NavigationMethod.Directional);
            e.Handled = true;
        }
    }

    // ── The new-workspace form ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Enter creates, Esc cancels and returns to the switcher; both stop here.</summary>
    private void OnNewWorkspaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (_shell is null || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (_shell.NewWorkspace.SubmitCommand.CanExecute(null))
            {
                _shell.NewWorkspace.SubmitCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _shell.CancelCreateWorkspaceCommand.Execute(null);
            WorkspacePicker.Focus(NavigationMethod.Directional);
            e.Handled = true;
        }
    }

    // ── The badge ────────────────────────────────────────────────────────────────────────────────────────────────────

    private void OnNewsCountRose(object? sender, EventArgs e) => Pulse();

    /// <summary>
    /// One brief swell and fade of the badge from its centre: the "pulsing" class runs the style's animation once
    /// (Sidebar.axaml) and is taken off after it. While one runs, a further increase only lengthens it.
    /// </summary>
    private void Pulse()
    {
        var pulse = ++_pulses;
        OtherWorkspacesNews.Classes.Set("pulsing", true);
        _ = Task.Delay(ShellView.PulseDuration).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (pulse == _pulses)
                {
                    OtherWorkspacesNews.Classes.Set("pulsing", false);
                }
            }),
            TaskScheduler.Default);
    }
}
