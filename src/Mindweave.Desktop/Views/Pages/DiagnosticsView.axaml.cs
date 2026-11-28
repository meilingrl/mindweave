using Avalonia;
using Avalonia.Controls;

namespace Mindweave.Desktop.Views.Pages;

public sealed partial class DiagnosticsView : UserControl
{
    public DiagnosticsView() => InitializeComponent();

    /// <summary>Shown again (back from another page): the requests are read again, so one made meanwhile is there.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as ViewModels.Pages.DiagnosticsViewModel)?.Refresh();
    }
}
