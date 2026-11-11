using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;

namespace Mindweave.Desktop.Views;

/// <summary>
/// A text box that offers a suggestion as dim "ghost" text right after what is typed, with a small key hint (2026-11-11;
/// first for a workspace's suggested names, later for recommended questions). Tab while a suggestion is offered puts it in
/// the box, the caret at the end, still editable and not saved; Tab again, while the box still holds the suggestion it put
/// there, offers and takes the next one, wrapping. Typing, pasting or deleting anything ends the offer until the next
/// <see cref="Suggestions"/>. With nothing offered Tab moves the focus as usual, and Shift+Tab always does. The offer is
/// announced through the help text (<see cref="OfferHelpFormat"/>); the ghost itself is drawn only, never a focus stop.
/// It never overlaps the text: it starts after it, is trimmed to the visible width, and is left out when there is no room.
/// </summary>
public class GhostTextBox : TextBox
{
    /// <summary>The candidates, best first. A new list (even an equal one) starts a new offer.</summary>
    public static readonly StyledProperty<IReadOnlyList<string>?> SuggestionsProperty =
        AvaloniaProperty.Register<GhostTextBox, IReadOnlyList<string>?>(nameof(Suggestions));

    /// <summary>The help text while nothing is offered.</summary>
    public static readonly StyledProperty<string?> IdleHelpTextProperty =
        AvaloniaProperty.Register<GhostTextBox, string?>(nameof(IdleHelpText));

    /// <summary>The help text while a suggestion is offered; <c>{0}</c> is the suggestion.</summary>
    public static readonly StyledProperty<string?> OfferHelpFormatProperty =
        AvaloniaProperty.Register<GhostTextBox, string?>(nameof(OfferHelpFormat));

    /// <summary>The key hint drawn after the ghost.</summary>
    public static readonly StyledProperty<string> KeyHintProperty =
        AvaloniaProperty.Register<GhostTextBox, string>(nameof(KeyHint), "Tab");

    public static readonly StyledProperty<IBrush?> GhostForegroundProperty =
        AvaloniaProperty.Register<GhostTextBox, IBrush?>(nameof(GhostForeground));

    public static readonly StyledProperty<IBrush?> KeyHintBorderBrushProperty =
        AvaloniaProperty.Register<GhostTextBox, IBrush?>(nameof(KeyHintBorderBrush));

    /// <summary>What Tab would put in the box now, or null.</summary>
    public static readonly DirectProperty<GhostTextBox, string?> OfferedProperty =
        AvaloniaProperty.RegisterDirect<GhostTextBox, string?>(nameof(Offered), o => o.Offered);

    private const double Gap = 8;
    private const double KeyGap = 6;

    private readonly GhostLayer _layer;
    private TextPresenter? _presenter;
    private string? _offered;
    private int _offerIndex = -1;
    private int _acceptedIndex = -1;
    private bool _dismissed;
    private bool _applying;

    public GhostTextBox()
    {
        _layer = new GhostLayer(this);
    }

    public IReadOnlyList<string>? Suggestions
    {
        get => GetValue(SuggestionsProperty);
        set => SetValue(SuggestionsProperty, value);
    }

    public string? IdleHelpText
    {
        get => GetValue(IdleHelpTextProperty);
        set => SetValue(IdleHelpTextProperty, value);
    }

    public string? OfferHelpFormat
    {
        get => GetValue(OfferHelpFormatProperty);
        set => SetValue(OfferHelpFormatProperty, value);
    }

    public string KeyHint
    {
        get => GetValue(KeyHintProperty);
        set => SetValue(KeyHintProperty, value);
    }

    public IBrush? GhostForeground
    {
        get => GetValue(GhostForegroundProperty);
        set => SetValue(GhostForegroundProperty, value);
    }

    public IBrush? KeyHintBorderBrush
    {
        get => GetValue(KeyHintBorderBrushProperty);
        set => SetValue(KeyHintBorderBrushProperty, value);
    }

    public string? Offered
    {
        get => _offered;
        private set => SetAndRaise(OfferedProperty, ref _offered, value);
    }

    /// <summary>The ghost is drawn now: something is offered, the box has the focus, and there is room after the text.</summary>
    public bool ShowsGhost => Place() is not null;

    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>Where the ghost and its key hint go, in this box's coordinates; null when nothing is drawn.</summary>
    internal GhostPlacement? Place()
    {
        if (Offered is not { } offered || !IsKeyboardFocusWithin || _presenter is not { IsEffectivelyVisible: true } presenter
            || presenter.TranslatePoint(default, this) is not { } origin)
        {
            return null;
        }

        // The visible part of the text area: the presenter scrolls inside it when the text is long.
        var viewer = presenter.FindAncestorOfType<ScrollViewer>() as Visual ?? presenter.GetVisualParent();
        if (viewer?.TranslatePoint(default, this) is not { } viewOrigin)
        {
            return null;
        }

        var view = new Rect(viewOrigin, viewer.Bounds.Size);
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight);
        var brush = GhostForeground ?? Brushes.Gray;
        var key = new TextLayout(KeyHint, typeface, Math.Max(8, FontSize - 3), brush);
        var keyWidth = key.WidthIncludingTrailingWhitespace + 8;
        var left = origin.X + presenter.TextLayout.WidthIncludingTrailingWhitespace + Gap;
        var room = view.Right - left - KeyGap - keyWidth;
        if (room < FontSize * 2)
        {
            // Not even two characters fit: nothing is drawn (the help text still says what Tab does).
            return null;
        }

        var ghost = new TextLayout(offered, typeface, FontSize, brush, textTrimming: TextTrimming.CharacterEllipsis, maxWidth: room);
        var top = origin.Y + ((presenter.Bounds.Height - ghost.Height) / 2);
        var ghostRect = new Rect(left, top, ghost.WidthIncludingTrailingWhitespace, ghost.Height);
        var keyHeight = key.Height + 2;
        var keyRect = new Rect(ghostRect.Right + KeyGap, origin.Y + ((presenter.Bounds.Height - keyHeight) / 2), keyWidth, keyHeight);
        return new GhostPlacement(
            new Rect(origin.X, origin.Y, presenter.TextLayout.WidthIncludingTrailingWhitespace, presenter.Bounds.Height),
            view,
            ghostRect,
            keyRect,
            ghost,
            key,
            ghost.TextLines.Any(l => l.HasCollapsed));
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_presenter is not null)
        {
            _presenter.PropertyChanged -= OnPresenterChanged;
        }

        base.OnApplyTemplate(e);
        _presenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter");
        if (_presenter is not null)
        {
            _presenter.PropertyChanged += OnPresenterChanged;
        }

        // Drawn over the template, after it in the visual children; arranged over the whole box and never hit.
        if (!VisualChildren.Contains(_layer))
        {
            VisualChildren.Add(_layer);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None && _offerIndex >= 0 && Suggestions is { } list && _offerIndex < list.Count)
        {
            Accept(list, _offerIndex);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            // Anything not put there by Tab (typing, pasting, deleting, the page itself) ends the offer for now.
            if (!_applying)
            {
                _dismissed = true;
            }

            Update();
        }
        else if (change.Property == SuggestionsProperty)
        {
            _dismissed = false;
            _acceptedIndex = -1;
            Update();
        }
        else if (change.Property == IdleHelpTextProperty || change.Property == OfferHelpFormatProperty)
        {
            Update();
        }
        else if (change.Property == IsKeyboardFocusWithinProperty || change.Property == GhostForegroundProperty
            || change.Property == KeyHintProperty || change.Property == KeyHintBorderBrushProperty || change.Property == BoundsProperty)
        {
            _layer.InvalidateVisual();
        }
    }

    private void OnPresenterChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty)
        {
            _layer.InvalidateVisual();
        }
    }

    private void Accept(IReadOnlyList<string> list, int index)
    {
        var value = list[index];
        _applying = true;
        try
        {
            SetCurrentValue(TextProperty, value);
            ClearSelection();
            CaretIndex = value.Length;
        }
        finally
        {
            _applying = false;
        }

        _acceptedIndex = index;
        Update();
    }

    /// <summary>What Tab offers now: the first candidate, or after one was taken, the next one, wrapping; never the box's text.</summary>
    private void Update()
    {
        var text = Text ?? string.Empty;
        var index = -1;
        if (!_dismissed && Suggestions is { Count: > 0 } list)
        {
            var start = _acceptedIndex >= 0 ? _acceptedIndex + 1 : 0;
            for (var i = 0; i < list.Count; i++)
            {
                var candidate = (start + i) % list.Count;
                if (!string.IsNullOrWhiteSpace(list[candidate]) && !string.Equals(list[candidate], text, StringComparison.OrdinalIgnoreCase))
                {
                    index = candidate;
                    break;
                }
            }
        }

        _offerIndex = index;
        Offered = index >= 0 ? Suggestions![index] : null;
        AutomationProperties.SetHelpText(
            this,
            Offered is { } offered && OfferHelpFormat is { } format ? string.Format(CultureInfo.CurrentCulture, format, offered) : IdleHelpText);
        _layer.InvalidateVisual();
    }

    private void RenderGhost(DrawingContext context)
    {
        if (Place() is not { } place)
        {
            return;
        }

        using (context.PushClip(place.View))
        {
            place.GhostLayout.Draw(context, place.Ghost.TopLeft);
            var border = KeyHintBorderBrush ?? GhostForeground ?? Brushes.Gray;
            context.DrawRectangle(null, new Pen(border, 1), place.Key.Deflate(0.5), 3, 3);
            place.KeyLayout.Draw(context, new Point(place.Key.X + 4, place.Key.Y + ((place.Key.Height - place.KeyLayout.Height) / 2)));
        }
    }

    /// <summary>The text as laid out, the visible text area, the ghost and its key hint, in the box's coordinates.</summary>
    internal sealed record GhostPlacement(Rect Text, Rect View, Rect Ghost, Rect Key, TextLayout GhostLayout, TextLayout KeyLayout, bool Trimmed);

    /// <summary>Draws the ghost over the box's template; takes no room and is never hit.</summary>
    private sealed class GhostLayer : Control
    {
        private readonly GhostTextBox _owner;

        public GhostLayer(GhostTextBox owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
            ClipToBounds = true;
            Focusable = false;
        }

        public override void Render(DrawingContext context) => _owner.RenderGhost(context);

        protected override Size MeasureOverride(Size availableSize) => default;
    }
}
