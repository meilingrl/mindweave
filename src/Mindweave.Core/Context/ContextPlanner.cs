using System.Security.Cryptography;
using System.Text;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;

namespace Mindweave.Core.Context;

/// <summary>
/// Identity of a context summary: the conversation whose messages it covers (MW-C02), the covered seq range,
/// a hash of the exact messages selected in it, and the prompt version. A summary is reused only when all match,
/// so a range whose answer was later superseded never reuses a summary of the older attempt.
/// </summary>
public sealed record SummaryKey(ConversationId Conversation, int FromSeq, int ToSeq, string InputHash, int PromptVersion);

/// <summary>A run of one conversation's context items that may be sent as a summary instead.</summary>
/// <param name="Closed">Closed chunks have boundaries fixed by the conversation's start and are reusable; a tail is not.</param>
public sealed record ContextChunk(SummaryKey Key, IReadOnlyList<ContextItem> Items, bool Closed);

public abstract record ContextPart
{
    private protected ContextPart()
    {
    }
}

public sealed record RawPart(ContextItem Item) : ContextPart;

public sealed record SummaryPart(ContextChunk Chunk) : ContextPart;

/// <summary>What one request sends, in order: raw messages and summaries standing in for older runs.</summary>
public sealed record ContextPlan(IReadOnlyList<ContextPart> Parts, int EstimatedTokens)
{
    public IEnumerable<ContextChunk> Summaries => Parts.OfType<SummaryPart>().Select(p => p.Chunk);

    public IEnumerable<ContextItem> RawItems => Parts.OfType<RawPart>().Select(p => p.Item);

    public bool IsCompressed => Parts.Any(p => p is SummaryPart);
}

public enum ContextRejection
{
    /// <summary>The question and the required source alone exceed the budget.</summary>
    QuestionTooLarge,

    /// <summary>Even with every older run summarized, the request does not fit.</summary>
    HistoryTooLong,
}

public sealed record PlanOutcome(ContextPlan? Plan, ContextRejection? Rejection);

/// <summary>
/// Chooses what a request sends within a token budget (architecture §6.1). Required items — the current question,
/// the branch's quoted first question, and the direct source answer with its question — are always sent as they
/// are. Everything else is cut into chunks per conversation; when the raw material does not fit, chunks are
/// replaced by summaries from the oldest on, reusable closed chunks before tails, until it fits.
/// </summary>
public static class ContextPlanner
{
    public const int PromptVersion = 1;
    public const int ChunkTokens = 4_000;

    /// <summary>Assumed size of a summary not generated yet.</summary>
    public const int UnknownSummaryTokens = 800;

    /// <param name="knownSummaryTokens">Size of an already stored summary for a key, or null if there is none.</param>
    public static PlanOutcome Plan(IReadOnlyList<ContextSegment> ancestors, ContextSegment own, int budget, Func<SummaryKey, int?> knownSummaryTokens)
    {
        ArgumentNullException.ThrowIfNull(ancestors);
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(knownSummaryTokens);
        var items = BranchContext.Assemble(ancestors, own);
        var cost = items.ToDictionary(i => i, i => TokenEstimator.Message(i.Text));
        var total = TokenEstimator.PerRequest + cost.Values.Sum();
        if (total <= budget)
        {
            return new PlanOutcome(new ContextPlan([.. items.Select(i => (ContextPart)new RawPart(i))], total), null);
        }

        var required = Required(items, ancestors, own);
        if (TokenEstimator.PerRequest + required.Sum(i => cost[i]) > budget)
        {
            return new PlanOutcome(null, ContextRejection.QuestionTooLarge);
        }

        var chunks = Chunks(items, required, [.. ancestors.Select(a => a.Conversation), own.Conversation], cost);
        var summarized = new List<ContextChunk>();
        foreach (var chunk in chunks.Where(c => c.Closed).Concat(chunks.Where(c => !c.Closed)))
        {
            if (total <= budget)
            {
                break;
            }

            total -= chunk.Items.Sum(i => cost[i]);
            total += SummaryTokens(chunk, knownSummaryTokens);
            summarized.Add(chunk);
        }

        if (total > budget)
        {
            return new PlanOutcome(null, ContextRejection.HistoryTooLong);
        }

        var owner = summarized.SelectMany(c => c.Items.Select(i => (Item: i, Chunk: c))).ToDictionary(x => x.Item, x => x.Chunk);
        var parts = new List<ContextPart>();
        foreach (var item in items)
        {
            if (!owner.TryGetValue(item, out var chunk))
            {
                parts.Add(new RawPart(item));
            }
            else if (chunk.Items[0] == item)
            {
                parts.Add(new SummaryPart(chunk));
            }
        }

        return new PlanOutcome(new ContextPlan(parts, total), null);
    }

    /// <summary>The text a summary is sent as, including its framing; its length is what the budget counts.</summary>
    public static string SummaryMessage(SummaryKey key, string summary)
    {
        ArgumentNullException.ThrowIfNull(key);
        return $"以下是较早一段对话（第 {key.FromSeq + 1}–{key.ToSeq + 1} 条消息）的摘要，原文保存在本地：\n\n{summary}";
    }

    /// <summary>What a stored summary costs when sent.</summary>
    public static int SummaryTokens(SummaryKey key, string summary) => TokenEstimator.Message(SummaryMessage(key, summary));

    private static int SummaryTokens(ContextChunk chunk, Func<SummaryKey, int?> known) =>
        known(chunk.Key) ?? SummaryTokens(chunk.Key, string.Empty) + UnknownSummaryTokens;

    private static HashSet<ContextItem> Required(IReadOnlyList<ContextItem> items, IReadOnlyList<ContextSegment> ancestors, ContextSegment own)
    {
        var required = new HashSet<ContextItem> { items[^1] };
        var ownItems = items.Where(i => i.Message.ConversationId == own.Conversation).ToList();
        if (own.Quote is not null && ownItems.FirstOrDefault(i => i.Message.Role == MessageRole.User) is { } quoted)
        {
            required.Add(quoted);
        }

        if (ancestors.Count > 0)
        {
            var parentItems = items.Where(i => i.Message.ConversationId == ancestors[^1].Conversation).ToList();
            if (parentItems.Count > 0 && parentItems[^1].Message.Role == MessageRole.Assistant)
            {
                required.Add(parentItems[^1]);
                if (parentItems.Count > 1 && parentItems[^2].Message.Role == MessageRole.User)
                {
                    required.Add(parentItems[^2]);
                }
            }
        }

        return required;
    }

    /// <summary>
    /// Chunks per conversation over its non-required items, in chain order. A chunk closes at a question once it
    /// reaches <see cref="ChunkTokens"/>, so boundaries depend only on where the conversation starts.
    /// </summary>
    private static List<ContextChunk> Chunks(IReadOnlyList<ContextItem> items, HashSet<ContextItem> required, IReadOnlyList<ConversationId> chain, Dictionary<ContextItem, int> cost)
    {
        var chunks = new List<ContextChunk>();
        foreach (var conversation in chain)
        {
            var run = items.Where(i => i.Message.ConversationId == conversation && !required.Contains(i)).ToList();
            var current = new List<ContextItem>();
            var size = 0;
            foreach (var item in run)
            {
                if (item.Message.Role == MessageRole.User && size >= ChunkTokens)
                {
                    chunks.Add(Chunk(conversation, current, closed: true));
                    current = [];
                    size = 0;
                }

                current.Add(item);
                size += cost[item];
            }

            if (current.Count > 0)
            {
                chunks.Add(Chunk(conversation, current, closed: size >= ChunkTokens));
            }
        }

        return chunks;
    }

    private static ContextChunk Chunk(ConversationId conversation, List<ContextItem> items, bool closed)
    {
        var ids = string.Join(',', items.Select(i => i.Message.Id));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ids)))[..32];
        return new ContextChunk(new SummaryKey(conversation, items[0].Message.Seq, items[^1].Message.Seq, hash, PromptVersion), items, closed);
    }
}
