using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Storage;
using Mindweave.Core.Branches;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Summaries;

public enum SummarizeRejection
{
    NotConfigured,
    StorageUnavailable,

    /// <summary>The conversation has no finished answer yet.</summary>
    NothingToSummarize,

    /// <summary>An answer of this conversation is queued or being generated; finish or stop it first (§6.4).</summary>
    AnswerInProgress,

    SummaryInProgress,

    /// <summary>A candidate waits; adopt or discard it before generating another (§8.1).</summary>
    HasCandidate,

    /// <summary>Even with older context summarized, the conversation does not fit the model.</summary>
    TooLongForModel,

    /// <summary>The conversation is archived or below an archived one; it is restored first (D-021).</summary>
    ConversationArchived,

    /// <summary>The conversation was deleted meanwhile.</summary>
    ConversationMissing,
}

public sealed record SummarizeOutcome(SummarizeRejection? Rejection, GenerationTaskId? Task)
{
    public bool Accepted => Rejection is null;
}

/// <summary>Whether adopting a candidate replaces a current version the candidate was not generated from (§8.2).</summary>
public sealed record AdoptCheck(bool BaselineChanged, SummaryVersion Current, SummaryVersion Candidate);

/// <summary>
/// Summary use cases (S4): accepting a summary (frozen coverage, one unfinished task and one candidate per
/// conversation), saving it only on success, version operations, reading state, and two-level naming.
/// </summary>
public sealed class SummaryService : ISummaries
{
    /// <summary>Output cap for a summary; reasoning models spend hidden reasoning against it (D-015).</summary>
    public const int SummaryOutputTokens = 16_384;

    private const int SourceExcerpt = 3_000;

    private const string Instructions = """
        你为用户整理一次探索对话的知识总结。只依据对话内容，不补充对话里没有的事实。
        保留：问题背景、主要解释、仍未解决的问题，以及原回答中的不确定之处（照实标明，不要改成确定的说法）。用户在对话中纠正过的内容，以纠正后的为准。
        输出格式：第一行写“标题：”加一个标题（不超过 20 个字，表达知识点或话题，而不是复述提问）；第二行写“短名：”加一个 2 到 8 个字的短名，用作知识网络中节点的名字，只保留最核心的知识点；空一行后用 Markdown 写正文，可以用小标题和列表。使用对话所用的语言。
        """;

    private const string Request = "请按要求总结以上对话。";

    private readonly ISummaryStore _summaries;
    private readonly IConversationStore _conversations;
    private readonly IBranchStore _branches;
    private readonly IContextSummaryStore? _contextSummaries;
    private readonly GenerationCoordinator _generation;
    private readonly ContextBudgets _budgets;
    private readonly StorageHealth _health;
    private readonly SummaryDrafts _drafts;
    private readonly ModelSettingsService _models;
    private readonly IClock _clock;
    private readonly SummaryNaming _naming;

    public SummaryService(
        ISummaryStore summaries,
        IConversationStore conversations,
        IBranchStore branches,
        GenerationCoordinator generation,
        ContextBudgets budgets,
        StorageHealth health,
        SummaryDrafts drafts,
        ModelSettingsService models,
        IClock clock,
        ILogger<SummaryService> logger,
        IContextSummaryStore? contextSummaries = null)
    {
        _summaries = summaries;
        _conversations = conversations;
        _branches = branches;
        _contextSummaries = contextSummaries;
        _generation = generation;
        _budgets = budgets;
        _health = health;
        _drafts = drafts;
        _models = models;
        _clock = clock;
        _naming = new SummaryNaming(summaries, conversations, generation, health, models, clock, logger, id => Changed?.Invoke(this, id));
        _generation.SummaryTaskFinished += (_, task) =>
        {
            _naming.OnTaskFinished(task);
            Changed?.Invoke(this, task.ConversationId);
        };
    }

    /// <summary>A conversation's summary node, versions, names or summary task changed. Raised on worker threads.</summary>
    public event EventHandler<ConversationId>? Changed;

    public Task<IReadOnlyList<SummaryEntry>> ListAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        _summaries.ListAsync(workspace, cancellationToken);

    public Task<SummaryEntry?> ForConversationAsync(ConversationId conversation, CancellationToken cancellationToken) =>
        _summaries.ForConversationAsync(conversation, cancellationToken);

    public Task<SummaryDetail?> GetAsync(SummaryNodeId node, CancellationToken cancellationToken) =>
        _summaries.GetAsync(node, cancellationToken);

    public Task<SummaryTaskStatus?> LatestTaskAsync(ConversationId conversation, CancellationToken cancellationToken) =>
        _summaries.LatestTaskAsync(conversation, cancellationToken);

    /// <summary>
    /// Accepts "summarize this conversation": freezes the coverage (everything finished so far), stores the task,
    /// then queues it. Nothing is written for the summary itself until it succeeds.
    /// </summary>
    public async Task<SummarizeOutcome> SummarizeAsync(ConversationId conversation, CancellationToken cancellationToken)
    {
        if (_health.IsBlocked)
        {
            return Reject(SummarizeRejection.StorageUnavailable);
        }

        if (await _models.GetActiveSnapshotAsync(cancellationToken).ConfigureAwait(false) is not { } active)
        {
            return Reject(SummarizeRejection.NotConfigured);
        }

        var messages = await _conversations.GetMessagesAsync(conversation, cancellationToken).ConfigureAwait(false);
        if (ConversationRules.HasUnfinishedAnswer(messages) || _generation.HasUnfinishedAnswer(conversation))
        {
            return Reject(SummarizeRejection.AnswerInProgress);
        }

        if (!messages.Any(m => m is { Role: MessageRole.Assistant, State: MessageState.Complete }))
        {
            return Reject(SummarizeRejection.NothingToSummarize);
        }

        var existing = await _summaries.ForConversationAsync(conversation, cancellationToken).ConfigureAwait(false);
        if (existing?.Node.Pending is not null)
        {
            return Reject(SummarizeRejection.HasCandidate);
        }

        // The stored task state decides: its row is written before the task is queued, and it turns terminal when the
        // version is saved, a moment before the task's slot is released.
        if (await _summaries.LatestTaskAsync(conversation, cancellationToken).ConfigureAwait(false) is { State: GenerationTaskState.Queued or GenerationTaskState.Running })
        {
            return Reject(SummarizeRejection.SummaryInProgress);
        }

        var model = active with { MaxOutputTokens = SummaryOutputTokens };
        var toSeq = messages.Where(m => m.IsFinished).Max(m => m.Seq);
        var covered = messages.Where(m => m.Seq <= toSeq).ToList();
        var system = new List<ChatTurn> { new(ChatRole.System, Instructions) };
        if (await BackgroundAsync(conversation, cancellationToken).ConfigureAwait(false) is { } background)
        {
            system.Add(new ChatTurn(ChatRole.System, background));
        }

        // The request is the last "question", so the planner keeps it and the recent turns, and summarizes older
        // context in this task's own slot when the conversation does not fit (S3). It is never stored.
        var now = _clock.UtcNow;
        var request = new Message(MessageId.New(), conversation, toSeq + 1, MessageRole.User, ContentBlocks.FromText(Request), MessageState.Complete, 0, null, null, null, now, now);
        var budget = await _budgets.BudgetForAsync(model, cancellationToken).ConfigureAwait(false);
        var planned = await PlanAsync(conversation, [.. covered, request], budget.Available - system.Sum(t => TokenEstimator.Message(t.Content)), cancellationToken).ConfigureAwait(false);
        if (planned.Plan is not { } plan)
        {
            return Reject(SummarizeRejection.TooLongForModel);
        }

        var task = new GenerationTaskRecord(
            GenerationTaskId.New(), GenerationTaskKind.Summary, conversation, null, model, [.. plan.RawItems.Select(i => i.Message.Id).Where(id => id != request.Id)], now,
            toSeq, existing?.Node.Current.Value);
        try
        {
            // Checked inside the write: a hidden conversation never gets a summary task, whatever the page showed.
            await _conversations.AddTaskAsync(task, cancellationToken).ConfigureAwait(false);
        }
        catch (ConversationArchivedException)
        {
            return Reject(SummarizeRejection.ConversationArchived);
        }
        catch (ConversationMissingException)
        {
            return Reject(SummarizeRejection.ConversationMissing);
        }
        catch (StorageWriteException ex)
        {
            _health.Report(ex);
            return Reject(SummarizeRejection.StorageUnavailable);
        }

        var turns = new ChatRequest(model, [.. system, .. ContextCompressor.Unsummarized(plan).Turns]);
        await _generation.EnqueueOrCancelAsync(new SummaryWork(task, turns, plan, budget, output => SaveAsync(task, output))).ConfigureAwait(false);
        Changed?.Invoke(this, conversation);
        return new SummarizeOutcome(null, task.Id);
    }

    public Task StopAsync(GenerationTaskId task) => _generation.StopTaskAsync(task);

    public async Task<AdoptCheck?> CheckAdoptAsync(SummaryNodeId node, CancellationToken cancellationToken)
    {
        var detail = await _summaries.GetAsync(node, cancellationToken).ConfigureAwait(false);
        if (detail?.Pending is not { } candidate)
        {
            return null;
        }

        return new AdoptCheck(candidate.Baseline != detail.Node.Current, detail.Current, candidate);
    }

    public Task AdoptAsync(SummaryNodeId node, SummaryVersionId candidate, CancellationToken cancellationToken) =>
        CommitAsync(() => AfterCurrentChangeAsync(node, () => _drafts.RunVersionOperationAsync(node, () => Done(_summaries.AdoptAsync(node, candidate, _clock.UtcNow, cancellationToken))), cancellationToken));

    public Task DiscardAsync(SummaryNodeId node, SummaryVersionId candidate, CancellationToken cancellationToken) =>
        CommitAsync(() => NotifyAsync(node, () => _summaries.DiscardAsync(node, candidate, _clock.UtcNow, cancellationToken), cancellationToken));

    public Task SetCurrentAsync(SummaryNodeId node, SummaryVersionId version, CancellationToken cancellationToken) =>
        CommitAsync(() => AfterCurrentChangeAsync(node, () => _drafts.RunVersionOperationAsync(node, () => Done(_summaries.SetCurrentAsync(node, version, _clock.UtcNow, cancellationToken))), cancellationToken));

    /// <summary>Deletes versions; returns the new current version, or null when the node was deleted with its last version.</summary>
    public async Task<SummaryVersionId?> DeleteVersionsAsync(SummaryNodeId node, IReadOnlySet<SummaryVersionId> versions, CancellationToken cancellationToken)
    {
        var detail = await _summaries.GetAsync(node, cancellationToken).ConfigureAwait(false);
        var result = await CommitAsync(() => _drafts.RunVersionOperationAsync(node, () => _summaries.DeleteVersionsAsync(node, versions, _clock.UtcNow, cancellationToken))).ConfigureAwait(false);
        if (detail is not null)
        {
            if (result.NewCurrent is { } current && current != detail.Node.Current)
            {
                await _naming.CheckNameAsync(node, cancellationToken).ConfigureAwait(false);
            }

            Changed?.Invoke(this, detail.Node.Conversation);
        }

        return result.NewCurrent;
    }

    public Task<bool> FlushDraftsAsync() => _drafts.FlushAsync();

    /// <summary>Opens an edit of the node's current version, continuing its newest draft (memory first, then stored).</summary>
    public Task<DraftRead> OpenDraftAsync(SummaryNodeId node, SummaryVersionId current, CancellationToken cancellationToken) =>
        _drafts.OpenAsync(node, current, cancellationToken);

    /// <summary>
    /// The newest input of an edit session, autosaved shortly after. Allowed while storage is blocked: it persists input
    /// already given. Null when the session is stale (saved or discarded elsewhere since it opened).
    /// </summary>
    public long? SetDraft(SummaryNodeId node, long generation, SummaryVersionId baseline, string title, string body) =>
        _drafts.Set(node, generation, baseline, title, body);

    /// <summary>Discards the session's draft; refused (<see cref="SummaryRefusal.Changed"/>) for a stale session.</summary>
    public async Task DiscardDraftAsync(SummaryNodeId node, long generation, long revision, CancellationToken cancellationToken)
    {
        var before = await _summaries.GetAsync(node, cancellationToken).ConfigureAwait(false);
        await _drafts.DiscardAsync(node, generation, revision).ConfigureAwait(false);
        if (before is not null)
        {
            Changed?.Invoke(this, before.Node.Conversation);
        }
    }

    /// <summary>Saves an edit as a new current version without a page's edit session (it opens one itself).</summary>
    public async Task<SummaryVersionId?> SaveEditAsync(SummaryNodeId node, SummaryVersionId baseline, string title, string body, CancellationToken cancellationToken)
    {
        var session = await _drafts.OpenAsync(node, baseline, cancellationToken).ConfigureAwait(false);
        return await SaveEditAsync(node, baseline, title, body, session.Generation, session.Revision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves the session's edit of <paramref name="baseline"/> as a new current version; null when nothing changed (no
    /// version is added). Refused for a stale session, or when <paramref name="baseline"/> is no longer current.
    /// </summary>
    public Task<SummaryVersionId?> SaveEditAsync(
        SummaryNodeId node, SummaryVersionId baseline, string title, string body, long generation, long revision, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body)
        ? Task.FromException<SummaryVersionId?>(new SummaryRefusedException(SummaryRefusal.Empty))
        : CommitAsync(async () =>
        {
            var before = await _summaries.GetAsync(node, cancellationToken).ConfigureAwait(false);
            var saved = await _drafts.CommitAsync(
                node, generation, revision, () => _summaries.SaveEditAsync(node, baseline, title.Trim(), body.Trim(), _clock.UtcNow, cancellationToken)).ConfigureAwait(false);

            // After the draft's gate is released: naming may write and schedule work of its own.
            await _naming.CheckNameAsync(node, cancellationToken).ConfigureAwait(false);
            if (before is not null)
            {
                Changed?.Invoke(this, before.Node.Conversation);
            }

            return saved;
        });

    public Task SetReadAsync(SummaryNodeId node, SummaryVersionId version, bool read, CancellationToken cancellationToken) =>
        NotifyAsync(node, () => _summaries.SetReadAsync(version, read ? ReadSource.Manual : null, _clock.UtcNow, cancellationToken), cancellationToken);

    /// <summary>
    /// Records how far a version has been read. It becomes read once progress passes the threshold and it has been
    /// visible long enough in this opening (U2, MW-C30); opening alone never marks it.
    /// </summary>
    public async Task<bool> RecordProgressAsync(SummaryNodeId node, SummaryVersionId version, double progress, string? locator, TimeSpan visible, CancellationToken cancellationToken)
    {
        var markRead = progress >= SummaryRules.ReadThreshold && visible >= SummaryRules.MinimumReadTime;
        await _summaries.RecordProgressAsync(version, progress, locator, markRead, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        if (markRead)
        {
            await NotifyAsync(node, () => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
        }

        return markRead;
    }

    public Task RenameAsync(SummaryNodeId node, string displayName, CancellationToken cancellationToken) =>
        CommitAsync(() => NotifyAsync(node, () => _summaries.RenameAsync(node, displayName.Trim(), _clock.UtcNow, cancellationToken), cancellationToken));

    /// <summary>
    /// A summary commit (§9.3, D1): refused while storage is blocked, and its own storage failure blocks new work. Either
    /// way a <see cref="StorageWriteException"/> reaches the caller and nothing was stored.
    /// </summary>
    private async Task<T> CommitAsync<T>(Func<Task<T>> commit)
    {
        _health.ThrowIfBlocked();
        try
        {
            return await commit().ConfigureAwait(false);
        }
        catch (StorageWriteException ex)
        {
            _health.Report(ex);
            throw;
        }
    }

    private async Task CommitAsync(Func<Task> commit)
    {
        _health.ThrowIfBlocked();
        try
        {
            await commit().ConfigureAwait(false);
        }
        catch (StorageWriteException ex)
        {
            _health.Report(ex);
            throw;
        }
    }

    private async Task<SummarySaveResult> SaveAsync(GenerationTaskRecord task, string output)
    {
        if (SummaryRules.ParseGenerated(output) is not { } parsed)
        {
            return SummarySaveResult.Unusable;
        }

        var saved = await _summaries.SaveGeneratedAsync(
            task.Id, task.ConversationId, parsed, task.SummaryToSeq!.Value,
            task.SummaryBaseline is { } baseline ? new SummaryVersionId(baseline) : null, task.Model.Model, _clock.UtcNow, CancellationToken.None).ConfigureAwait(false);
        if (saved is null)
        {
            return SummarySaveResult.NotRunning;
        }

        if (!saved.IsCandidate)
        {
            await _naming.ScheduleNamingSafelyAsync(saved.Node).ConfigureAwait(false);
        }

        return SummarySaveResult.Saved;
    }

    /// <summary>For a branch: the quote and the source answer, as background that is not to be summarized.</summary>
    private async Task<string?> BackgroundAsync(ConversationId conversation, CancellationToken cancellationToken)
    {
        if (await _branches.GetSourceAsync(conversation, cancellationToken).ConfigureAwait(false) is not { } source)
        {
            return null;
        }

        var parent = await _conversations.GetMessagesAsync(source.Parent, cancellationToken).ConfigureAwait(false);
        var answer = parent.FirstOrDefault(m => m.Id == source.SourceMessage)?.Text ?? string.Empty;
        var excerpt = answer.Length > SourceExcerpt ? answer[..SourceExcerpt] + "…" : answer;
        return $"""
            本会话是从另一段回答中分支出来的。以下内容只用于理解背景，不要总结它们。
            用户引用的段落：
            {source.Quote}

            来源回答（节选）：
            {excerpt}
            """;
    }

    private async Task<PlanOutcome> PlanAsync(ConversationId conversation, IReadOnlyList<Message> own, int budget, CancellationToken cancellationToken)
    {
        // Only this conversation is summarized; its branch background travels as a separate note, not as turns.
        var stored = _contextSummaries is null ? [] : await _contextSummaries.ListAsync([conversation], cancellationToken).ConfigureAwait(false);
        var known = stored.ToDictionary(s => s.Key, s => ContextPlanner.SummaryTokens(s.Key, s.Text));
        return ContextPlanner.Plan([], new ContextSegment(conversation, null, own), budget, key => known.TryGetValue(key, out var tokens) ? tokens : null);
    }

    private async Task NotifyAsync(SummaryNodeId node, Func<Task> operation, CancellationToken cancellationToken)
    {
        var before = await _summaries.GetAsync(node, cancellationToken).ConfigureAwait(false);
        await operation().ConfigureAwait(false);
        if (before is not null)
        {
            Changed?.Invoke(this, before.Node.Conversation);
        }
    }

    /// <summary>
    /// A change of the current version, then (outside any draft gate the operation took) the name check and
    /// notification.
    /// </summary>
    private Task AfterCurrentChangeAsync(SummaryNodeId node, Func<Task> operation, CancellationToken cancellationToken) =>
        NotifyAsync(
            node,
            async () =>
            {
                await operation().ConfigureAwait(false);
                await _naming.CheckNameAsync(node, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    private static async Task<bool> Done(Task operation)
    {
        await operation.ConfigureAwait(false);
        return true;
    }

    private static SummarizeOutcome Reject(SummarizeRejection rejection) => new(rejection, null);
}
