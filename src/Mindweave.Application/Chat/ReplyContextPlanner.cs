using Mindweave.Core.Chat;

namespace Mindweave.Application.Chat;

/// <summary>Pure planning over already frozen, retry-filtered original messages. Never discovers history.</summary>
public static partial class ReplyContextPlanner
{
    public const int FormatVersion = 1;

    public static ReplyContextPlan Plan(Guid targetConversationId, ChatContext context, AiConfiguration configuration)
    {
        ValidateConfiguration(configuration);
        var messages = context.Messages.ToArray();
        ValidateOriginals(messages);
        if (messages.Length == 0 || messages[^1].Role != MessageRole.User || messages[^1].ConversationId != targetConversationId)
            throw new InvalidOperationException("上下文必须以当前会话的提问结束。");
        var byId = messages.ToDictionary(m => m.Id);
        var keep = new HashSet<Guid> { messages[^1].Id };
        if (context.DirectSourceMessageId is { } sourceId)
        {
            if (!byId.TryGetValue(sourceId, out var source) || source.Role != MessageRole.Assistant ||
                source.ReplyToUserId is not { } userId || !byId.TryGetValue(userId, out var sourceUser) || sourceUser.Role != MessageRole.User)
                throw new InvalidOperationException("直接来源或其提问不在冻结原消息中。");
            keep.Add(sourceId);
            keep.Add(userId);
        }
        var exchanges = messages.Where(m => m.ConversationId == targetConversationId && m.Role == MessageRole.Assistant && IsTerminal(m.Status))
            .Select(m => (Answer: m, User: m.ReplyToUserId is { } id && byId.TryGetValue(id, out var user) && user.Role == MessageRole.User && user.ConversationId == targetConversationId ? user : null))
            .Where(pair => pair.User is not null).Reverse().ToArray();
        if (exchanges.Length > 0)
        {
            keep.Add(exchanges[0].User!.Id);
            keep.Add(exchanges[0].Answer.Id);
        }
        ReplyContextPlan MakePlan(IEnumerable<ContextPart> parts) => new(FormatVersion, ChatRequestBudget.EstimatorProfile,
            Array.AsReadOnly(messages.Select(m => m.Id).ToArray()), Array.AsReadOnly(parts.ToArray()), context.DirectSourceMessageId, context.SourceQuote);
        if (ChatRequestBudget.Fits(configuration, ChatRequestBudget.Inputs(messages, context.SourceQuote)))
            return MakePlan(messages.Select(m => new VerbatimContextPart(m.Id)));
        if (context.SourceQuote is not null && context.DirectSourceMessageId is null)
            throw new InvalidOperationException("压缩分支上下文需要明确的直接来源消息身份。");
        if (!ChatRequestBudget.Fits(configuration, ChatRequestBudget.Inputs(messages.Where(m => keep.Contains(m.Id)), context.SourceQuote)))
            throw new InvalidOperationException("必要原文已超过上下文预算；请调整配置或缩短当前问题。");

        // Pack contiguous older materials from left to right; full prefix chunks remain
        // stable when later messages are appended. Never cross a protected original.
        ContextPart[] Parts()
        {
            var parts = new List<ContextPart>();
            var run = new List<ChatMessage>();
            void Flush()
            {
                if (run.Count == 0) return;
                parts.AddRange(SplitMessages(targetConversationId, run, configuration).Select(c => new SummaryContextPart(c)));
                run.Clear();
            }
            foreach (var message in messages)
            {
                if (keep.Contains(message.Id)) { Flush(); parts.Add(new VerbatimContextPart(message.Id)); }
                else run.Add(message);
            }
            Flush();
            return parts.ToArray();
        }
        bool ReservedFits(ContextPart[] parts, int backgroundBytes) => ChatRequestBudget.Fits(configuration, ComposeUnchecked(MakePlan(parts), byId,
            parts.OfType<SummaryContextPart>().ToDictionary(p => p.Chunk.CacheKey,
                _ => new ContextSummaryContent(new string('x', backgroundBytes), "", "", "", ""))));
        var selected = Parts();
        // One ASCII character is the protocol-valid lower bound, not a promise of useful
        // compression. Actual generated content is always checked again before sending.
        if (!ReservedFits(selected, 1)) throw new InvalidOperationException("压缩分段及必要原文无法在预算内规划；请提高上下文预算。");
        foreach (var pair in exchanges.Skip(1))
        {
            var added = new[] { pair.User!.Id, pair.Answer.Id }.Where(id => keep.Add(id)).ToArray();
            var candidate = Parts();
            // Allocation heuristic only: 256 ASCII Background bytes per chunk plus exact
            // JSON/wrapper cost. Neither this nor max output tokens bounds provider bytes.
            if (ReservedFits(candidate, 256)) selected = candidate;
            else foreach (var id in added) keep.Remove(id);
        }
        return MakePlan(selected);
    }

    public static ChatRequest CreateSummaryRequest(Guid targetConversationId, ContextChunk chunk,
        IReadOnlyList<ChatMessage> originals, AiConfiguration configuration)
    {
        ValidateConfiguration(configuration);
        ValidateOriginals(originals);
        var byId = originals.ToDictionary(m => m.Id);
        ValidateChunk(chunk, byId);
        if (chunk.MaxOutputTokens != Math.Min(1024, configuration.MaxOutputTokens) ||
            chunk.CacheKey != CacheKey(targetConversationId, chunk.Slices, byId, configuration))
            throw new InvalidOperationException("压缩片段与原消息或生成配置不匹配。");
        var request = BuildSummaryRequest(chunk.Slices, byId, configuration);
        if (!ChatRequestBudget.Fits(request.Configuration, request.Messages))
            throw new InvalidOperationException("压缩请求超过上下文预算。");
        return request;
    }

    public static ChatInput[] ComposeInputs(ReplyContextPlan plan, IReadOnlyList<ChatMessage> originals,
        IReadOnlyDictionary<string, ContextSummaryContent> completedSummaries, AiConfiguration configuration)
    {
        ValidateConfiguration(configuration);
        ValidateOriginals(originals);
        ValidatePlan(plan, originals, configuration);
        var inputs = ComposeUnchecked(plan, originals.ToDictionary(m => m.Id), completedSummaries);
        if (!ChatRequestBudget.Fits(configuration, inputs))
            throw new InvalidOperationException("压缩后的完整回答请求仍超过上下文预算；原文已保留，请调整配置后重试。");
        return inputs;
    }

    private static bool IsTerminal(GenerationStatus status) => status is GenerationStatus.Completed or GenerationStatus.Stopped or GenerationStatus.Failed or GenerationStatus.Interrupted;
    private static void ValidateConfiguration(AiConfiguration configuration)
    {
        if (configuration.MaxOutputTokens <= 0 || configuration.ContextTokenBudget <= 0)
            throw new InvalidOperationException("上下文与输出预算必须为正整数。");
    }
}
