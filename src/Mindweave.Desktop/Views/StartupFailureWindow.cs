using Avalonia.Controls;
using Avalonia.Layout;

namespace Mindweave.Desktop.Views;

/// <summary>
/// Shown instead of the main window when Mindweave cannot start before any page exists (health check 2026-11-12,
/// errors-6): what went wrong, in a selectable text, and a button to close. Built in code so it depends on nothing that
/// may have failed.
/// </summary>
internal sealed class StartupFailureWindow : Window
{
    public StartupFailureWindow(string message)
    {
        Title = UiText.StartupFailedTitle;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        var close = new Button { Name = "StartupFailureClose", Content = UiText.Exit, HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = UiText.StartupFailedTitle, FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new SelectableTextBlock { Name = "StartupFailureMessage", Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                close,
            },
        };
    }
}
