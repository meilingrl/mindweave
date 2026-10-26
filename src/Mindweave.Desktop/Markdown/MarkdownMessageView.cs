using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Mindweave.Core.Branches;

namespace Mindweave.Desktop.Markdown;

/// <summary>One native Markdown renderer per message; exposes stable identity for chat navigation.</summary>
public sealed class MarkdownMessageView : UserControl
{
    public static readonly StyledProperty<string> BodyProperty = AvaloniaProperty.Register<MarkdownMessageView, string>(nameof(Body), "");
    public static readonly StyledProperty<Guid> MessageIdProperty = AvaloniaProperty.Register<MarkdownMessageView, Guid>(nameof(MessageId));
    public static readonly StyledProperty<long> RevisionProperty = AvaloniaProperty.Register<MarkdownMessageView, long>(nameof(Revision));
    public string Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public Guid MessageId { get => GetValue(MessageIdProperty); set => SetValue(MessageIdProperty, value); }
    public long Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public MarkdownRenderer Renderer { get; } = new();
    public string? PreservationFailure { get; private set; }
    public event EventHandler? PreservationChanged;
    private readonly TextBlock preservationStatus = new() { IsVisible = false, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 11, Foreground = Avalonia.Media.Brush.Parse("#9A4122") };
    private string renderedBody = "";
    private ScrollViewer? scroll;
    private ReadingLocation? reading;
    private SourceSelection? pendingSelection;
    private bool restoring;
    private long updateVersion;

    public MarkdownMessageView()
    {
        Content = new StackPanel { Spacing = 6, Children = { Renderer, preservationStatus } };
        // Links remain selectable text; opening an external resource requires a future explicit product action.
        Renderer.LinkClick += (_, args) => args.Handled = true;
        SizeChanged += (_, args) =>
        {
            if (restoring || args.PreviousSize.Width == args.NewSize.Width || scroll is null || reading is null) return;
            PreserveAfterLayout(null, reading, updateVersion);
        };
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        scroll = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is not null) { scroll.ScrollChanged += OnScrollChanged; reading = CaptureReading(); }
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (scroll is not null) scroll.ScrollChanged -= OnScrollChanged;
        scroll = null; base.OnDetachedFromVisualTree(e);
    }
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs args)
    {
        if (!restoring) reading = CaptureReading();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != BodyProperty) return;
        SourceSelection? selection = pendingSelection;
        PreservationFailure = null;
        try { if (MarkdownSelectionAdapter.Blocks(Renderer).Any(b => b.SelectionStart != b.SelectionEnd)) selection = MarkdownSelectionAdapter.Capture(MessageId, Revision, renderedBody, Renderer); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { PreservationFailure = error.Message; }
        pendingSelection = selection;
        var pin = CaptureReading();
        renderedBody = Body ?? "";
        var document = Markdig.Markdown.Parse(renderedBody, MarkdownUpdateProducer.DefaultPipeline);
        // Remove resource nodes before any renderer sees them: no image loader, file read, or HTTP request.
        foreach (var image in document.Descendants<LinkInline>().Where(link => link.IsImage).ToArray())
            image.ReplaceBy(new LiteralInline("［图片暂不支持］") { Span = image.Span });
        // Full updates may reuse text controls and retain raw selection indices. Clear the old
        // presentation before changing its source, then restore only after envelope validation.
        Renderer.ClearSelection();
        foreach (var block in MarkdownSelectionAdapter.Blocks(Renderer)) block.Highlights.Remove("saved-source");
        Renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(document);
        var version = ++updateVersion;
        PreserveAfterLayout(selection, pin, version);
    }
    private void PreserveAfterLayout(SourceSelection? selection, ReadingLocation? pin, long version)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (version != updateVersion) return;
            restoring = true;
            try
            {
                Renderer.UpdateLayout();
                if (selection is not null)
                {
                    try { MarkdownSelectionAdapter.Restore(selection, renderedBody, Renderer); }
                    catch (InvalidDataException error) { PreservationFailure = error.Message; }
                    pendingSelection = null;
                }
                if (pin is not null && scroll is not null && !MarkdownSelectionAdapter.RestoreReading(pin, MessageId, Renderer, scroll))
                    PreservationFailure ??= "阅读区块已改变，需回到消息。";
                reading = CaptureReading();
                preservationStatus.Text = PreservationFailure is null ? "" : "无法精确定位原句。" + PreservationFailure;
                preservationStatus.IsVisible = PreservationFailure is not null;
                PreservationChanged?.Invoke(this, EventArgs.Empty);
            }
            finally { restoring = false; }
        }, DispatcherPriority.Loaded);
    }
    public SourceSelection CaptureSelection() => MarkdownSelectionAdapter.Capture(MessageId, Revision, renderedBody, Renderer);
    public IReadOnlyList<MarkdownFragment> RestoreSelection(SourceSelection selection)
    {
        if (selection.MessageId != MessageId) throw new InvalidDataException("来源消息身份不匹配。");
        return MarkdownSelectionAdapter.Restore(selection, renderedBody, Renderer);
    }
    public ReadingLocation? CaptureReading() => scroll is null ? null : MarkdownSelectionAdapter.CaptureReading(MessageId, Renderer, scroll);
    public bool RestoreReading(ReadingLocation location) => scroll is not null && MarkdownSelectionAdapter.RestoreReading(location, MessageId, Renderer, scroll);
}
