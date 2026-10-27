using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.Summaries;

public sealed class KnowledgeSummaryInputException(string code, string? message = null) : InvalidOperationException(message ?? code)
{
    public string Code { get; } = code;
}

/// <summary>Pure bounded planner. Caller supplies all owned rows and exactly authorized frozen branch material; store independently validates authority.</summary>
public static class KnowledgeSummaryInputPlanner
{
    public const int FormatVersion = 1;
    public const int PreparedFormatVersion = 1;

    public static KnowledgeSummaryInputPlan Plan(KnowledgeSummaryMaterial material, AiConfiguration configuration)
    {
        ValidateMaterial(material);
        if (configuration.ContextTokenBudget <= 0 || configuration.MaxOutputTokens <= 0)
            throw Error("KnowledgeSummaryInvalidBudget");
        var own = ChatHistory.LatestInput(material.OwnRows.OrderBy(m => m.Sequence).ToArray()).ToImmutableArray();
        if (own.IsEmpty || own.All(m => string.IsNullOrWhiteSpace(m.Body))) throw Error("KnowledgeSummaryEmptyOwnSource");
        var ownRefs = own.Select((m, i) => Reference(m, i, SummarySourceScope.Own)).ToImmutableArray();
        var ancestorRefs = material.FrozenAncestors.Select((m, i) => Reference(m, i, SummarySourceScope.FrozenAncestor)).ToImmutableArray();
        SummaryQuoteRef? quote = null;
        if (material.DirectSource is { } source)
        {
            var original = material.FrozenAncestors.Single(m => m.Id == source.MessageId);
            quote = new(source, Hash(source.Quote), Hash(JsonSerializer.Serialize(source)), Hash(original.Body), original.Revision);
        }
        var coverage = new KnowledgeSummaryCoverage(material.ConversationId, material.OwnRows.Max(m => m.Sequence), ownRefs, ancestorRefs, material.FrozenRanges, quote);
        var uses = ImmutableArray.CreateBuilder<SummaryMaterialReference>();
        uses.Add(new(0, SummaryMaterialKind.Instruction, 0, "$", null, Hash(KnowledgeSummaryProtocol.Instruction)));
        foreach (var r in ownRefs) uses.Add(new(uses.Count, SummaryMaterialKind.OwnMessage, 1, $"$.OwnConversation[{r.Ordinal}]", r.MessageId, r.BodyHash));
        foreach (var r in ancestorRefs) uses.Add(new(uses.Count, SummaryMaterialKind.FrozenAncestor, 1, $"$.FrozenBranchBackground[{r.Ordinal}]", r.MessageId, r.BodyHash));
        if (quote is not null) uses.Add(new(uses.Count, SummaryMaterialKind.DirectQuote, 1, "$.DirectQuote", quote.Source.MessageId, quote.BindingHash));
        var request = Compose(configuration, own, material.FrozenAncestors, quote);
        if (!ChatRequestBudget.Fits(configuration, request.Messages)) throw new KnowledgeSummaryInputException("KnowledgeSummaryBudgetExceeded", "完整知识总结输入与输出预留超过预算。请提高上下文预算或选择较短会话；未接受任务。");
        var provisional = new KnowledgeSummaryInputPlan(FormatVersion, KnowledgeSummaryProtocol.Version, ChatRequestBudget.EstimatorProfile,
            PreparedFormatVersion, coverage, uses.ToImmutable(), configuration, "");
        return provisional with { RequestHash = RequestHash(provisional, request) };
    }

    public static ChatRequest CreateRequest(KnowledgeSummaryInputPlan plan, KnowledgeSummaryMaterial material)
    {
        if (plan.FormatVersion != FormatVersion || plan.PreparedFormatVersion != PreparedFormatVersion ||
            plan.ProtocolVersion != KnowledgeSummaryProtocol.Version || plan.EstimatorProfile != ChatRequestBudget.EstimatorProfile)
            throw Error("KnowledgeSummaryUnsupportedFormat");
        var rebuilt = Plan(material, plan.Configuration);
        // Compares all reference metadata as well as physical request/configuration; no repair on mismatch.
        if (JsonSerializer.Serialize(plan) != JsonSerializer.Serialize(rebuilt)) throw Error("KnowledgeSummaryFrozenInputMismatch");
        var byId = material.OwnRows.ToDictionary(m => m.Id);
        return Compose(plan.Configuration, plan.Coverage.OwnMessages.Select(r => byId[r.MessageId]).ToImmutableArray(), material.FrozenAncestors, plan.Coverage.DirectQuote);
    }

    public static bool HasLaterContent(KnowledgeSummaryCoverage coverage, IEnumerable<ChatMessage> messages) => messages.Any(m =>
        m.ConversationId == coverage.ConversationId && m.Sequence > coverage.OwnHighWaterSequence &&
        m.Status is GenerationStatus.Completed or GenerationStatus.Stopped or GenerationStatus.Failed or GenerationStatus.Interrupted);

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(KnowledgeSummaryProtocol.StrictUtf8.GetBytes(text)));

    private static string RequestHash(KnowledgeSummaryInputPlan plan, ChatRequest request) => Hash(JsonSerializer.Serialize(new
    {
        plan.FormatVersion, plan.PreparedFormatVersion, plan.ProtocolVersion, plan.EstimatorProfile,
        Purpose = request.Purpose.ToString(), request.Configuration, plan.Coverage, plan.Materials, request.Messages
    }));

    private static ChatRequest Compose(AiConfiguration configuration, ImmutableArray<ChatMessage> own,
        ImmutableArray<ChatMessage> ancestors, SummaryQuoteRef? quote)
    {
        static object Entry(ChatMessage m) => new { m.Id, m.ConversationId, m.Sequence, m.Revision, Role = m.Role.ToString(), Status = m.Status.ToString(), m.ReplyToUserId, m.SupersedesMessageId, m.Body };
        var envelope = JsonSerializer.Serialize(new
        {
            OwnConversation = own.Select(Entry), FrozenBranchBackground = ancestors.Select(Entry), DirectQuote = quote
        });
        return new(configuration, ImmutableArray.Create(new ChatInput("system", KnowledgeSummaryProtocol.Instruction), new ChatInput("user", envelope)), ChatRequestPurpose.Standard);
    }

    private static KnowledgeSummarySourceRef Reference(ChatMessage m, int ordinal, SummarySourceScope scope) =>
        new(ordinal, scope, m.Id, m.ConversationId, m.Sequence, m.Revision, Hash(m.Body), m.Role, m.Status, m.ReplyToUserId, m.SupersedesMessageId);

    private static void ValidateMaterial(KnowledgeSummaryMaterial material)
    {
        if (material.OwnRows.IsDefaultOrEmpty) throw Error("KnowledgeSummaryEmptyOwnSource");
        if (material.FrozenAncestors.IsDefault || material.FrozenRanges.IsDefault) throw Error("KnowledgeSummaryInvalidSources");
        var all = material.OwnRows.Concat(material.FrozenAncestors).ToArray();
        if (all.Select(m => m.Id).Distinct().Count() != all.Length || material.OwnRows.Any(m => m.ConversationId != material.ConversationId) ||
            material.OwnRows.Select(m => m.Sequence).Distinct().Count() != material.OwnRows.Length ||
            all.Any(m => m.Id == Guid.Empty || m.Sequence <= 0 || m.Revision < 0 || !Enum.IsDefined(m.Role) || !Enum.IsDefined(m.Status)))
            throw Error("KnowledgeSummaryInvalidSources");
        if (all.Any(m => m.Status is GenerationStatus.Queued or GenerationStatus.Running)) throw Error("KnowledgeSummaryActiveReply");
        foreach (var m in all)
        {
            try { KnowledgeSummaryProtocol.ValidateUnicode(m.Body); }
            catch (SummaryProtocolException) { throw Error("KnowledgeSummaryInvalidUnicode"); }
        }
        var owned = material.OwnRows.ToDictionary(m => m.Id);
        foreach (var message in material.OwnRows)
        {
            if (message.Role == MessageRole.User)
            {
                if (message.ReplyToUserId is not null || message.SupersedesMessageId is not null || message.Status != GenerationStatus.Completed)
                    throw Error("KnowledgeSummaryInvalidUserSource");
            }
            else if (message.ReplyToUserId is not { } parentId || !owned.TryGetValue(parentId, out var parent) ||
                parent.Role != MessageRole.User || parent.Sequence >= message.Sequence)
                throw Error("KnowledgeSummaryOrphanAttempt");
            if (message.SupersedesMessageId is { } previousId && (!owned.TryGetValue(previousId, out var previous) ||
                previous.Role != MessageRole.Assistant || previous.ReplyToUserId != message.ReplyToUserId || previous.Sequence >= message.Sequence))
                throw Error("KnowledgeSummaryInvalidSupersededAttempt");
        }
        var ranges = material.FrozenRanges.SelectMany(r => r.MessageIds.Select(id => (r.ConversationId, Id: id))).ToArray();
        if (!ranges.SequenceEqual(material.FrozenAncestors.Select(m => (m.ConversationId, m.Id))) ||
            material.FrozenAncestors.Any(m => m.ConversationId == material.ConversationId)) throw Error("KnowledgeSummaryUnauthorizedBackground");
        if (material.DirectSource is { } source)
        {
            if (source.ChildConversationId != material.ConversationId || string.IsNullOrWhiteSpace(source.Quote) ||
                !material.FrozenAncestors.Any(m => m.Id == source.MessageId && m.ConversationId == source.ParentConversationId && m.Role == MessageRole.Assistant))
                throw Error("KnowledgeSummaryUnboundQuote");
            KnowledgeSummaryProtocol.ValidateUnicode(source.Quote);
        }
        else if (!material.FrozenAncestors.IsEmpty) throw Error("KnowledgeSummaryUnboundQuote");
    }
    private static KnowledgeSummaryInputException Error(string code) => new(code);
}
