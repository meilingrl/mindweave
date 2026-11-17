using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;

namespace Mindweave.Application.Storage;

/// <summary>
/// One step of a run as stored (architecture §15.3, migration v17): a model request (<see cref="AiRequestId"/> names its
/// recorded request) or one tool execution (<see cref="Tool"/>, its <see cref="Risk"/>, the short <see cref="Input"/>, the
/// stored <see cref="Result"/>).
/// </summary>
public sealed record RunStepRecord(
    GenerationTaskId Task,
    int Ordinal,
    RunStepKind Kind,
    RunStepState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt = null,
    Guid? AiRequestId = null,
    string? CallId = null,
    string? Tool = null,
    ToolRisk? Risk = null,
    string? Input = null,
    ResourceId? Result = null,
    GenerationErrorKind? ErrorKind = null,
    string? ErrorDetail = null)
{
    public TimeSpan? Duration => FinishedAt is { } end ? end - StartedAt : null;
}

/// <summary>The run of one answer: its steps in order, and the limit that ended its use of tools, if one did.</summary>
public sealed record RunRecord(MessageId Answer, IReadOnlyList<RunStepRecord> Steps, RunLimit? LimitReached);

/// <summary>Runs and their steps (architecture §15.12). Writes are guarded like an answer's checkpoints.</summary>
public interface IRunStore
{
    /// <summary>Records a step that has started (state running).</summary>
    Task AddStepAsync(RunStepRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Records how a step ended, and in the same transaction, when <paramref name="blocks"/> are given, the answer's blocks so
    /// far — only while the answer is still streaming and <paramref name="revision"/> is newer, as a checkpoint is. A tool step's
    /// result resource is marked as made by it. Returns whether the blocks were written.
    /// </summary>
    Task<bool> FinishStepAsync(RunStepRecord record, MessageId answer, IReadOnlyList<ContentBlock>? blocks, long revision, CancellationToken cancellationToken);

    /// <summary>Records which limit ended the run's use of tools.</summary>
    Task SetLimitReachedAsync(GenerationTaskId task, RunLimit limit, CancellationToken cancellationToken);

    /// <summary>The runs of the conversation's answers that offered tools, by answer.</summary>
    Task<IReadOnlyDictionary<MessageId, RunRecord>> RunsAsync(ConversationId conversation, CancellationToken cancellationToken);
}
