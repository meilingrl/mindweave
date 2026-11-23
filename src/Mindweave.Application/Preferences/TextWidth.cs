using System.Globalization;
using Mindweave.Application.Storage;

namespace Mindweave.Application.Preferences;

/// <summary>
/// How wide the text of a conversation may be (the owner, 2026-11-23): a number of pixels, or null for the whole page (the
/// default). The page itself keeps its width; what the text leaves becomes its margins.
/// </summary>
public interface ITextWidth
{
    Task<double?> GetAsync(CancellationToken cancellationToken);

    Task SetAsync(double? width, CancellationToken cancellationToken);
}

/// <summary>One key in the preference store (no migration); missing or unreadable means the whole page.</summary>
public sealed class TextWidthService(IPreferenceStore store) : ITextWidth
{
    public const string Key = "conversation.text-width";

    public const double Min = 420;

    public const double Max = 2_000;

    public async Task<double?> GetAsync(CancellationToken cancellationToken) =>
        Parse(await store.GetAsync(Key, cancellationToken).ConfigureAwait(false));

    public Task SetAsync(double? width, CancellationToken cancellationToken) =>
        Clamp(width) is { } pixels
            ? store.SetAsync(Key, pixels.ToString("0.##", CultureInfo.InvariantCulture), cancellationToken)
            : store.RemoveAsync(Key, cancellationToken);

    public static double? Parse(string? stored) =>
        double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out var width) ? Clamp(width) : null;

    public static double? Clamp(double? width) => width is { } pixels && double.IsFinite(pixels) ? Math.Clamp(pixels, Min, Max) : null;
}
