using System.Globalization;
using Mindweave.Application.Storage;

namespace Mindweave.Application.Preferences;

/// <summary>
/// How wide the text may be (the owner, 2026-11-23): a number of pixels, 800 unless chosen, or null for the whole page. The
/// page itself keeps its width; what the text leaves becomes its margins.
/// </summary>
public interface ITextWidth
{
    Task<double?> GetAsync(CancellationToken cancellationToken);

    Task SetAsync(double? width, CancellationToken cancellationToken);
}

/// <summary>One key in the preference store (no migration); missing or unreadable means the default.</summary>
public sealed class TextWidthService(IPreferenceStore store) : ITextWidth
{
    public const string Key = "text-width";

    /// <summary>Stored for the whole page, so that it can be told from no choice.</summary>
    public const string Whole = "whole";

    public const double Default = 800;

    public const double Min = 420;

    public const double Max = 2_000;

    public async Task<double?> GetAsync(CancellationToken cancellationToken) =>
        Parse(await store.GetAsync(Key, cancellationToken).ConfigureAwait(false));

    public Task SetAsync(double? width, CancellationToken cancellationToken) =>
        store.SetAsync(Key, Clamp(width) is { } pixels ? pixels.ToString("0.##", CultureInfo.InvariantCulture) : Whole, cancellationToken);

    public static double? Parse(string? stored) =>
        stored == Whole ? null
        : double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out var width) && double.IsFinite(width) ? Clamp(width)
        : Default;

    public static double? Clamp(double? width) => width is { } pixels && double.IsFinite(pixels) ? Math.Clamp(pixels, Min, Max) : null;
}
