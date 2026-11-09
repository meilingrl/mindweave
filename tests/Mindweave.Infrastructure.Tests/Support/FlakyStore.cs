using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;

namespace Mindweave.Infrastructure.Tests;

/// <summary>Wraps the real store and fails the first N terminal answer writes.</summary>
internal sealed class FlakyStore(IConversationStore inner) : IConversationStore
{
    private int _failFinishes;

    public int FailFinishes
    {
        get => Volatile.Read(ref _failFinishes);
        set => Volatile.Write(ref _failFinishes, value);
    }

    /// <summary>Awaited at the start of every terminal answer write, before it fails or reaches the store.</summary>
    public Func<Task>? BeforeFinish { get; set; }

    /// <summary>What a failing terminal write throws; storage refusing it as a full disk unless a test sets another.</summary>
    public Func<Exception> FinishFailure { get; set; } = () => new StorageWriteException(StorageFailureKind.Full, "simulated disk full", null);

    public async Task<bool> FinishAnswerAsync(GenerationTaskId task, MessageId answer, IReadOnlyList<ContentBlock> blocks, long revision, MessageState state, GenerationErrorKind? errorKind, string? errorDetail, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (BeforeFinish is { } before)
        {
            await before();
        }

        return Interlocked.Decrement(ref _failFinishes) >= 0
            ? throw FinishFailure()
            : await inner.FinishAnswerAsync(task, answer, blocks, revision, state, errorKind, errorDetail, at, cancellationToken);
    }

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(Core.Workspaces.WorkspaceId workspace, CancellationToken cancellationToken) => inner.ListAsync(workspace, cancellationToken);

    public Task<Conversation?> GetAsync(ConversationId id, CancellationToken cancellationToken) => inner.GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<Message>> GetMessagesAsync(ConversationId id, CancellationToken cancellationToken) => inner.GetMessagesAsync(id, cancellationToken);

    public Task CreateAsync(Conversation conversation, Message question, Message answer, GenerationTaskRecord task, CancellationToken cancellationToken) => inner.CreateAsync(conversation, question, answer, task, cancellationToken);

    public Task<(Message Question, Message Answer)> AppendQuestionAsync(Message question, Message answer, GenerationTaskRecord task, CancellationToken cancellationToken) => inner.AppendQuestionAsync(question, answer, task, cancellationToken);

    public Task<Message> AddRetryAsync(Message answer, GenerationTaskRecord task, CancellationToken cancellationToken) => inner.AddRetryAsync(answer, task, cancellationToken);

    public Task AddTaskAsync(GenerationTaskRecord task, CancellationToken cancellationToken) => inner.AddTaskAsync(task, cancellationToken);

    public Task<bool> MarkRunningAsync(GenerationTaskId task, MessageId? answer, DateTimeOffset at, CancellationToken cancellationToken) => inner.MarkRunningAsync(task, answer, at, cancellationToken);

    public Task<bool> CheckpointAsync(MessageId answer, IReadOnlyList<ContentBlock> blocks, long revision, CancellationToken cancellationToken) => inner.CheckpointAsync(answer, blocks, revision, cancellationToken);

    public Task FinishTaskAsync(GenerationTaskId task, GenerationTaskState state, GenerationErrorKind? errorKind, string? errorDetail, DateTimeOffset at, CancellationToken cancellationToken) => inner.FinishTaskAsync(task, state, errorKind, errorDetail, at, cancellationToken);

    public Task<bool> SetAutoTitleAsync(ConversationId id, string title, DateTimeOffset at, CancellationToken cancellationToken) => inner.SetAutoTitleAsync(id, title, at, cancellationToken);

    public Task SetManualTitleAsync(ConversationId id, string title, DateTimeOffset at, CancellationToken cancellationToken) => inner.SetManualTitleAsync(id, title, at, cancellationToken);

    public Task<RecoveryReport> RecoverUnfinishedAsync(DateTimeOffset at, CancellationToken cancellationToken) => inner.RecoverUnfinishedAsync(at, cancellationToken);

    public Task<IReadOnlySet<string>> CredentialRefsInUseAsync(CancellationToken cancellationToken) => inner.CredentialRefsInUseAsync(cancellationToken);
}
