using Avalonia;
using Avalonia.Controls;

namespace Mindweave.Desktop.Views.Pages;

public sealed partial class StorageDetailsView : UserControl
{
    public StorageDetailsView() => InitializeComponent();

    /// <summary>
    /// Shown again (back from another section or page): the backups are read again, so one made meanwhile (before a
    /// delete, say) is there (2026-11-12). The first showing reads them when the page is made.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as ViewModels.Pages.StorageDetailsViewModel)?.Refresh();
    }
}
