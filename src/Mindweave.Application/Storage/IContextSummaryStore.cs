using Mindweave.Core.Context;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Storage;

/// <summary>A stored context summary: its identity, the model that wrote it, and its text.</summary>
public sealed record ContextSummary(Guid Id, SummaryKey Key, string Model, string Text, DateTimeOffset CreatedAt);

/// <summary>Context summaries (architecture §6.2). Not user content: never listed, never part of the network.</summary>
public interface IContextSummaryStore
{
    /// <summary>Every summary owned by the given conversations, for planning and reuse.</summary>
    Task<IReadOnlyList<ContextSummary>> ListAsync(IReadOnlyCollection<ConversationId> conversations, CancellationToken cancellationToken);

    /// <summary>Stores a summary; if one with the same key exists (another task got there first), returns that one.</summary>
    Task<ContextSummary> AddAsync(ContextSummary summary, CancellationToken cancellationToken);

    /// <summary>Records which summaries a task sent (MW-C16).</summary>
    Task RecordTaskInputAsync(GenerationTaskId task, IReadOnlyList<Guid> summaries, CancellationToken cancellationToken);

    /// <summary>Answers in the conversation whose task sent at least one summary, for the "compressed" notice.</summary>
    Task<IReadOnlySet<MessageId>> CompressedAnswersAsync(ConversationId conversation, CancellationToken cancellationToken);
}
