using Avalonia.Controls;

namespace Mindweave.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

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

    /// <summary>Called when a second launch asks this instance to come forward.</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
    }
}
