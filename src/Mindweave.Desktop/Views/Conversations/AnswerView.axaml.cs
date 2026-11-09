using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
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
    /// <summary>Space between the selection and its floating action.</summary>
    internal const double ActionGap = 6;

    private const string HighlightResource = "SourceHighlight";

    private readonly MarkdownRenderer _renderer = new();
    private MessageItemViewModel? _item;
    private ObservableStringBuilder? _live;
    private string _shown = string.Empty;
    private ScrollViewer? _scroller;
    private TopLevel? _topLevel;
    private ConversationView? _conversation;

    /// <summary>
    /// What the reader selected in this streaming answer, as text per block. Re-rendering can shift the displayed text under
    /// unchanged offsets (a closing emphasis drops its markers), so after each layout the selection is put back on this
    /// text (<see cref="KeepSelectionOnItsText"/>), and it is what is handed over when the answer ends.
    /// </summary>
    private IReadOnlyList<CarriedSegment>? _chosen;

    public AnswerView()
    {
        InitializeComponent();
        BodyHost.Content = _renderer;
        _renderer.LinkClick += (_, e) => e.Handled = true;
        // A new press or key starts a new selection gesture: the chosen text is set again when it ends.
        _renderer.AddHandler(PointerPressedEvent, (_, _) => _chosen = null, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        _renderer.AddHandler(KeyDownEvent, (_, _) => _chosen = null, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        _renderer.AddHandler(PointerReleasedEvent, (_, _) => OnSelectionGestureEnded(), RoutingStrategies.Bubble, handledEventsToo: true);
        _renderer.AddHandler(KeyUpEvent, (_, _) => OnSelectionGestureEnded(), RoutingStrategies.Bubble, handledEventsToo: true);

        // The floating action (S7 round 3): a small raised card with the branch icon and 创建分支. It lives in the
        // nearest overlay layer while a selection is shown (see ShowSelectionActions), never inside the answer. In the
        // main window that is the page's own layer, under the window's dialog scrims, so a dialog covers it too.
        var icon = new PathIcon();
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable("IconBranch"));
        BranchButton = new Button
        {
            Name = nameof(BranchButton),
            Classes = { "quiet" },
            Content = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6,
                Children = { icon, new TextBlock { Text = UiText.CreateBranch, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } },
            },
        };
        ToolTip.SetShowOnDisabled(BranchButton, true);
        AutomationProperties.SetName(BranchButton, UiText.CreateBranch);
        BranchButton.Click += (_, _) => _ = CreateBranchAsync();
        SelectionActions = new Border { Name = nameof(SelectionActions), Classes = { "selection-actions" }, Child = BranchButton, IsVisible = false };

        // The action follows the selection when the page scrolls or resizes (raised inside the layout pass, after the
        // answer has been arranged at its new place) and when the answer is laid out again (a narrower window rewraps it).
        EffectiveViewportChanged += (_, _) => PlaceSelectionActions();
        ActualThemeVariantChanged += OnThemeChanged;
        _renderer.SizeChanged += (_, _) => PlaceSelectionActions();
        _renderer.LayoutUpdated += (_, _) => KeepSelectionOnItsText();
    }

    /// <summary>The floating card next to the selection; in the nearest overlay layer only while it is shown.</summary>
    internal Border SelectionActions { get; }

    /// <summary>创建分支 on the floating card.</summary>
    internal Button BranchButton { get; }

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
            var resolved = SourceAnchors.Restore(SourceAnchors.Deserialize(source.AnchorJson), _shown, _renderer, HighlightBrush());
            return SourceAnchors.TopOf(resolved[0], relativeTo);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The highlight colour of the theme this answer is shown in. A lookup without the theme finds nothing in the
    /// theme dictionaries (the fallback used to be a bright khaki, unreadable under dark text colours, S7 round 4).
    /// </summary>
    private IBrush HighlightBrush() =>
        this.TryFindResource(HighlightResource, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Transparent;

    /// <summary>A theme switch while a source passage is highlighted recolours the highlight in place.</summary>
    private void OnThemeChanged(object? sender, EventArgs e) => SourceAnchors.RecolorHighlight(_renderer, HighlightBrush());

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_item is not null)
        {
            _item.PropertyChanged -= OnItemChanged;
            HandOverSelection();
        }

        // The message template hosts this view for questions too (hidden); only answers are rendered.
        _item = DataContext is MessageItemViewModel { IsAnswer: true } answer ? answer : null;
        _live = null;
        _shown = string.Empty;
        if (_item is not null)
        {
            _item.PropertyChanged += OnItemChanged;
            Show(_item.Body);
            TakeOverSelection();
        }

        UpdateBranchButton();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_item is not null)
        {
            _item.PropertyChanged -= OnItemChanged;
        }

        // Leaving the page (or the answer being removed) takes the floating card with it.
        HandOverSelection();
        HideSelectionActions();
        _scroller = null;
        _conversation = null;
        _topLevel?.RemoveHandler(GotFocusEvent, OnFocusMoved);
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroller = this.FindAncestorOfType<ScrollViewer>();
        _conversation = this.FindAncestorOfType<ConversationView>();
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(GotFocusEvent, OnFocusMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        if (_item is not null)
        {
            _item.PropertyChanged -= OnItemChanged;
            _item.PropertyChanged += OnItemChanged;
            Show(_item.Body);
            TakeOverSelection();
        }
    }

    /// <summary>
    /// A streaming answer's row is replaced by a new row for its stored text when it ends (its view switches from the
    /// live builder to a full parse). Text can be selected and copied while it streams, so the selection is handed
    /// over to the page here, as this view leaves, and taken over by the stored answer's view (<see cref="TakeOverSelection"/>).
    /// </summary>
    private void HandOverSelection()
    {
        // The chosen text, not the current offsets: the last update may have shifted the text before the layout pass.
        if (_item is { IsLive: true } live && _conversation is { } page && (_chosen ?? SelectionCarry.Capture(_renderer)) is { } selection)
        {
            var focused = _topLevel?.FocusManager?.GetFocusedElement() is Visual focus && (focus == _renderer || _renderer.IsVisualAncestorOf(focus));
            page.HandOverSelection(live.Id, new CarriedSelection(selection, focused));
        }
    }

    private void TakeOverSelection()
    {
        if (_item is { IsLive: false } stored && _conversation?.TakeOverSelection(stored.Id) is { } selection && SelectionCarry.Apply(_renderer, selection.Segments))
        {
            // Focus went with the old view; take it back (once this view's controls are attached and can hold it) unless
            // something else has it since, so the copy gesture still copies the selection.
            if (selection.Focused)
            {
                Dispatcher.UIThread.Post(RestoreFocus, DispatcherPriority.Loaded);
            }

            UpdateBranchButton();
        }
    }

    private void RestoreFocus()
    {
        if (_topLevel is { FocusManager: { } focus } top && focus.GetFocusedElement() is var focused && (focused is null || ReferenceEquals(focused, top))
            && SourceAnchors.HasSelection(_renderer))
        {
            _renderer.Focus();
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

    /// <summary>
    /// While the answer streams, keeps the selection on the text the reader chose: if a re-render shifted the displayed text,
    /// the selection moves to where that text now is, or is cleared when it is no longer shown.
    /// </summary>
    private void KeepSelectionOnItsText()
    {
        if (_chosen is not { } chosen || _item is not { IsLive: true })
        {
            return;
        }

        if (SelectionCarry.Same(SelectionCarry.Capture(_renderer), chosen))
        {
            return;
        }

        if (!SelectionCarry.Apply(_renderer, chosen))
        {
            _chosen = null;
            ClearSelection();
            return;
        }

        _chosen = SelectionCarry.Capture(_renderer);
        UpdateBranchButton();
    }

    /// <summary>Clears this answer's selection, and with it the floating action.</summary>
    internal void ClearSelection()
    {
        _chosen = null;
        foreach (var block in SourceAnchors.Blocks(_renderer))
        {
            block.SelectionStart = 0;
            block.SelectionEnd = 0;
        }

        UpdateBranchButton();
    }

    /// <summary>A selection made here replaces any selection in another answer: one floating action on the page at a time.</summary>
    private void OnSelectionGestureEnded()
    {
        _chosen = _item is { IsLive: true } ? SelectionCarry.Capture(_renderer) : null;
        if (SourceAnchors.HasSelection(_renderer) && this.FindAncestorOfType<ConversationView>() is { } page)
        {
            foreach (var other in page.GetVisualDescendants().OfType<AnswerView>().Where(a => a != this))
            {
                other.ClearSelection();
            }
        }

        UpdateBranchButton();
    }

    /// <summary>Typing somewhere else (the composer, a rename box) ends the selection, so no stale action floats over earlier answers.</summary>
    private void OnFocusMoved(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is TextBox && SourceAnchors.HasSelection(_renderer))
        {
            ClearSelection();
        }
    }

    private void UpdateBranchButton()
    {
        // While storage is paused the entry stays in sight, disabled, and says why (§9.3); the selection is kept for copying.
        var paused = _item is { IsBranchPaused: true };
        BranchButton.IsEnabled = !paused;
        ToolTip.SetTip(BranchButton, paused ? UiText.StoragePausedReason : null);

        if (_item is { CanBranch: true } && SourceAnchors.HasSelection(_renderer))
        {
            ShowSelectionActions();
        }
        else
        {
            HideSelectionActions();
        }
    }

    private void ShowSelectionActions()
    {
        if (SelectionActions.Parent is null && OverlayLayer.GetOverlayLayer(this) is { } layer)
        {
            layer.Children.Add(SelectionActions);
        }

        PlaceSelectionActions();
    }

    private void HideSelectionActions()
    {
        SelectionActions.IsVisible = false;
        if (SelectionActions.Parent is OverlayLayer layer)
        {
            layer.Children.Remove(SelectionActions);
        }
    }

    /// <summary>
    /// Puts the floating action just above the selection's first visible line, starting at its left edge (kept inside
    /// the answer); below its last visible line when above would leave the page's visible area; at the top of that area
    /// when the selection fills it. Hidden, but kept, while the selection is scrolled out of sight.
    /// </summary>
    private void PlaceSelectionActions()
    {
        if (SelectionActions.Parent is not OverlayLayer layer || this.TranslatePoint(default, layer) is not { } origin)
        {
            return;
        }

        var viewport = _scroller?.TranslatePoint(default, layer) is { } corner ? new Rect(corner, _scroller.Viewport) : new Rect(layer.Bounds.Size);
        var lines = SourceAnchors.SelectionRects(_renderer, layer).Where(l => l.Bottom > viewport.Top && l.Top < viewport.Bottom).ToArray();
        SelectionActions.IsVisible = lines.Length > 0;
        if (lines.Length == 0)
        {
            return;
        }

        SelectionActions.Measure(Size.Infinity);
        var size = SelectionActions.DesiredSize;
        var left = Math.Max(origin.X, viewport.Left);
        var right = Math.Min(origin.X + Bounds.Width, viewport.Right);
        var x = Math.Clamp(lines[0].X, left, Math.Max(left, right - size.Width));
        var y = lines[0].Y - size.Height - ActionGap;
        if (y < viewport.Top)
        {
            y = lines[^1].Bottom + ActionGap;
            if (y + size.Height > viewport.Bottom)
            {
                y = viewport.Top + ActionGap;
            }
        }

        Canvas.SetLeft(SelectionActions, x);
        Canvas.SetTop(SelectionActions, y);
    }

    private async Task CreateBranchAsync()
    {
        if (CaptureSelection() is { } selection && this.FindAncestorOfType<ConversationView>()?.DataContext is ConversationViewModel page)
        {
            HideSelectionActions();
            await page.CreateBranchAsync(selection);
        }
    }
}
