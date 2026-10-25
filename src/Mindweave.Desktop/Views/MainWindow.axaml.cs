using Avalonia.Controls;

namespace Mindweave.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (DataContext is ViewModels.RootViewModel root && root.ExitPrompt.ShouldHoldClose())
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
