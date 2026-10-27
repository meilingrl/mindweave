using System.Collections.Immutable;
using System.Text;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.Summaries;

public static class SummaryReadPolicy
{
    /// <summary>Renderer supplies actual readable text, without hidden Markdown syntax. Offsets use this returned text's Unicode scalars.</summary>
    public static string NormalizeReadableText(string text)
    {
        KnowledgeSummaryProtocol.ValidateUnicode(text);
        return string.Join(" ", text.Normalize(NormalizationForm.FormC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static SummaryReadState Open(Guid versionId, string readableText, SummaryReadState? previous)
    {
        var normalized = NormalizeReadableText(readableText);
        var hash = KnowledgeSummaryInputPlanner.Hash(normalized);
        if (previous is not null && previous.VersionId != versionId) throw new ArgumentException("阅读状态属于其他版本。", nameof(previous));
        var sameText = previous?.TextHash == hash;
        return new(versionId, checked((previous?.Epoch ?? 0) + 1), checked((previous?.Revision ?? 0) + 1), previous?.IsRead ?? false,
            false, hash, normalized.EnumerateRunes().Count(), sameText ? previous!.Seen : [], TimeSpan.Zero, 0, null, false);
    }

    public static SummaryReadState SetManual(SummaryReadState state, bool isRead) => state with
    {
        Revision = checked(state.Revision + 1), IsRead = isRead, SuppressAutomatic = !isRead,
        VisibleDwell = TimeSpan.Zero, LastObservationTime = null, WasActiveVisible = false
    };

    public static SummaryOutcome<SummaryReadState> Observe(SummaryReadState state, SummaryReadObservation observation,
        double threshold = .85, TimeSpan? shortBodyDwell = null)
    {
        var dwell = shortBodyDwell ?? TimeSpan.FromSeconds(3);
        if (!double.IsFinite(threshold) || threshold <= 0 || threshold > 1 || dwell < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(threshold));
        if (observation.Epoch != state.Epoch || observation.ExpectedRevision != state.Revision || observation.Sequence <= state.ObservationSequence ||
            observation.TextHash != state.TextHash || observation.MonotonicTime < TimeSpan.Zero ||
            (state.LastObservationTime is { } last && observation.MonotonicTime < last))
            return new(SummaryOutcomeKind.Obsolete, state, "StaleReadingObservation");
        if (observation.Intervals.IsDefault || observation.Intervals.Any(i => i.Start < 0 || i.End < i.Start || i.End > state.TextLength))
            return new(SummaryOutcomeKind.Rejected, state, "InvalidReadableInterval");
        var currentVisible = Union(observation.Intervals);
        var active = observation.IsActive && observation.IsVisible && !currentVisible.IsEmpty;
        var shortBodyVisible = state.TextLength > 0 && currentVisible.Sum(i => (long)i.End - i.Start) / (double)state.TextLength >= threshold;
        var seen = active ? Union(state.Seen.Concat(observation.Intervals)) : state.Seen;
        // Hidden/inactive observations close the segment. Never charge the unseen gap when returning.
        var visibleDwell = state.VisibleDwell;
        if (active && shortBodyVisible && state.WasActiveVisible && observation.FitsViewport && state.LastObservationTime is { } since)
            visibleDwell += observation.MonotonicTime - since;
        var fraction = state.TextLength > 0 ? seen.Sum(i => (long)i.End - i.Start) / (double)state.TextLength : 0;
        var read = state.IsRead || (!state.SuppressAutomatic && active && fraction >= threshold &&
            (!observation.FitsViewport || visibleDwell >= dwell));
        return new(SummaryOutcomeKind.Applied, state with
        {
            Revision = checked(state.Revision + 1), IsRead = read, Seen = seen, VisibleDwell = visibleDwell,
            ObservationSequence = observation.Sequence, LastObservationTime = observation.MonotonicTime,
            WasActiveVisible = active && shortBodyVisible && observation.FitsViewport
        });
    }

    private static ImmutableArray<ReadableInterval> Union(IEnumerable<ReadableInterval> intervals)
    {
        var result = new List<ReadableInterval>();
        foreach (var interval in intervals.Where(i => i.End > i.Start).OrderBy(i => i.Start).ThenBy(i => i.End))
        {
            if (result.Count == 0 || interval.Start > result[^1].End) result.Add(interval);
            else result[^1] = result[^1] with { End = Math.Max(result[^1].End, interval.End) };
        }
        return result.ToImmutableArray();
    }
}
