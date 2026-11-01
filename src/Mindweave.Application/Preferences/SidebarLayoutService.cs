using System.Globalization;
using Mindweave.Application.Storage;

namespace Mindweave.Application.Preferences;

/// <summary>
/// Keeps the sidebar layout in the preference store (two keys, no migration). Missing or unreadable values mean the
/// default; a stored width outside the bounds is brought back inside them.
/// </summary>
public sealed class SidebarLayoutService(IPreferenceStore store) : ISidebarLayout
{
    public const string WidthKey = "sidebar.width";

    public const string CollapsedKey = "sidebar.collapsed";

    public async Task<SidebarLayout> GetAsync(CancellationToken cancellationToken)
    {
        var width = await store.GetAsync(WidthKey, cancellationToken).ConfigureAwait(false);
        var collapsed = await store.GetAsync(CollapsedKey, cancellationToken).ConfigureAwait(false);
        return Parse(width, collapsed);
    }

    public async Task SetAsync(SidebarLayout layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var width = SidebarLayout.ClampWidth(layout.Width).ToString("0.##", CultureInfo.InvariantCulture);
        await store.SetAsync(WidthKey, width, cancellationToken).ConfigureAwait(false);
        await store.SetAsync(CollapsedKey, layout.IsCollapsed ? "true" : "false", cancellationToken).ConfigureAwait(false);
    }

    public static SidebarLayout Parse(string? width, string? collapsed) => new(
        double.TryParse(width, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? SidebarLayout.ClampWidth(value) : SidebarLayout.DefaultWidth,
        collapsed == "true");
}
