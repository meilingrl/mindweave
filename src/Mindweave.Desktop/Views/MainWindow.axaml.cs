using Avalonia.Controls;

namespace Mindweave.Desktop.Views;

/// <summary>
/// The window: closing, coming forward, size and position. What it shows is a <see cref="RootView"/>, replaced by a new
/// one whenever the session's view is replaced (a language switch, an in-app restore), so the window itself stays.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Content = new RootView();
    }

    /// <summary>
    /// Shows whichever view is current: a language switch replaces the view, and an in-app restore (2026-11-12) the
    /// services too, and with them the root view model this window shows and closes through.
    /// </summary>
    internal void Follow(AppSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.ViewReplaced += (_, _) => ShowView(session.RootViewModel);
    }

    /// <summary>
    /// A new root view for <paramref name="root"/>. The old one leaves the window first, with the old view model, so its
    /// pages save what they keep on leaving and nothing of the old view is built for the new view model.
    /// </summary>
    private void ShowView(ViewModels.RootViewModel root)
    {
        Content = null;
        DataContext = root;
        Content = new RootView();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        // Closing the window is one way of quitting: the exit coordinator asks, stops and saves, then shuts down
        // itself; only that final shutdown passes here.
        if (DataContext is ViewModels.RootViewModel root && root.Exit.InterceptClose())
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Called when a second launch asks this instance to come forward. Ignored once the exit is releasing: the window
    /// is closing or closed, and this instance only still holds the single-instance lock while it shuts down.
    /// </summary>
    public void BringToFront()
    {
        if (DataContext is ViewModels.RootViewModel { Exit.Stage: ViewModels.ExitStage.Releasing })
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
    }
}
