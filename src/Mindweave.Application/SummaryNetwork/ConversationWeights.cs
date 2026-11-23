using Mindweave.Core.Conversations;

namespace Mindweave.Application.SummaryNetwork;

/// <summary>How much of the exploration hangs on a conversation: its own questions plus those of everything branched from it.</summary>
public static class ConversationWeights
{
    /// <summary>
    /// <c>w(c) = q'(c) + sum of q'(d)</c> over all descendants <c>d</c> of <c>c</c>, with <c>q'(x) = max(q(x), 1)</c>.
    /// Iterative, so a very deep chain of branches cannot overflow the stack.
    /// </summary>
    public static IReadOnlyDictionary<ConversationId, int> Of(
        IReadOnlyDictionary<ConversationId, ConversationId?> parents,
        IReadOnlyDictionary<ConversationId, int> questions)
    {
        ArgumentNullException.ThrowIfNull(parents);
        ArgumentNullException.ThrowIfNull(questions);
        var weight = new Dictionary<ConversationId, int>(parents.Count);
        var pending = new Dictionary<ConversationId, int>(parents.Count);
        foreach (var (id, parent) in parents)
        {
            weight[id] = questions.TryGetValue(id, out var q) ? Math.Max(q, 1) : 1;
            if (parent is { } p && parents.ContainsKey(p))
            {
                pending[p] = pending.GetValueOrDefault(p) + 1;
            }
        }

        // Leaves first: a conversation passes its total up once all its children have passed theirs.
        var ready = new Stack<ConversationId>(parents.Keys.Where(id => !pending.ContainsKey(id)));
        while (ready.Count > 0)
        {
            var id = ready.Pop();
            if (parents[id] is not { } parent || !parents.ContainsKey(parent))
            {
                continue;
            }

            weight[parent] += weight[id];
            if (--pending[parent] == 0)
            {
                ready.Push(parent);
            }
        }

        return weight;
    }
}
