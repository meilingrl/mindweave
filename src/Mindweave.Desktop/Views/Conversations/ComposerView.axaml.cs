using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Mindweave.Desktop.ViewModels.Conversations;

namespace Mindweave.Desktop.Views.Conversations;

public sealed partial class ComposerView : UserControl
{
    public ComposerView()
    {
        InitializeComponent();

        // Enter sends, Shift+Enter inserts a newline. Text composition (IME) consumes its own Enter first.
        Input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Raised when the "go to settings" link in a notice is clicked; the owning page handles it.</summary>
    public event EventHandler? SettingsRequested;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        OpenSettingsFromNotice.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || DataContext is not Composer composer)
        {
            return;
        }

        e.Handled = true;
        if (composer.SendCommand.CanExecute(null))
        {
            composer.SendCommand.Execute(null);
        }
    }
}
