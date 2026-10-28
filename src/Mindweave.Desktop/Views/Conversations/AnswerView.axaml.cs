using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Mindweave.Core.Branches;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.ViewModels.Conversations;

namespace Mindweave.Desktop.Views.Conversations;

/// <summary>
/// One answer rendered as Markdown. A finished answer is parsed once in full (deterministic layout, so source
/// anchors resolve the same way after a restart); a streaming answer feeds LiveMarkdown's live builder, which
/// only appends. The view turns a native selection into a <see cref="BranchSelection"/> and back.
/// </summary>
public sealed partial class AnswerView : UserControl
{
    private readonly MarkdownRenderer _renderer = new();
    private MessageItemViewModel? _item;
    private ObservableStringBuilder? _live;
    private string _shown = string.Empty;

    public AnswerView()
    {
        InitializeComponent();
        BodyHost.Content = _renderer;
        _renderer.LinkClick += (_, e) => e.Handled = true;
        _renderer.AddHandler(PointerReleasedEvent, (_, _) => UpdateBranchButton(), RoutingStrategies.Bubble, handledEventsToo: true);
        _renderer.AddHandler(KeyUpEvent, (_, _) => UpdateBranchButton(), RoutingStrategies.Bubble, handledEventsToo: true);
        BranchButton.Click += (_, _) => _ = CreateBranchAsync();
    }

    /// <summary>The Markdown the renderer shows now; anchors are relative to exactly this text.</summary>
    internal string Markdown => _shown;

    internal MarkdownRenderer Renderer => _renderer;

    /// <summary>False while a streamed update is still being laid out; positions measured then are not trustworthy.</summary>
    internal bool IsLayoutCurrent => _live is null || _renderer.DocumentUpdate?.Version == _live.Version;

    /// <summary>The selection as a branch request, or null when nothing is selected or the answer cannot be branched from.</summary>
    internal BranchSelection? CaptureSelection()
    {
        if (_item is not { CanBranch: true } item || !IsLayoutCurrent || !SourceAnchors.HasSelection(_renderer))
        {
            return null;
        }

        try
        {
            var anchor = SourceAnchors.Capture(_shown, _renderer);
            return new BranchSelection(item.Id, 0, SourceAnchors.QuoteOf(anchor), SourceAnchors.Serialize(anchor), SourceAnchors.FormatVersion, SourceAnchors.RenderProfile);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Selects and highlights a branch's source; returns its top relative to <paramref name="relativeTo"/>, or null if it cannot be located exactly.</summary>
    internal double? RevealSource(BranchSource source, Visual relativeTo)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.AnchorFormatVersion != SourceAnchors.FormatVersion || !SourceAnchors.CompatibleProfiles.Contains(source.RenderProfile) || source.SourceBlock != 0)
        {
            return null;
        }

        try
        {
            var brush = this.FindResource("SourceHighlight") as IBrush ?? Brushes.Khaki;
            var resolved = SourceAnchors.Restore(SourceAnchors.Deserialize(source.AnchorJson), _shown, _renderer, brush);
            return SourceAnchors.TopOf(resolved[0], relativeTo);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        {
            return null;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_item is not null)
        {
            _item.PropertyChanged -= OnItemChanged;
        }

        // The message template hosts this view for questions too (hidden); only answers are rendered.
        _item = DataContext is MessageItemViewModel { IsAnswer: true } answer ? answer : null;
        _live = null;
        _shown = string.Empty;
        if (_item is not null)
        {
            _item.PropertyChanged += OnItemChanged;
            Show(_item.Body);
        }

        UpdateBranchButton();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_item is not null)
        {
            _item.PropertyChanged -= OnItemChanged;
        }

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_item is not null)
        {
            _item.PropertyChanged -= OnItemChanged;
            _item.PropertyChanged += OnItemChanged;
            Show(_item.Body);
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MessageItemViewModel.Body))
        {
            Show(_item!.Body);
        }
        else if (e.PropertyName is nameof(MessageItemViewModel.CanBranch) or nameof(MessageItemViewModel.IsBranchPaused))
        {
            UpdateBranchButton();
        }
    }

    private void Show(string markdown)
    {
        if (markdown == _shown && (_live is not null || _renderer.DocumentUpdate is not null))
        {
            return;
        }

        if (_item is { CanBranch: false, IsInProgress: true } && (_live is null || markdown.StartsWith(_shown, StringComparison.Ordinal)))
        {
            // Streaming only ever appends; the builder re-parses in the background and updates controls incrementally.
            if (_live is null)
            {
                _live = new ObservableStringBuilder();
                _renderer.MarkdownBuilder = _live;
                _shown = string.Empty;
            }

            _live.Append(markdown[_shown.Length..]);
        }
        else
        {
            _live = null;
            _renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdig.Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline));
        }

        _shown = markdown;
    }

    private void UpdateBranchButton()
    {
        BranchButton.IsVisible = _item is { CanBranch: true } && SourceAnchors.HasSelection(_renderer);

        // While storage is paused the entry stays in sight, disabled, and says why (§9.3); the selection is kept for copying.
        var paused = _item is { IsBranchPaused: true };
        BranchButton.IsEnabled = !paused;
        ToolTip.SetTip(BranchButton, paused ? UiText.StoragePausedReason : null);
    }

    private async Task CreateBranchAsync()
    {
        if (CaptureSelection() is { } selection && this.FindAncestorOfType<ConversationView>()?.DataContext is ConversationViewModel page)
        {
            BranchButton.IsVisible = false;
            await page.CreateBranchAsync(selection);
        }
    }
}
