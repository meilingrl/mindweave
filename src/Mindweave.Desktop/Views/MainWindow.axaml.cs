using Avalonia.Controls;

namespace Mindweave.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

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
