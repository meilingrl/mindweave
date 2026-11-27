namespace Mindweave.Core.Conversations;

/// <summary>A message of a conversation's line as joining sees it: who said it, where it stands, and when it was made.</summary>
/// <param name="At">When it was made: a question when it was asked, an answer when its attempt began.</param>
public readonly record struct LineEntry(MessageId Id, MessageRole Role, int Seq, DateTimeOffset At);

/// <summary>A conversation's line after joining: every message with its number, in order.</summary>
/// <param name="Renumbered">The messages that were here were numbered anew; otherwise they kept their numbers.</param>
public sealed record JoinedLine(IReadOnlyList<(MessageId Id, int Seq)> Messages, bool Renumbered)
{
    private Dictionary<MessageId, int>? _seqs;

    /// <summary>The number of each message.</summary>
    public IReadOnlyDictionary<MessageId, int> Seqs => _seqs ??= Messages.ToDictionary(m => m.Id, m => m.Seq);

    /// <summary>
    /// A place that one side's line named by number (up to and including <paramref name="seq"/>), as it follows the message
    /// it meant: the last message of that side at or below the number. <see cref="Nothing"/> when there is none.
    /// </summary>
    public int Follow(IReadOnlyList<LineEntry> side, int seq)
    {
        ArgumentNullException.ThrowIfNull(side);
        if (seq < 0)
        {
            return Nothing;
        }

        var meant = side.Where(m => m.Seq <= seq).Select(m => (MessageId?)m.Id).LastOrDefault();
        return meant is { } id && Seqs.TryGetValue(id, out var now) ? now : Nothing;
    }

    /// <summary>
    /// How far a summary covers that one side made up to <paramref name="seq"/>: as far as the line goes without a message it
    /// did not know (docs/design/sharing-backup.md §2), so a question of the other side before its end ends it. It never
    /// goes below the first message: a version covers at least one, and the line then shows that later content is not in it.
    /// </summary>
    public int Coverage(IReadOnlyList<LineEntry> side, int seq)
    {
        ArgumentNullException.ThrowIfNull(side);
        var known = side.Where(m => m.Seq <= seq).Select(m => m.Id).ToHashSet();
        var end = Follow(side, seq);
        foreach (var (id, at) in Messages)
        {
            if (at > end)
            {
                break;
            }

            if (!known.Contains(id))
            {
                end = at - 1;
                break;
            }
        }

        return Math.Max(end, Messages.Count > 0 ? Messages[0].Seq : 0);
    }

    /// <summary>The upper bound that says nothing of a conversation is used (the branch context's own).</summary>
    public const int Nothing = -1;
}

/// <summary>
/// Joins two lines of one conversation that each hold messages the other lacks (docs/design/sharing-backup.md V2): a
/// question keeps its answers and attempts with it; the line is ordered by when each question was asked; each side's own
/// order is kept; questions of the same instant go by identity. The result does not depend on which side is which, so two
/// devices that exchange manual backups reach the same line, and joining a line with one it already holds changes nothing.
/// </summary>
public static class ConversationJoin
{
    /// <param name="here">This device's line, by number.</param>
    /// <param name="other">The other side's line, by number.</param>
    public static JoinedLine Join(IReadOnlyList<LineEntry> here, IReadOnlyList<LineEntry> other)
    {
        ArgumentNullException.ThrowIfNull(here);
        ArgumentNullException.ThrowIfNull(other);
        var order = Order(here, other);
        var held = here.Select(m => m.Id).ToList();

        // What was here keeps its numbers when everything else comes after it; the rest keep theirs when they still fit.
        if (order.Take(held.Count).SequenceEqual(held))
        {
            var numbers = here.ToDictionary(m => m.Id, m => m.Seq);
            var theirs = other.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First().Seq);
            var after = order.Skip(held.Count).ToList();
            var last = here.Count > 0 ? here.Max(m => m.Seq) : -1;
            var kept = after.Select(id => theirs.GetValueOrDefault(id, -1)).ToList();
            var keepsTheirs = kept.Count == 0 || (kept[0] > last && kept.Zip(kept.Skip(1)).All(p => p.Second > p.First));
            for (var i = 0; i < after.Count; i++)
            {
                numbers[after[i]] = keepsTheirs ? kept[i] : last + 1 + i;
            }

            return new JoinedLine([.. order.Select(id => (id, numbers[id]))], Renumbered: false);
        }

        return new JoinedLine([.. order.Select((id, i) => (id, i))], Renumbered: true);
    }

    /// <summary>The joined order of the two lines' messages, each once.</summary>
    public static IReadOnlyList<MessageId> Order(IReadOnlyList<LineEntry> here, IReadOnlyList<LineEntry> other)
    {
        ArgumentNullException.ThrowIfNull(here);
        ArgumentNullException.ThrowIfNull(other);
        var mine = Groups(here);
        var theirs = Groups(other);

        // A message belongs to the group this side puts it in; the other side's copy of it is passed over.
        var placed = mine.SelectMany(g => g.Members).Select(m => m.Id).ToHashSet();
        theirs = [.. theirs.Select(g => g with { Members = [.. g.Members.Where(m => g.Key == m.Id || !placed.Contains(m.Id))] })];
        var answers = new Dictionary<MessageId, List<IReadOnlyList<LineEntry>>>();
        foreach (var group in mine.Concat(theirs))
        {
            if (!answers.TryGetValue(group.Key, out var lists))
            {
                answers[group.Key] = lists = [];
            }

            lists.Add([.. group.Members.Where(m => m.Id != group.Key)]);
        }

        var heads = mine.Concat(theirs).GroupBy(g => g.Key).ToDictionary(g => g.Key, g => g.First().Head);
        var ordered = Merge([.. mine.Select(g => heads[g.Key])], [.. theirs.Select(g => heads[g.Key])]);
        var line = new List<MessageId>();
        foreach (var head in ordered)
        {
            line.Add(head.Id);
            var lists = answers[head.Id];
            line.AddRange(Merge(lists[0], lists.Count > 1 ? lists[1] : []).Select(m => m.Id).Where(id => id != head.Id));
        }

        return [.. line.Distinct()];
    }

    /// <summary>A question with the answers after it; answers before any question form a group of their own, under the first.</summary>
    private sealed record Group(MessageId Key, LineEntry Head, IReadOnlyList<LineEntry> Members);

    private static List<Group> Groups(IReadOnlyList<LineEntry> line)
    {
        var groups = new List<Group>();
        List<LineEntry>? members = null;
        foreach (var message in line.OrderBy(m => m.Seq).DistinctBy(m => m.Id))
        {
            if (members is null || message.Role == MessageRole.User)
            {
                members = [message];
                groups.Add(new Group(message.Id, message, members));
            }
            else
            {
                members.Add(message);
            }
        }

        return groups;
    }

    /// <summary>
    /// Both orders at once: of what may come next under both, the earliest, then the lower identity. Should the two orders
    /// contradict each other, which no device makes, the earliest that is left goes next, so it still ends.
    /// </summary>
    private static List<LineEntry> Merge(IReadOnlyList<LineEntry> a, IReadOnlyList<LineEntry> b)
    {
        var nodes = a.Concat(b).GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        var before = nodes.Keys.ToDictionary(id => id, _ => new HashSet<MessageId>());
        foreach (var order in new[] { a, b })
        {
            for (var i = 1; i < order.Count; i++)
            {
                if (order[i].Id != order[i - 1].Id)
                {
                    before[order[i].Id].Add(order[i - 1].Id);
                }
            }
        }

        var left = new SortedSet<LineEntry>(nodes.Values, Earliest.Instance);
        var result = new List<LineEntry>(nodes.Count);
        while (left.Count > 0)
        {
            LineEntry? ready = null;
            foreach (var m in left)
            {
                if (before[m.Id].Count == 0)
                {
                    ready = m;
                    break;
                }
            }

            var next = ready ?? left.Min;

            left.Remove(next);
            result.Add(next);
            foreach (var waiting in before.Values)
            {
                waiting.Remove(next.Id);
            }
        }

        return result;
    }

    private sealed class Earliest : IComparer<LineEntry>
    {
        public static readonly Earliest Instance = new();

        public int Compare(LineEntry x, LineEntry y)
        {
            var at = x.At.CompareTo(y.At);
            return at != 0 ? at : x.Id.Value.CompareTo(y.Id.Value);
        }
    }
}
