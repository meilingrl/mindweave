using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Mindweave.Desktop.ViewModels.Conversations;

namespace Mindweave.Desktop.Views.Conversations;

/// <summary>
/// The attachment viewer of the current page, shown by the shell over the whole window as the other dialogs are
/// (requirements §10.4.3). Escape, ✕ or a click on the dimmed window closes it.
/// </summary>
public sealed partial class AttachmentViewerView : UserControl
{
    public AttachmentViewerView()
    {
        InitializeComponent();
        IsVisible = false;
        ViewerCopyButton.Click += OnCopy;
        ViewerScrim.PointerPressed += (_, e) =>
        {
            if (e.Source == ViewerScrim && DataContext is AttachmentViewer viewer)
            {
                viewer.Close();
            }
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is AttachmentViewer { IsOpen: true } viewer)
            {
                viewer.Close();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        ViewerScrim.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && ViewerScrim.IsVisible)
            {
                ViewerCloseButton.Focus();
            }
        };
    }

    private AttachmentViewer? _viewer;

    /// <summary>Closed (or on a page without a viewer), the whole control is gone, so it never takes a click meant for the page.</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewer is not null)
        {
            _viewer.PropertyChanged -= OnViewerChanged;
        }

        _viewer = DataContext as AttachmentViewer;
        if (_viewer is not null)
        {
            _viewer.PropertyChanged += OnViewerChanged;
        }

        IsVisible = _viewer?.IsOpen == true;
        base.OnDataContextChanged(e);
    }

    private void OnViewerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AttachmentViewer.IsOpen))
        {
            IsVisible = _viewer?.IsOpen == true;
        }
    }

    /// <summary>The viewer of a page that has one (a conversation, 新探索), else null.</summary>
    public static FuncValueConverter<object?, AttachmentViewer?> ViewerOf { get; } = new(page => (page as IAttachmentViewerHost)?.AttachmentViewer);

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AttachmentViewer { Text: { } text } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
