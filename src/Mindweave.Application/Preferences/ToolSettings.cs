using System.Globalization;
using Mindweave.Application.Storage;
using Mindweave.Core.Runs;

namespace Mindweave.Application.Preferences;

/// <summary>
/// 设置 › 工具 (requirements §10.4.4): whether answers may use tools (on unless turned off), and a run's limits. Kept in the
/// preference store (decision R9: keys <c>tools.enabled</c> and <c>tools.limits</c>, no migration); values outside the allowed
/// ranges are brought into them. A run freezes what was set when it was accepted.
/// </summary>
public interface IToolSettings
{
    Task<bool> GetEnabledAsync(CancellationToken cancellationToken);

    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken);

    Task<RunLimits> GetLimitsAsync(CancellationToken cancellationToken);

    /// <summary>Stores the limits brought into their ranges, and returns what was stored.</summary>
    Task<RunLimits> SetLimitsAsync(RunLimits limits, CancellationToken cancellationToken);
}

public sealed class ToolSettingsService(IPreferenceStore store) : IToolSettings
{
    public const string EnabledKey = "tools.enabled";

    public const string LimitsKey = "tools.limits";

    private readonly PreferenceChoice _enabled = new(store, EnabledKey, fallback: true);

    public Task<bool> GetEnabledAsync(CancellationToken cancellationToken) => _enabled.GetAsync(cancellationToken);

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken) => _enabled.SetAsync(enabled, cancellationToken);

    public async Task<RunLimits> GetLimitsAsync(CancellationToken cancellationToken) =>
        Parse(await store.GetAsync(LimitsKey, cancellationToken).ConfigureAwait(false));

    public async Task<RunLimits> SetLimitsAsync(RunLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var clamped = limits.Clamped();
        await store.SetAsync(LimitsKey, Format(clamped), cancellationToken).ConfigureAwait(false);
        return clamped;
    }

    /// <summary>"requests tool-calls minutes tokens"; anything unreadable is the default.</summary>
    public static RunLimits Parse(string? stored)
    {
        var parts = stored?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: 4 }
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var requests)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var calls)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens))
        {
            return RunLimits.Default;
        }

        return new RunLimits(requests, calls, TimeSpan.FromMinutes(minutes), tokens).Clamped();
    }

    public static string Format(RunLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return string.Create(CultureInfo.InvariantCulture, $"{limits.ModelRequests} {limits.ToolCalls} {(int)Math.Round(limits.Duration.TotalMinutes)} {limits.Tokens}");
    }
}
