using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Mindweave.Desktop.ViewModels.Conversations;

namespace Mindweave.Desktop.Views.Conversations;

public sealed partial class ComposerView : UserControl
{
    private Composer? _composer;

    public ComposerView()
    {
        InitializeComponent();

        // Enter sends, Shift+Enter inserts a newline. Text composition (IME) consumes its own Enter first.
        Input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        // Pasting (requirements §10.4.3): files and images become attachments, a long text a 粘贴的文字 attachment.
        Input.AddHandler(TextBox.PastingFromClipboardEvent, OnPasting, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AttachFileItem.Click += async (_, _) => await PickAsync(images: false);
        AttachImageItem.Click += async (_, _) => await PickAsync(images: true);
        ComposerBox.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        ComposerBox.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ComposerBox.Classes.Set("drop-target", false));
        ComposerBox.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>Raised when the "go to settings" link in a notice is clicked; the owning page handles it.</summary>
    public event EventHandler? SettingsRequested;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        OpenSettingsFromNotice.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_composer is not null)
        {
            _composer.FillRequested -= OnFillRequested;
            _composer.TextInserted -= OnTextInserted;
        }

        _composer = DataContext as Composer;
        if (_composer is not null)
        {
            _composer.FillRequested += OnFillRequested;
            _composer.TextInserted += OnTextInserted;
        }

        base.OnDataContextChanged(e);
    }

    /// <summary>A recommended question was put in: the input takes the focus, the caret after the text, nothing selected.</summary>
    private void OnFillRequested(object? sender, EventArgs e)
    {
        Input.Focus(NavigationMethod.Pointer);
        Input.ClearSelection();
        Input.CaretIndex = Input.Text?.Length ?? 0;
    }

    /// <summary>转为文字 put the paste back: the caret goes after it.</summary>
    private void OnTextInserted(object? sender, int caret)
    {
        Input.Focus(NavigationMethod.Pointer);
        Input.ClearSelection();
        Input.CaretIndex = Math.Min(caret, Input.Text?.Length ?? 0);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not Composer composer)
        {
            return;
        }

        // Undo right after a long paste became an attachment brings the text back (requirements §10.4.3).
        if (e.Key == Key.Z && e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta && composer.UndoConversion())
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        e.Handled = true;
        if (composer.SendCommand.CanExecute(null))
        {
            composer.SendCommand.Execute(null);
        }
    }

    private async void OnPasting(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not Composer { CanAttach: true } composer || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        // Decided before the clipboard is read (the event cannot wait): the text box pastes only what this lets through.
        e.Handled = true;
        try
        {
            var files = await clipboard.TryGetFilesAsync();
            if (files is { Length: > 0 } && files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() is { Count: > 0 } paths)
            {
                await composer.AttachFilesAsync(paths);
                return;
            }

            var text = await clipboard.TryGetTextAsync();
            if (string.IsNullOrEmpty(text))
            {
                using var bitmap = await clipboard.TryGetBitmapAsync();
                if (bitmap is not null)
                {
                    await composer.AttachImageAsync(Encode(bitmap), null);
                }

                return;
            }

            if (composer.TryTakePaste(text, Math.Min(Input.SelectionStart, Input.SelectionEnd)))
            {
                return;
            }

            // A short text is pasted as usual, replacing any selection.
            var start = Math.Min(Input.SelectionStart, Input.SelectionEnd);
            var end = Math.Max(Input.SelectionStart, Input.SelectionEnd);
            var current = Input.Text ?? string.Empty;
            Input.Text = current[..start] + text + current[end..];
            Input.CaretIndex = start + text.Length;
        }
#pragma warning disable CA1031 // A clipboard that cannot be read pastes nothing; the input stays as it was.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static byte[] Encode(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    private async Task PickAsync(bool images)
    {
        if (DataContext is not Composer composer || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var options = new FilePickerOpenOptions
        {
            Title = images ? UiText.ImageDialogTitle : UiText.FileDialogTitle,
            AllowMultiple = true,
            FileTypeFilter = images
                ? [new FilePickerFileType(UiText.ImagesFileFilter) { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp"] }]
                : null,
        };
        var picked = await storage.OpenFilePickerAsync(options);
        var paths = picked.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
        {
            await composer.AttachFilesAsync(paths);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var accepts = DataContext is Composer { CanAttach: true } && (e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.Contains(DataFormat.Text));
        e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        ComposerBox.Classes.Set("drop-target", accepts);
    }

    /// <summary>Dropped files become attachments; dropped text goes in the input, or becomes an attachment when long.</summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        ComposerBox.Classes.Set("drop-target", false);
        if (DataContext is not Composer { CanAttach: true } composer)
        {
            return;
        }

        e.Handled = true;
        if (e.DataTransfer.TryGetFiles() is { Length: > 0 } files)
        {
            await composer.AttachFilesAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>());
        }
        else if (e.DataTransfer.TryGetText() is { Length: > 0 } text && !composer.TryTakePaste(text, Input.CaretIndex))
        {
            var current = Input.Text ?? string.Empty;
            var at = Math.Clamp(Input.CaretIndex, 0, current.Length);
            Input.Text = current.Insert(at, text);
        }
    }
}
