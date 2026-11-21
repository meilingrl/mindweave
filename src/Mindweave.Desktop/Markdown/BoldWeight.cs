using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Mindweave.Application.Preferences;

namespace Mindweave.Desktop.Markdown;

/// <summary>
/// The weight of bold text in answers and summaries (D-027). With a bold colour, bold is drawn in Medium, not Semibold:
/// the colour tells it apart, so it does not need the heavier face. With "same as text" it keeps the bold weight, its
/// only mark. LiveMarkdown sets the weight on its bold span itself, a local value no style can replace, so it is changed
/// here whenever a bold span takes the bold weight, in finished and streaming answers alike.
/// </summary>
/// <remarks>
/// Where the answer font has no Medium face (Microsoft YaHei on Windows), Medium draws as Regular; the colour still
/// marks bold there.
/// </remarks>
public static class BoldWeight
{
    private const string BoldClass = "Bold";

    private static int _registered;

    /// <summary>The weight bold spans are drawn in now: Medium until the setting says otherwise.</summary>
    public static FontWeight Weight { get; private set; } = FontWeight.Medium;

    public static FontWeight For(BoldColour colour) => colour == BoldColour.SameAsText ? FontWeight.Bold : FontWeight.Medium;

    /// <summary>Once per process, however many applications are created (tests create several).</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            TextElement.FontWeightProperty.Changed.AddClassHandler<Span>(OnWeightChanged);
        }
    }

    /// <summary>Draws bold in <paramref name="weight"/> from now on, in the answers already shown as well.</summary>
    public static void Use(FontWeight weight)
    {
        Weight = weight;
        foreach (var span in Seen())
        {
            span.FontWeight = weight;
        }
    }

    private static void OnWeightChanged(Span span, AvaloniaPropertyChangedEventArgs e)
    {
        if (!span.Classes.Contains(BoldClass))
        {
            return;
        }

        // LiveMarkdown has just given a bold span the bold weight: remember the span, so a later change of the
        // setting reaches it, and draw it in the weight in use.
        if (span.FontWeight == FontWeight.Bold)
        {
            Remember(span);
            if (Weight != FontWeight.Bold)
            {
                span.FontWeight = Weight;
            }
        }
    }

    // Bold spans shown so far, held weakly: a span of a closed answer or a re-render goes away with it.
    private static readonly List<WeakReference<Span>> _seen = [];

    private static void Remember(Span span)
    {
        if (_seen.Count >= 256 && _seen.Count % 256 == 0)
        {
            _seen.RemoveAll(r => !r.TryGetTarget(out _));
        }

        _seen.Add(new WeakReference<Span>(span));
    }

    private static List<Span> Seen()
    {
        _seen.RemoveAll(r => !r.TryGetTarget(out _));
        return _seen.Select(r => r.TryGetTarget(out var span) ? span : null).OfType<Span>().ToList();
    }
}
