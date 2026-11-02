using Avalonia.Controls;

namespace Mindweave.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private ViewModels.WorkspaceDeletionViewModel? _deletion;

    public MainWindow() => InitializeComponent();

    /// <summary>The delete-a-workspace dialog puts the caret in its name box when it opens (Round 4 · topic-delete).</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_deletion is not null)
        {
            _deletion.PropertyChanged -= OnDeletionChanged;
        }

        _deletion = (DataContext as ViewModels.RootViewModel)?.WorkspaceDeletion;
        if (_deletion is not null)
        {
            _deletion.PropertyChanged += OnDeletionChanged;
        }
    }

    private void OnDeletionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.WorkspaceDeletionViewModel.IsOpen) && _deletion?.IsOpen == true)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => WorkspaceDeleteName.Focus());
        }
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
