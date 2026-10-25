using Avalonia.Controls;
using Avalonia.Interactivity;
using Mindweave.Desktop.ViewModels.Conversations;

namespace Mindweave.Desktop.Views.Conversations;

public sealed partial class NewConversationView : UserControl
{
    public NewConversationView()
    {
        InitializeComponent();
        ComposerHost.SettingsRequested += (_, _) => (DataContext as NewConversationViewModel)?.OpenSettingsCommand.Execute(null);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ComposerHost.FindControl<TextBox>("Input")?.Focus();
    }
}
