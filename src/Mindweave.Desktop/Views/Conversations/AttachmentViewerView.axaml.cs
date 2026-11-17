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
            _viewer.OpenRequested -= OnOpenRequested;
        }

        _viewer = DataContext as AttachmentViewer;
        if (_viewer is not null)
        {
            _viewer.PropertyChanged += OnViewerChanged;
            _viewer.OpenRequested += OnOpenRequested;
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
        else if (e.PropertyName is nameof(AttachmentViewer.Highlight) or nameof(AttachmentViewer.Text))
        {
            // After the text is laid out: select the cited passage and bring it into view.
            Avalonia.Threading.Dispatcher.UIThread.Post(ShowHighlight, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>The cited passage selected and scrolled to, a few lines below the top.</summary>
    private void ShowHighlight()
    {
        if (_viewer is not { Highlight: { } range, Text: { } text } || range.End > text.Length)
        {
            return;
        }

        ViewerText.SelectionStart = range.Start;
        ViewerText.SelectionEnd = range.End;
        var top = ViewerText.TextLayout.HitTestTextPosition(range.Start).Top;
        ViewerScroller.Offset = new Avalonia.Vector(0, Math.Max(0, top - 48));
    }

    /// <summary>The confirmed address goes to the system browser; nothing else is ever handed to it.</summary>
    private async void OnOpenRequested(object? sender, Uri url)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(url);
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
