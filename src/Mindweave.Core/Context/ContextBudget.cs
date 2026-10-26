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
    /// overestimated by about 1.4–1.5× (TRACK.md findings).
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
}

/// <summary>What one request may spend on input: capacity minus the output reserve and a safety margin.</summary>
public sealed record ContextBudget(int Capacity, int OutputReserve, int Margin)
{
    public const int DefaultCapacity = 32_000;
    public const int DefaultOutputReserve = 4_096;
    public const int MinimumMargin = 1_000;

    public int Available => Math.Max(0, Capacity - OutputReserve - Margin);

    public static ContextBudget For(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var capacity = model.Capabilities.ContextWindowTokens is > 0 and var declared ? declared : DefaultCapacity;
        return new ContextBudget(capacity, model.MaxOutputTokens ?? DefaultOutputReserve, Math.Max(MinimumMargin, capacity / 10));
    }
}
