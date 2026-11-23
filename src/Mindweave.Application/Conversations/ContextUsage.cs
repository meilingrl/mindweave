namespace Mindweave.Application.Conversations;

/// <summary>What a part of the context is, as the usage shows it (the owner, 2026-11-23).</summary>
public enum ContextUsageKind
{
    /// <summary>This conversation's own questions and answers, sent as they are.</summary>
    Conversation,

    /// <summary>The conversations this branch comes from, up to its source answer.</summary>
    Ancestors,

    /// <summary>Context summaries standing in for earlier conversation.</summary>
    ContextSummaries,

    Attachments,

    /// <summary>The instructions of a run, and the fixed framing of every request.</summary>
    Instructions,

    /// <summary>The definitions of the tools a run offers.</summary>
    Tools,
}

public sealed record ContextUsagePart(ContextUsageKind Kind, int Tokens);

/// <summary>
/// How much of the model's context the conversation takes now, as estimated by the planner that decides what is sent.
/// <paramref name="Tokens"/> is what would be sent (after any summaries); <paramref name="FullTokens"/> is everything,
/// raw. <paramref name="TooLong"/> when even summaries cannot make it fit.
/// </summary>
public sealed record ContextUsage(int Tokens, int FullTokens, int Budget, bool Compressed, bool TooLong)
{
    public double Fraction => Budget == 0 ? 1 : Math.Min(1, (double)Tokens / Budget);

    /// <summary>What <see cref="Tokens"/> is made of; the parts add up to it. Only parts that take something.</summary>
    public IReadOnlyList<ContextUsagePart> Parts { get; init; } = [];

    /// <summary>
    /// Estimated sizes as shares of <paramref name="tokens"/> (billed tokens, through the model's calibration), so the parts
    /// shown add up to the total shown.
    /// </summary>
    public static IReadOnlyList<ContextUsagePart> Share(IEnumerable<(ContextUsageKind Kind, int Estimate)> estimates, int tokens)
    {
        ArgumentNullException.ThrowIfNull(estimates);
        var parts = estimates.Where(e => e.Estimate > 0).ToList();
        var total = parts.Sum(p => (long)p.Estimate);
        if (total == 0)
        {
            return [];
        }

        var shared = new List<ContextUsagePart>();
        long seen = 0, given = 0;
        foreach (var (kind, estimate) in parts)
        {
            seen += estimate;
            var upTo = (long)Math.Round((double)seen * tokens / total);
            shared.Add(new ContextUsagePart(kind, (int)(upTo - given)));
            given = upTo;
        }

        return shared;
    }
}
