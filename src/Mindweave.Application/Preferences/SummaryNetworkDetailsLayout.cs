using System.Globalization;
using Mindweave.Application.Storage;

namespace Mindweave.Application.Preferences;

/// <summary>The summary network page's details beside the canvas: their width and whether they are folded away (the owner, 2026-11-23).</summary>
public sealed record SummaryNetworkDetailsLayout(double Width, bool IsCollapsed)
{
    public const double MinWidth = 180;

    public const double MaxWidth = 480;

    public const double DefaultWidth = 232;

    public static SummaryNetworkDetailsLayout Default { get; } = new(DefaultWidth, IsCollapsed: false);

    /// <summary>A width inside the bounds; anything that is not a finite number is the default.</summary>
    public static double ClampWidth(double width) =>
        double.IsFinite(width) ? Math.Clamp(width, MinWidth, MaxWidth) : DefaultWidth;
}

/// <summary>The details' layout as the summary network page uses it: restored at start, kept when changed.</summary>
public interface ISummaryNetworkDetailsLayout
{
    Task<SummaryNetworkDetailsLayout> GetAsync(CancellationToken cancellationToken);

    Task SetAsync(SummaryNetworkDetailsLayout layout, CancellationToken cancellationToken);
}

/// <summary>
/// Keeps the details' layout in the preference store (two keys, no migration). Missing or unreadable values mean the
/// default; a stored width outside the bounds is brought back inside them.
/// </summary>
public sealed class SummaryNetworkDetailsLayoutService(IPreferenceStore store) : ISummaryNetworkDetailsLayout
{
    public const string WidthKey = "summary-network.details.width";

    public const string CollapsedKey = "summary-network.details.collapsed";

    public async Task<SummaryNetworkDetailsLayout> GetAsync(CancellationToken cancellationToken)
    {
        var width = await store.GetAsync(WidthKey, cancellationToken).ConfigureAwait(false);
        var collapsed = await store.GetAsync(CollapsedKey, cancellationToken).ConfigureAwait(false);
        return Parse(width, collapsed);
    }

    public async Task SetAsync(SummaryNetworkDetailsLayout layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var width = SummaryNetworkDetailsLayout.ClampWidth(layout.Width).ToString("0.##", CultureInfo.InvariantCulture);
        await store.SetAsync(WidthKey, width, cancellationToken).ConfigureAwait(false);
        await store.SetAsync(CollapsedKey, layout.IsCollapsed ? "true" : "false", cancellationToken).ConfigureAwait(false);
    }

    public static SummaryNetworkDetailsLayout Parse(string? width, string? collapsed) => new(
        double.TryParse(width, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? SummaryNetworkDetailsLayout.ClampWidth(value)
            : SummaryNetworkDetailsLayout.DefaultWidth,
        collapsed == "true");
}
