using Avalonia.Controls;
using Avalonia.Interactivity;
using Mindweave.Desktop.ViewModels.Conversations;

namespace Mindweave.Desktop.Views.Conversations;

public sealed partial class ConversationView : UserControl
{
    private bool _followBottom = true;

    public ConversationView()
    {
        InitializeComponent();

        // Follow the newest text only while the reader is already at the bottom; never yank them back
        // from something they scrolled up to read. (Persistent reading position is S2.)
        Scroller.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta.Y != 0 && _followBottom)
            {
                Scroller.ScrollToEnd();
            }
            else if (e.OffsetDelta.Y != 0)
            {
                _followBottom = Scroller.Offset.Y + Scroller.Viewport.Height >= Scroller.Extent.Height - 24;
            }
        };
        ComposerHost.SettingsRequested += (_, _) => (DataContext as ConversationViewModel)?.OpenSettingsCommand.Execute(null);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Scroller.ScrollToEnd();
    }
}
