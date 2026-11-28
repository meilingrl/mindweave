using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.Markdown;

/// <summary>
/// Holds one message's Markdown renderer (D2 §2.5, dependencies-4). A formula or Markdown the model wrote wrongly must not
/// take the app down every time the message is drawn: when building or laying out the message throws, it is shown as plain
/// text with a one-line note instead, and the rest of the page is laid out as usual. Text that cannot be drawn is the
/// message's own; it is not tried again while it only grows (a streaming answer), only once it is replaced.
/// <para>
/// A control deep inside the renderer can also be laid out by itself later (a formula whose drawing was prepared in the
/// background). Its exception then reaches the UI guard instead of this one; <see cref="FallBackWhereBroken"/> finds the
/// message whose layout it broke.
/// </para>
/// </summary>
public sealed class RenderGuard : Decorator
{
    /// <summary>Guards on screen, for <see cref="FallBackWhereBroken"/>. UI thread only.</summary>
    private static readonly HashSet<RenderGuard> Attached = [];

    private readonly SelectableTextBlock _plain;
    private readonly StackPanel _fallback;
    private string _source = string.Empty;
    private string? _failedSource;

    public RenderGuard(MarkdownRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        Renderer = renderer;
        _plain = new SelectableTextBlock { Name = "PlainBody", TextWrapping = TextWrapping.Wrap };
        _fallback = new StackPanel
        {
            Name = "PlainFallback",
            Spacing = 6,
            Children =
            {
                new TextBlock { Name = "PlainNote", Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, Text = UiText.MessageShownAsPlainText },
                _plain,
            },
        };
        Child = renderer;
    }

    public MarkdownRenderer Renderer { get; }

    /// <summary>The message is shown as plain text because drawing it threw.</summary>
    public bool IsPlain => ReferenceEquals(Child, _fallback);

    /// <summary>
    /// The message's text as it stands now, shown as plain text if drawing fails. Set before every change to what the
    /// renderer shows; a text that no longer begins with the one that failed is tried in Markdown again.
    /// </summary>
    public string Source
    {
        get => _source;
        set
        {
            _source = value ?? string.Empty;
            _plain.Text = _source;
            if (IsPlain && (_failedSource is not { Length: > 0 } failed || !_source.StartsWith(failed, StringComparison.Ordinal)))
            {
                Child = Renderer;
            }
        }
    }

    /// <summary>Runs <paramref name="show"/>, which hands the renderer its next document; a throw shows the text plainly.</summary>
    public void Show(string source, Action<MarkdownRenderer> show)
    {
        ArgumentNullException.ThrowIfNull(show);
        Source = source;
        if (IsPlain)
        {
            return;
        }

        try
        {
            show(Renderer);

            // Laid out through this guard: Avalonia measures a control whose own layout changed by itself, from the layout
            // manager, when its parents are valid, and an exception there would pass this guard by.
            InvalidateMeasure();
        }
#pragma warning disable CA1031 // Whatever the renderer throws on this text, the text is shown plainly (D2 §2.5).
        catch (Exception ex) when (!UiExceptionGuard.IsFatal(ex))
#pragma warning restore CA1031
        {
            FailToPlainText(ex);
        }
    }

    /// <summary>
    /// Called by the UI guard with an exception no handler took. When it came from the Markdown or formula renderer, each
    /// guard on screen whose renderer was left with an unfinished layout shows its message plainly. True when one did.
    /// </summary>
    public static bool FallBackWhereBroken(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!FromRenderer(exception))
        {
            return false;
        }

        var broken = Attached.Where(g => !g.IsPlain && g.Renderer.GetSelfAndVisualDescendants().OfType<Layoutable>().Any(l => l.IsVisible && (!l.IsMeasureValid || !l.IsArrangeValid))).ToList();
        foreach (var guard in broken)
        {
            guard.FailToPlainText(exception);
        }

        return broken.Count > 0;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attached.Add(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Attached.Remove(this);
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        try
        {
            return base.MeasureOverride(availableSize);
        }
#pragma warning disable CA1031 // As in Show: the text is shown plainly.
        catch (Exception ex) when (!UiExceptionGuard.IsFatal(ex) && !IsPlain)
#pragma warning restore CA1031
        {
            FailToPlainText(ex);
            return base.MeasureOverride(availableSize);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        try
        {
            return base.ArrangeOverride(finalSize);
        }
#pragma warning disable CA1031 // As in Show: the text is shown plainly.
        catch (Exception ex) when (!UiExceptionGuard.IsFatal(ex) && !IsPlain)
#pragma warning restore CA1031
        {
            FailToPlainText(ex);
            _fallback.Measure(finalSize);
            return base.ArrangeOverride(finalSize);
        }
    }

    private static bool FromRenderer(Exception exception) =>
        exception.ToString() is var trace
        && (trace.Contains("LiveMarkdown", StringComparison.Ordinal) || trace.Contains("RaTeX", StringComparison.Ordinal) || trace.Contains("Markdig", StringComparison.Ordinal));

    /// <summary>What made the message plain last (tests).</summary>
    internal Exception? LastFailure { get; private set; }

    private void FailToPlainText(Exception exception)
    {
        LastFailure = exception;
        DesktopLog.LogDrawingFailure(exception);
        _failedSource = _source;
        Child = _fallback;
        try
        {
            // Nothing of the broken document is kept or laid out again: the layout pass that threw still measures the renderer
            // once more by itself, and with no document that cannot throw.
            Renderer.DocumentUpdate = null;
        }
#pragma warning disable CA1031 // Clearing is a courtesy; the renderer is out of the tree either way.
        catch (Exception swallowed) when (!UiExceptionGuard.IsFatal(swallowed))
#pragma warning restore CA1031
        {
            DesktopLog.LogDrawingFailure(swallowed);
        }

        InvalidateMeasure();
    }
}
