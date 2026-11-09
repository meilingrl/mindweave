using Mindweave.Application.Ai;
using Mindweave.Application.Storage;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Conversations;

public sealed class GenerationOptions
{
    /// <summary>Global limit on concurrent AI requests (architecture §6).</summary>
    public int Slots { get; init; } = 2;

    /// <summary>Minimum gap between partial-answer writes; only the newest text is written.</summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(1);
}

public enum GenerationPriority
{
    User,
    Background,
}

/// <summary>Work accepted for generation; the request (model snapshot and turns) is already frozen.</summary>
public abstract record GenerationWork(GenerationTaskRecord Task, ChatRequest Request, GenerationPriority Priority);

/// <param name="Plan">
/// When the plan summarizes older context, the summaries are produced in this task's slot before the answer
/// request, and <paramref name="Request"/>'s turns are replaced by the prepared ones.
/// </param>
public sealed record AnswerWork(GenerationTaskRecord Task, ChatRequest Request, LiveAnswer Live, ContextPlan? Plan = null, ContextBudget? Budget = null)
    : GenerationWork(Task, Request, GenerationPriority.User);

public sealed record TitleWork(GenerationTaskRecord Task, ChatRequest Request)
    : GenerationWork(Task, Request, GenerationPriority.Background);

public sealed record AnswerOutcome(ConversationId ConversationId, MessageId MessageId, MessageState State, string Text);

/// <summary>User-started work that has not finished (running or queued): what the exit prompt names, by kind.</summary>
public readonly record struct UnfinishedUserWork(int Answers, int Summaries)
{
    public bool Any => Answers + Summaries > 0;
}

/// <summary>What became of a usable summary output handed to <see cref="SummaryWork.Save"/>.</summary>
public enum SummarySaveResult
{
    /// <summary>Stored together with the task's completion.</summary>
    Saved,

    /// <summary>The output had no usable title and body; nothing was stored.</summary>
    Unusable,

    /// <summary>The task was no longer running (stopped meanwhile); nothing was stored.</summary>
    NotRunning,
}

/// <summary>
/// A summary of a conversation (S4). Like an answer it may first summarize older context in its own slot. The
/// output stays in memory until <paramref name="Save"/> stores it with the task's completion; a failed, stopped or
/// interrupted task leaves no version (MW-C06).
/// </summary>
public sealed record SummaryWork(GenerationTaskRecord Task, ChatRequest Request, ContextPlan? Plan, ContextBudget? Budget, Func<string, Task<SummarySaveResult>> Save)
    : GenerationWork(Task, Request, GenerationPriority.User);

/// <summary>Background naming of same-name summary nodes; <paramref name="Apply"/> receives the complete output.</summary>
public sealed record NamingWork(GenerationTaskRecord Task, ChatRequest Request, Func<string, Task> Apply)
    : GenerationWork(Task, Request, GenerationPriority.Background);
