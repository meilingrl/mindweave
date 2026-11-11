using Mindweave.Application.Storage;

namespace Mindweave.Application.Preferences;

/// <summary>
/// How a new answer is announced (2026-11-10): whether a hint also plays the system's short alert sound (available, but
/// off unless chosen), and whether conversations of the open workspace pop up a hint too (on unless turned off). Used by
/// the settings page and the new-answer hints (course requirement C3). The 提示 section's third choice lives here too:
/// whether a workspace's names are prepared in the background (2026-11-11, on unless turned off).
/// </summary>
public interface INewsAlerts
{
    Task<bool> GetSoundAsync(CancellationToken cancellationToken);

    /// <summary>Applies at once for this run, even when the write then fails.</summary>
    Task SetSoundAsync(bool enabled, CancellationToken cancellationToken);

    /// <summary>A conversation of the open workspace that is not on screen pops up a hint when an answer ends there.</summary>
    Task<bool> GetInWorkspaceAsync(CancellationToken cancellationToken);

    /// <summary>Applies at once for this run, even when the write then fails.</summary>
    Task SetInWorkspaceAsync(bool enabled, CancellationToken cancellationToken);

    /// <summary>
    /// Once new conversations have titles, names for their workspace are asked for in the background, so a rename shows
    /// one at once (2026-11-11). Off: no request is ever sent unless the user asks in the rename row.
    /// </summary>
    Task<bool> GetPrepareNamesAsync(CancellationToken cancellationToken);

    /// <summary>Applies at once for this run, even when the write then fails.</summary>
    Task SetPrepareNamesAsync(bool enabled, CancellationToken cancellationToken);
}

/// <summary>
/// Keeps each choice in the preference store (one key each, no migration); a missing or unknown value means the choice's
/// default. A choice is also held in memory once read or made, so a hint does not read storage each time and a choice
/// that could not be written still holds until the application closes, as for the appearance.
/// </summary>
public sealed class NewsAlertsService(IPreferenceStore store) : INewsAlerts
{
    public const string SoundKey = "news.sound";

    public const string InWorkspaceKey = "news.in-workspace";

    public const string PrepareNamesKey = "workspace.prepare-names";

    private readonly Choice _sound = new(store, SoundKey, fallback: false);
    private readonly Choice _inWorkspace = new(store, InWorkspaceKey, fallback: true);
    private readonly Choice _prepareNames = new(store, PrepareNamesKey, fallback: true);

    public Task<bool> GetSoundAsync(CancellationToken cancellationToken) => _sound.GetAsync(cancellationToken);

    public Task SetSoundAsync(bool enabled, CancellationToken cancellationToken) => _sound.SetAsync(enabled, cancellationToken);

    public Task<bool> GetInWorkspaceAsync(CancellationToken cancellationToken) => _inWorkspace.GetAsync(cancellationToken);

    public Task SetInWorkspaceAsync(bool enabled, CancellationToken cancellationToken) => _inWorkspace.SetAsync(enabled, cancellationToken);

    public Task<bool> GetPrepareNamesAsync(CancellationToken cancellationToken) => _prepareNames.GetAsync(cancellationToken);

    public Task SetPrepareNamesAsync(bool enabled, CancellationToken cancellationToken) => _prepareNames.SetAsync(enabled, cancellationToken);

    /// <summary>One on/off choice under one key: stored as "on" or "off"; anything else is <paramref name="fallback"/>.</summary>
    private sealed class Choice(IPreferenceStore store, string key, bool fallback)
    {
        private const int Unknown = -1;

        /// <summary><see cref="Unknown"/>, 0 (off) or 1 (on).</summary>
        private int _value = Unknown;

        public async Task<bool> GetAsync(CancellationToken cancellationToken)
        {
            var known = Volatile.Read(ref _value);
            if (known != Unknown)
            {
                return known == 1;
            }

            var stored = await store.GetAsync(key, cancellationToken).ConfigureAwait(false) switch
            {
                "on" => 1,
                "off" => 0,
                _ => fallback ? 1 : 0,
            };

            // A choice made while this was reading wins over what was stored before it.
            var now = Interlocked.CompareExchange(ref _value, stored, Unknown);
            return (now == Unknown ? stored : now) == 1;
        }

        public Task SetAsync(bool enabled, CancellationToken cancellationToken)
        {
            Volatile.Write(ref _value, enabled ? 1 : 0);
            return store.SetAsync(key, enabled ? "on" : "off", cancellationToken);
        }
    }
}
