using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Storage;

public enum GenerationTaskKind
{
    Answer,
    Title,

    /// <summary>Generates a summary version of a conversation (S4).</summary>
    Summary,

    /// <summary>Distinguishes summary nodes whose base titles collide (§10.1).</summary>
    Naming,
}

public enum GenerationTaskState
{
    Queued,
    Running,
    Completed,
    Stopped,
    Failed,
    Interrupted,
    Cancelled,
}

/// <summary>A generation task as accepted: frozen model snapshot and the message IDs it was given.</summary>
/// <param name="SummaryToSeq">For a summary task, the frozen coverage: the conversation up to this sequence number.</param>
/// <param name="SummaryBaseline">For a summary task, the summary version that was current when it was accepted, if any.</param>
public sealed record GenerationTaskRecord(
    GenerationTaskId Id,
    GenerationTaskKind Kind,
    ConversationId ConversationId,
    MessageId? TargetMessageId,
    ModelSnapshot Model,
    IReadOnlyList<MessageId> InputMessageIds,
    DateTimeOffset CreatedAt,
    int? SummaryToSeq = null,
    Guid? SummaryBaseline = null);

/// <summary>
/// Sidebar row: the conversation plus what is needed to label and place it without loading all messages.
/// A branch carries its parent and the quote it was created from. <paramref name="IsArchived"/>: the user archived it;
/// <paramref name="IsHidden"/>: archived itself or below an archived ancestor (the sidebar leaves it out).
/// </summary>
public sealed record ConversationSummary(
    Conversation Conversation,
    string? FirstQuestion,
    MessageState? LastAnswerState,
    ConversationId? Parent = null,
    string? Quote = null,
    bool IsArchived = false,
    bool IsHidden = false);

public sealed record RecoveryReport(int InterruptedAnswers, int InterruptedTasks);

/// <summary>
/// Conversation persistence. Every method is one transaction; methods returning bool report whether a
/// guarded update applied (false means the row was already in a later state, which callers must respect).
/// </summary>
public interface IConversationStore
{
    Task<IReadOnlyList<ConversationSummary>> ListAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    Task<Conversation?> GetAsync(ConversationId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Message>> GetMessagesAsync(ConversationId id, CancellationToken cancellationToken);

    /// <summary>Creates the conversation, the question, the pending answer and its task together.</summary>
    Task CreateAsync(Conversation conversation, Message question, Message answer, GenerationTaskRecord task, CancellationToken cancellationToken);

    /// <summary>
    /// Appends question + pending answer + task. Sequence numbers are assigned inside the transaction.
    /// Throws <see cref="AnswerInProgressException"/> if the conversation still has an unfinished answer,
    /// <see cref="ConversationArchivedException"/> if it is hidden by an archive, <see cref="ConversationMissingException"/>
    /// if it was deleted.
    /// </summary>
    Task<(Message Question, Message Answer)> AppendQuestionAsync(Message question, Message answer, GenerationTaskRecord task, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a new attempt superseding the last answer; refuses unless that answer is last and finished, and (like
    /// <see cref="AppendQuestionAsync"/>) when the conversation is hidden by an archive or was deleted.
    /// </summary>
    Task<Message> AddRetryAsync(Message answer, GenerationTaskRecord task, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a task row. Throws <see cref="ConversationMissingException"/> if the conversation was deleted, and
    /// <see cref="ConversationArchivedException"/> for any task but naming of a conversation hidden by an archive.
    /// </summary>
    Task AddTaskAsync(GenerationTaskRecord task, CancellationToken cancellationToken);

    /// <summary>Queued → running; for answers also pending → streaming. False if it was cancelled meanwhile.</summary>
    Task<bool> MarkRunningAsync(GenerationTaskId task, MessageId? answer, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Writes partial content only if still streaming and newer than what is stored.</summary>
    Task<bool> CheckpointAsync(MessageId answer, IReadOnlyList<ContentBlock> blocks, long revision, CancellationToken cancellationToken);

    /// <summary>Writes the terminal answer and task state together, only if the answer is not yet terminal.</summary>
    Task<bool> FinishAnswerAsync(
        GenerationTaskId task,
        MessageId answer,
        IReadOnlyList<ContentBlock> blocks,
        long revision,
        MessageState state,
        GenerationErrorKind? errorKind,
        string? errorDetail,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    Task FinishTaskAsync(GenerationTaskId task, GenerationTaskState state, GenerationErrorKind? errorKind, string? errorDetail, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// Sets an automatic title unless the user has named the conversation or it is hidden by an archive. Returns whether
    /// it applied.
    /// </summary>
    Task<bool> SetAutoTitleAsync(ConversationId id, string title, DateTimeOffset at, CancellationToken cancellationToken);

    Task SetManualTitleAsync(ConversationId id, string title, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Startup: everything not finished by the previous process becomes interrupted. Never re-sends.</summary>
    Task<RecoveryReport> RecoverUnfinishedAsync(DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Credential references still needed by unfinished tasks (cleanup must keep them).</summary>
    Task<IReadOnlySet<string>> CredentialRefsInUseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Checks that storage takes writes again after a failure: takes the write lock and grows the file by a typical
    /// write's size, then rolls back, so nothing is stored. Throws <see cref="StorageWriteException"/> while it does not.
    /// </summary>
    Task ProbeWriteAsync(CancellationToken cancellationToken);
}

public sealed class AnswerInProgressException : Exception
{
    public AnswerInProgressException()
        : base("The conversation already has an unfinished answer.")
    {
    }

    public AnswerInProgressException(string message)
        : base(message)
    {
    }

    public AnswerInProgressException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
