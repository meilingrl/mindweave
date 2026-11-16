using System.Text;
using Mindweave.Core.Models;

namespace Mindweave.Core.Context;

/// <summary>
/// Token estimate without a model-specific tokenizer. Deliberately high: an underestimate makes the service
/// reject the request, an overestimate only compresses a little earlier. CJK and other non-ASCII characters
/// count one token each; ASCII three characters per token.
/// </summary>
public static class TokenEstimator
{
    public const int PerMessage = 4;

    /// <summary>
    /// Fixed overhead of a request (chat template, role markers). Measured against a real service's reported prompt tokens
    /// (2026-10-27): with 3 the smallest request was undercounted (56 estimated, 62 billed); larger requests were
    /// overestimated by about 1.4–1.5× (development findings).
    /// </summary>
    public const int PerRequest = 16;

    public static int Estimate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ascii = 0;
        var other = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.IsAscii)
            {
                ascii++;
            }
            else
            {
                other++;
            }
        }

        return other + ((ascii + 2) / 3);
    }

    /// <summary>One message as sent: its text plus the per-message framing.</summary>
    public static int Message(string text) => Estimate(text) + PerMessage;

    /// <summary>A whole request as sent: the per-request overhead plus each message.</summary>
    public static int Request(IEnumerable<string> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return PerRequest + messages.Sum(Message);
    }
}

/// <summary>
/// How one model's billed prompt tokens relate to <see cref="TokenEstimator"/>: billed ≈ <see cref="Scale"/> × estimate +
/// <see cref="Overhead"/>. Learned from the usage services report, so a model whose tokenizer counts more than the estimate
/// (or a relay that adds a hidden prompt) still gets requests that fit. It only ever makes the estimate larger: a model that
/// counts less keeps <see cref="None"/>, since an overestimate only compresses a little earlier (2026-11-05, real services).
/// </summary>
public sealed record TokenCalibration(double Scale, int Overhead)
{
    /// <summary>Requests looked at: recent ones, so a changed relay or model version is followed.</summary>
    public const int SampleSize = 50;

    /// <summary>The scale is never fitted above this; beyond it a report is more likely wrong than the estimate.</summary>
    public const double MaxScale = 3;

    public static TokenCalibration None { get; } = new(1, 0);

    /// <summary>Billed tokens predicted for a request the estimator puts at <paramref name="estimate"/>.</summary>
    public int Predict(int estimate) => (int)Math.Ceiling((Scale * estimate) + Overhead);

    /// <summary>The largest estimate whose prediction stays within <paramref name="tokens"/>.</summary>
    public int EstimateWithin(int tokens) => tokens <= Overhead ? 0 : (int)Math.Floor((tokens - Overhead) / Scale);

    /// <summary>
    /// Fits billed ≈ scale × estimate + overhead to (estimate, billed) pairs so that the result covers the requests seen.
    /// Guarded against misleading data (review 2026-11-05): the slope is fitted only when the requests span a wide range of
    /// sizes (otherwise small-request noise would be extrapolated to large ones), and from three samples on, a report far
    /// outside the others (a relay reporting a running total, say) is set aside before fitting. <see cref="ContextBudget"/>
    /// also caps the overhead, so no report can leave a model without room.
    /// </summary>
    public static TokenCalibration Fit(IReadOnlyList<(int Estimate, int Reported)> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var usable = samples.Where(s => s.Estimate > 0 && s.Reported > 0).ToList();
        if (usable.Count == 0)
        {
            return None;
        }

        var scale = Slope(usable);
        if (usable.Count >= 3)
        {
            var kept = WithoutOutliers(usable, scale);
            if (kept.Count < usable.Count)
            {
                usable = kept;
                scale = Slope(usable);
            }
        }

        var overhead = (int)Math.Ceiling(Math.Max(0, usable.Max(s => s.Reported - (scale * s.Estimate))));
        return scale == 1 && overhead == 0 ? None : new TokenCalibration(scale, overhead);
    }

    /// <summary>Requests at least this far apart in size (largest ÷ smallest, and the largest this big) give a slope.</summary>
    public const int SlopeSpread = 4;

    public const int SlopeMinimumEstimate = 1_000;

    private static double Slope(List<(int Estimate, int Reported)> samples)
    {
        var min = samples.Min(s => s.Estimate);
        var max = samples.Max(s => s.Estimate);
        if (samples.Count < 2 || max < SlopeMinimumEstimate || max < SlopeSpread * min)
        {
            return 1;
        }

        var meanX = samples.Average(s => (double)s.Estimate);
        var meanY = samples.Average(s => (double)s.Reported);
        var sxx = samples.Sum(s => (s.Estimate - meanX) * (s.Estimate - meanX));
        var slope = samples.Sum(s => (s.Estimate - meanX) * (s.Reported - meanY)) / sxx;
        return Math.Round(Math.Clamp(slope, 1, MaxScale), 4, MidpointRounding.ToPositiveInfinity);
    }

    /// <summary>Drops samples whose residual is far above the median residual (median + 4 × MAD, with a floor for agreeing data).</summary>
    private static List<(int Estimate, int Reported)> WithoutOutliers(List<(int Estimate, int Reported)> samples, double scale)
    {
        var residuals = samples.Select(s => s.Reported - (scale * s.Estimate)).ToList();
        var median = Median(residuals);
        var mad = Median([.. residuals.Select(r => Math.Abs(r - median))]);
        var limit = median + Math.Max(4 * mad, 100 + (0.25 * Math.Abs(median)));
        return [.. samples.Where((_, i) => residuals[i] <= limit)];
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }
}

/// <summary>
/// What one request may spend on input: capacity minus the output reserve and a safety margin, in billed tokens. Planners
/// compare <see cref="TokenEstimator"/> estimates, so <see cref="Available"/> is that space in estimate units, through the
/// model's <see cref="Calibration"/>.
/// </summary>
public sealed record ContextBudget(int Capacity, int OutputReserve, int Margin, TokenCalibration? Calibration = null)
{
    public const int DefaultCapacity = 32_000;
    public const int DefaultOutputReserve = 4_096;
    public const int MinimumMargin = 1_000;

    /// <summary>
    /// The calibration in use, its overhead at most a quarter of the input space: with the scale capped at
    /// <see cref="TokenCalibration.MaxScale"/>, at least a quarter of the plain space always remains, so a misleading report
    /// can tighten a model but never leave it unable to send (a refused send makes no request, so it would never recover).
    /// </summary>
    private TokenCalibration Fitted => Calibration is { } c && c.Overhead > AvailableTokens / 4
        ? c with { Overhead = AvailableTokens / 4 }
        : Calibration ?? TokenCalibration.None;

    /// <summary>Input space in billed tokens.</summary>
    public int AvailableTokens => Math.Max(0, Capacity - OutputReserve - Margin);

    /// <summary>Input space in estimate units: the largest estimate predicted to fit <see cref="AvailableTokens"/>.</summary>
    public int Available => Fitted.EstimateWithin(AvailableTokens);

    /// <summary>Billed tokens predicted for an estimate.</summary>
    public int Predict(int estimate) => Fitted.Predict(estimate);

    /// <summary>Whether a request of this estimate leaves the output reserve free (the margin may be used up).</summary>
    public bool Fits(int estimate) => Predict(estimate) <= Capacity - OutputReserve;

    public static ContextBudget For(ModelSnapshot model, TokenCalibration? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var capacity = model.Capabilities.ContextWindowTokens is > 0 and var declared ? declared : DefaultCapacity;
        return new ContextBudget(capacity, model.MaxOutputTokens ?? DefaultOutputReserve, Math.Max(MinimumMargin, capacity / 10), calibration);
    }
}
