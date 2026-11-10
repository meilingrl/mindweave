using Mindweave.Application.Storage;

namespace Mindweave.Application.Preferences;

/// <summary>
/// Whether a new answer in another workspace also plays the system's short alert sound (the user's feedback 2026-11-10:
/// available, but off unless chosen). Used by the settings page and the workspace hint (course requirement C3).
/// </summary>
public interface INewsAlerts
{
    Task<bool> GetSoundAsync(CancellationToken cancellationToken);

    /// <summary>Applies at once for this run, even when the write then fails.</summary>
    Task SetSoundAsync(bool enabled, CancellationToken cancellationToken);
}

/// <summary>
/// Keeps the choice in the preference store (one key, no migration); missing or unknown means off. The choice is also
/// held in memory once read or made, so a hint does not read storage each time and a choice that could not be written
/// still holds until the application closes, as for the appearance.
/// </summary>
public sealed class NewsAlertsService(IPreferenceStore store) : INewsAlerts
{
    public const string SoundKey = "news.sound";

    private const int Unknown = -1;

    /// <summary><see cref="Unknown"/>, 0 (off) or 1 (on).</summary>
    private int _sound = Unknown;

    public async Task<bool> GetSoundAsync(CancellationToken cancellationToken)
    {
        var known = Volatile.Read(ref _sound);
        if (known != Unknown)
        {
            return known == 1;
        }

        var stored = await store.GetAsync(SoundKey, cancellationToken).ConfigureAwait(false) == "on" ? 1 : 0;

        // A choice made while this was reading wins over what was stored before it.
        var now = Interlocked.CompareExchange(ref _sound, stored, Unknown);
        return (now == Unknown ? stored : now) == 1;
    }

    public Task SetSoundAsync(bool enabled, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _sound, enabled ? 1 : 0);
        return store.SetAsync(SoundKey, enabled ? "on" : "off", cancellationToken);
    }
}
