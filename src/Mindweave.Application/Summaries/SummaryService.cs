using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
public sealed partial class SummaryService
{
    /// <summary>Output cap for a summary; reasoning models spend hidden reasoning against it (D-015).</summary>
    public const int SummaryOutputTokens = 16_384;

    private const int NamingOutputTokens = 4_096;
    private const int SourceExcerpt = 3_000;
    private const int NamingBodyExcerpt = 600;

    private const string Instructions = """
        你为用户整理一次探索对话的知识总结。只依据对话内容，不补充对话里没有的事实。
        保留：问题背景、主要解释、仍未解决的问题，以及原回答中的不确定之处（照实标明，不要改成确定的说法）。用户在对话中纠正过的内容，以纠正后的为准。
        输出格式：第一行写“标题：”加一个标题（不超过 20 个字，表达知识点或话题，而不是复述提问）；第二行写“短名：”加一个 2 到 8 个字的短名，用作知识网络中节点的名字，只保留最核心的知识点；空一行后用 Markdown 写正文，可以用小标题和列表。使用对话所用的语言。
        """;

    private const string Request = "请按要求总结以上对话。";

    private const string NamingInstructions = """
        下面是同一工作区里基础标题相同的几条知识总结。为每一条给出一个简短的区分词（2 到 8 个字），说明它与其他几条的区别，必须能在它的正文里找到依据。
        现有名称里的区分词仍然准确时保留原词。确实无法区分时留空，不要编造区别。
        每行输出一条，格式为“编号：区分词”，不要输出其他内容。
        """;

    private static readonly Regex NamingLine = new(@"^\s*\[?(?<n>\d+)\]?\s*[：:.、]\s*(?<term>.*)$", RegexOptions.CultureInvariant);

    private readonly ISummaryStore _summaries;
    private readonly IConversationStore _conversations;
    private readonly IBranchStore _branches;
    private readonly IContextSummaryStore? _contextSummaries;
    private readonly GenerationCoordinator _generation;
    private readonly ModelSettingsService _models;
    private readonly IClock _clock;
    private readonly ILogger<SummaryService> _logger;
    private readonly ConcurrentDictionary<(WorkspaceId, string), byte> _namingQueued = new();
    private readonly ConcurrentDictionary<GenerationTaskId, (WorkspaceId, string)> _namingTasks = new();

    public SummaryService(
        ISummaryStore summaries,
        IConversationStore conversations,
        IBranchStore branches,
        GenerationCoordinator generation,
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
        _models = models;
        _clock = clock;
        _logger = logger;
        _generation.SummaryTaskFinished += (_, task) =>
        {
            // A naming task stopped before its output arrived (an archive, an exit) never reached Apply's cleanup.
            if (_namingTasks.TryRemove(task.Id, out var group))
            {
                _namingQueued.TryRemove(group, out byte _);
            }

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
        if (_generation.IsStorageBlocked)
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
        var budget = ContextBudget.For(model);
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

        var turns = new ChatRequest(model, [.. system, .. ContextCompressor.Unsummarized(plan).Turns]);
        _generation.Enqueue(new SummaryWork(task, turns, plan, budget, output => SaveAsync(task, output)));
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
        AfterCurrentChangeAsync(node, () => _summaries.AdoptAsync(node, candidate, _clock.UtcNow, cancellationToken), cancellationToken);

    public Task DiscardAsync(SummaryNodeId node, SummaryVersionId candidate, CancellationToken cancellationToken) =>
        NotifyAsync(node, () => _summaries.DiscardAsync(node, candidate, _clock.UtcNow, cancellationToken), cancellationToken);

    public Task SetCurrentAsync(SummaryNodeId node, SummaryVersionId version, CancellationToken cancellationToken) =>
        AfterCurrentChangeAsync(node, () => _summaries.SetCurrentAsync(node, version, _clock.UtcNow, cancellationToken), cancellationToken);

    /// <summary>Deletes versions; returns the new current version, or null when the node was deleted with its last version.</summary>
    public async Task<SummaryVersionId?> DeleteVersionsAsync(SummaryNodeId node, IReadOnlySet<SummaryVersionId> versions, CancellationToken cancellationToken)
    {
        var detail = await _summaries.GetAsync(node, cancellationToken).ConfigureAwait(false);
        var result = await _summaries.DeleteVersionsAsync(node, versions, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        if (detail is not null)
        {
            if (result.NewCurrent is { } current && current != detail.Node.Current)
            {
                await CheckNameAsync(node, cancellationToken).ConfigureAwait(false);
            }

            Changed?.Invoke(this, detail.Node.Conversation);
        }

        return result.NewCurrent;
    }

    public Task SaveDraftAsync(SummaryNodeId node, SummaryVersionId baseline, string title, string body, CancellationToken cancellationToken) =>
        _summaries.SaveDraftAsync(new SummaryEditDraft(node, baseline, title, body, _clock.UtcNow), cancellationToken);

    public Task DiscardDraftAsync(SummaryNodeId node, CancellationToken cancellationToken) =>
        NotifyAsync(node, () => _summaries.DeleteDraftAsync(node, cancellationToken), cancellationToken);

    /// <summary>Saves an edit of the current version as a new version; null when nothing changed (no version is added).</summary>
    public async Task<SummaryVersionId?> SaveEditAsync(SummaryNodeId node, SummaryVersionId baseline, string title, string body, CancellationToken cancellationToken)
    {
        SummaryVersionId? saved = null;
        await AfterCurrentChangeAsync(
            node,
            async () => saved = await _summaries.SaveEditAsync(node, baseline, title.Trim(), body.Trim(), _clock.UtcNow, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return saved;
    }

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
        NotifyAsync(node, () => _summaries.RenameAsync(node, displayName.Trim(), _clock.UtcNow, cancellationToken), cancellationToken);

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
            await ScheduleNamingSafelyAsync(saved.Node).ConfigureAwait(false);
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

    private Task AfterCurrentChangeAsync(SummaryNodeId node, Func<Task> operation, CancellationToken cancellationToken) =>
        NotifyAsync(
            node,
            async () =>
            {
                await operation().ConfigureAwait(false);
                await CheckNameAsync(node, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>
    /// After the current version changed (§10.2): if its title no longer matches the base title, the base title and
    /// (unless manual) the display name follow it, and the new name's same-name group is checked.
    /// </summary>
    private async Task CheckNameAsync(SummaryNodeId node, CancellationToken cancellationToken)
    {
        if (await _summaries.GetAsync(node, cancellationToken).ConfigureAwait(false) is not { } detail)
        {
            return;
        }

        if (SummaryRules.NormalizeName(detail.Current.Title) != SummaryRules.NormalizeName(detail.Node.BaseTitle))
        {
            await _summaries.UpdateBaseTitleAsync(node, detail.Node.Current, detail.Current.Title, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
            await ScheduleNamingSafelyAsync(node).ConfigureAwait(false);
        }
    }

    private async Task ScheduleNamingSafelyAsync(SummaryNodeId node)
    {
        try
        {
            await ScheduleNamingAsync(node).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Naming is best effort; the summary is already saved under its base name.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogNamingNotScheduled(_logger, ex, node);
        }
    }

    /// <summary>Queues naming for the node's same-name group when it has two or more members; groups are deduplicated.</summary>
    private async Task ScheduleNamingAsync(SummaryNodeId node)
    {
        if (await _summaries.GetAsync(node, CancellationToken.None).ConfigureAwait(false) is not { } detail)
        {
            return;
        }

        var key = SummaryRules.NormalizeName(detail.Node.BaseTitle);
        var group = (await _summaries.ListAsync(detail.Node.Workspace, CancellationToken.None).ConfigureAwait(false))
            .Where(e => SummaryRules.NormalizeName(e.Node.BaseTitle) == key)
            .ToList();
        if (group.Count < 2 || group.All(e => e.Node.NameSource == NameSource.Manual) || _generation.IsStorageBlocked)
        {
            return;
        }

        if (await _models.GetActiveSnapshotAsync(CancellationToken.None).ConfigureAwait(false) is not { } model || !_namingQueued.TryAdd((detail.Node.Workspace, key), 0))
        {
            return;
        }

        var prompt = new StringBuilder().Append("基础标题：").Append(detail.Node.BaseTitle).Append("\n\n");
        for (var i = 0; i < group.Count; i++)
        {
            var body = group[i].Current.Body;
            prompt.Append(CultureInfo.InvariantCulture, $"[{i + 1}] 现有名称：{group[i].Node.DisplayName}\n标题：{group[i].Current.Title}\n正文开头：{(body.Length > NamingBodyExcerpt ? body[..NamingBodyExcerpt] : body)}\n\n");
        }

        var expectations = group.Select(e => new NamingExpectation(e.Node.Id, e.Node.Current, e.Node.DisplayName)).ToList();
        var baseTitles = group.Select(e => e.Node.BaseTitle).ToList();
        var task = new GenerationTaskRecord(
            GenerationTaskId.New(), GenerationTaskKind.Naming, detail.Node.Conversation, null, model with { MaxOutputTokens = NamingOutputTokens }, [], _clock.UtcNow);
        await _conversations.AddTaskAsync(task, CancellationToken.None).ConfigureAwait(false);
        _namingTasks[task.Id] = (detail.Node.Workspace, key);
        _generation.Enqueue(new NamingWork(
            task,
            new ChatRequest(task.Model, [new ChatTurn(ChatRole.System, NamingInstructions), new ChatTurn(ChatRole.User, prompt.ToString())]),
            async output =>
            {
                try
                {
                    var terms = ParseNaming(output, group.Count);
                    var names = expectations.Select((e, i) => (e, SummaryRules.DisplayName(baseTitles[i], terms[i]))).ToList();
                    var renamed = await _summaries.ApplyNamesAsync(names, _clock.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    foreach (var entry in group.Where(e => renamed.Contains(e.Node.Id)))
                    {
                        Changed?.Invoke(this, entry.Node.Conversation);
                    }
                }
                finally
                {
                    _namingTasks.TryRemove(task.Id, out _);
                    _namingQueued.TryRemove((detail.Node.Workspace, key), out _);
                }
            }));
    }

    /// <summary>"编号：区分词" lines; a missing or empty line leaves that node with its base name.</summary>
    internal static string?[] ParseNaming(string output, int count)
    {
        var terms = new string?[count];
        foreach (var line in output.Split('\n'))
        {
            var match = NamingLine.Match(line);
            if (match.Success && int.TryParse(match.Groups["n"].Value, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= count)
            {
                var term = match.Groups["term"].Value.Trim().Trim('“', '”', '"', '「', '」');
                terms[n - 1] = term.Length is > 0 and <= 16 ? term : null;
            }
        }

        return terms;
    }

    private static SummarizeOutcome Reject(SummarizeRejection rejection) => new(rejection, null);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Naming for summary node {Node} not scheduled")]
    private static partial void LogNamingNotScheduled(ILogger logger, Exception exception, SummaryNodeId node);
}
