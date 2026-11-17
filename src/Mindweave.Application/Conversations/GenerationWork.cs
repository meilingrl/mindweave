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

    /// <summary>
    /// Prepared in advance, never asked for (a workspace's names, 2026-11-11): starts only when no user or background work
    /// waits, and gives its slot up as soon as such work would otherwise wait for it.
    /// </summary>
    Idle,
}

/// <summary>Work accepted for generation; the request (model snapshot and turns) is already frozen.</summary>
public abstract record GenerationWork(GenerationTaskRecord Task, ChatRequest Request, GenerationPriority Priority);

/// <param name="Plan">
/// When the plan summarizes older context, the summaries are produced in this task's slot before the answer
/// request, and <paramref name="Request"/>'s turns are replaced by the prepared ones.
/// </param>
/// <param name="Inputs">What the plan was made from, so the runner can plan again once attachment summaries exist.</param>
public sealed record AnswerWork(GenerationTaskRecord Task, ChatRequest Request, LiveAnswer Live, ContextPlan? Plan = null, ContextBudget? Budget = null, ContextInputs? Inputs = null)
    : GenerationWork(Task, Request, GenerationPriority.User);

/// <summary>The inputs of a context plan: the frozen ancestor chain, the conversation itself, and its attachments' candidates.</summary>
public sealed record ContextInputs(IReadOnlyList<Core.Branches.ContextSegment> Ancestors, Core.Branches.ContextSegment Own, IReadOnlyList<ResourceCandidate> Resources);

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

/// <summary>
/// Who a request without a task row is counted for (2026-11-11): the workspace and the kind of request, so its token use
/// is recorded (<c>ai_request</c> without a task, migration v10) and the statistics page can count it.
/// </summary>
public sealed record RequestAttribution(Core.Workspaces.WorkspaceId Workspace, string Kind)
{
    /// <summary>A workspace's suggested names, asked for while renaming it or prepared in the background.</summary>
    public const string WorkspaceNames = "workspace-name";

    /// <summary>Recommended questions for 新探索 (requirements §10.3.1), asked for with 换一批 or prepared in the background.</summary>
    public const string RecommendQuestions = "recommend-questions";

    /// <summary>Recommended nodes for the summary network (requirements §10.3.2).</summary>
    public const string RecommendNodes = "recommend-nodes";

    /// <summary>Suggested links between summaries (requirements §10.3.4), asked for only with 建议关联.</summary>
    public const string SuggestLinks = "suggest-links";

    /// <summary>Every kind a request without a task may be recorded for; storage refuses any other.</summary>
    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(StringComparer.Ordinal) { WorkspaceNames, RecommendQuestions, RecommendNodes, SuggestLinks };
}

/// <summary>
/// A request whose output is only shown and never stored as content (a workspace's suggested names, 2026-11-11). It takes a
/// slot like other work (<paramref name="Priority"/>: user when asked for, idle when prepared in the background) and waits
/// while storage is paused, but has no task row: nothing of it survives a restart, so there is nothing to mark interrupted
/// or keep a key for, and the exit prompt does not ask about it (an exit cancels it silently). <paramref name="Task"/> is an
/// identity in memory only. The request itself is recorded for <paramref name="Attribution"/> when one is given. The
/// output, or why there is none, arrives through <see cref="Output"/>: cancelled when stopped, a
/// <see cref="Ai.ChatClientException"/> (or <see cref="StorageAdmissionRefusedException"/>) when it failed, and a
/// <see cref="GenerationPreemptedException"/> when idle work gave its slot up. Idle work that must not be interrupted once
/// it runs (recommendations, architecture §6: "已在运行的不打断") sets <paramref name="Yields"/> false: it still starts only
/// when no user or background work waits, but keeps its slot until it ends.
/// </summary>
public sealed record TransientWork(GenerationTaskRecord Task, ChatRequest Request, GenerationPriority Priority, RequestAttribution? Attribution = null, bool Yields = true)
    : GenerationWork(Task, Request, Priority)
{
    public TaskCompletionSource<string> Output { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Idle work gave its slot up to user or background work that would otherwise have waited; it may be asked again later.</summary>
public sealed class GenerationPreemptedException : OperationCanceledException
{
    public GenerationPreemptedException()
        : base("Idle work gave its slot to other work.")
    {
    }

    public GenerationPreemptedException(string message)
        : base(message)
    {
    }

    public GenerationPreemptedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
