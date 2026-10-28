using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Models;
using Mindweave.Application.Storage;
using Mindweave.Core.Branches;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Conversations;

public enum SendRejection
{
    EmptyQuestion,
    QuestionTooLong,
    NotConfigured,
    AnswerInProgress,
    StorageUnavailable,
    NotRetryable,

    /// <summary>The question with its required source alone exceeds the model's context budget.</summary>
    QuestionTooLargeForModel,

    /// <summary>Even with older context summarized, the conversation does not fit the model's context budget.</summary>
    HistoryTooLongForModel,

    /// <summary>The conversation is archived or below an archived one; it is restored first (D-021).</summary>
    ConversationArchived,

    /// <summary>The conversation was deleted meanwhile.</summary>
    ConversationMissing,
}

public sealed record SendOutcome(SendRejection? Rejection, ConversationId Conversation, MessageId? Answer)
{
    public bool Accepted => Rejection is null;

    internal static SendOutcome Reject(SendRejection rejection, ConversationId conversation = default) => new(rejection, conversation, null);
}

/// <param name="CompressedAnswers">Answers whose request sent older context as a summary (shown with a light note).</param>
public sealed record ConversationContent(Conversation Conversation, IReadOnlyList<Message> Messages, IReadOnlySet<MessageId>? CompressedAnswers = null);

/// <summary>
/// How much of the model's context the conversation takes now, as estimated by the planner that decides what is sent.
/// <paramref name="Tokens"/> is what would be sent (after any summaries); <paramref name="FullTokens"/> is everything,
/// raw. <paramref name="TooLong"/> when even summaries cannot make it fit.
/// </summary>
public sealed record ContextUsage(int Tokens, int FullTokens, int Budget, bool Compressed, bool TooLong)
{
    public double Fraction => Budget == 0 ? 1 : Math.Min(1, (double)Tokens / Budget);
}

public sealed record BranchOutcome(BranchRejection? Rejection, ConversationId Child)
{
    public bool Accepted => Rejection is null;
}

/// <summary>
/// An archive request: archived, refused because a conversation of the subtree still has an answer or summary queued
/// or running (<paramref name="Busy"/>), or <paramref name="Missing"/> when the conversation no longer exists.
/// </summary>
public sealed record ArchiveOutcome(IReadOnlyList<ConversationId> Busy, bool Missing = false)
{
    public bool Archived => !Missing && Busy.Count == 0;
}

/// <summary>
/// Conversation use cases. Every accepted request is durable (question, pending answer and task in one
/// transaction) before it reaches the generation queue, so a crash after "send" never loses the question
/// and never re-sends it.
/// </summary>
public sealed partial class ConversationService
{
    public const int MaxQuestionLength = 100_000;
    private const int TitleAnswerExcerpt = 2000;

    /// <summary>
    /// Output cap for a title request. Reasoning models count hidden reasoning against it: with 64 tokens,
    /// a real reasoning model spent the whole budget reasoning and returned no title (finish_reason "length";
    /// see TRACK.md findings, 2026-10-26).
    /// The title itself is still cut to <see cref="ConversationRules.MaxTitleLength"/>.
    /// </summary>
    private const int TitleOutputTokens = 1024;

    private readonly IConversationStore _store;
    private readonly IBranchStore _branches;
    private readonly IContextSummaryStore? _summaries;
    private readonly IConversationArchiveStore? _archive;
    private readonly GenerationCoordinator _generation;
    private readonly ModelSettingsService _models;
    private readonly IClock _clock;
    private readonly ILogger<ConversationService> _logger;
    private readonly ConcurrentDictionary<ConversationId, byte> _titleRequested = new();

    public ConversationService(
        IConversationStore store,
        IBranchStore branches,
        GenerationCoordinator generation,
        ModelSettingsService models,
        IClock clock,
        ILogger<ConversationService> logger,
        IContextSummaryStore? summaries = null,
        IConversationArchiveStore? archive = null)
    {
        _store = store;
        _branches = branches;
        _summaries = summaries;
        _archive = archive;
        _generation = generation;
        _models = models;
        _clock = clock;
        _logger = logger;
        _generation.AnswerFinished += OnAnswerFinished;
        _generation.TitleSaved += (_, id) => ConversationChanged?.Invoke(this, id);
        // A title stopped by an exit is asked for again when the next answer completes (never on restart by itself).
        _generation.TitleAbandoned += (_, id) => _titleRequested.TryRemove(id, out byte _);
        _models.Changed += (_, _) => ModelChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The active model or its declared context capacity changed, so context budgets differ. Any thread.</summary>
    public event EventHandler? ModelChanged;

    /// <summary>A conversation's stored content or title changed. Raised on worker threads.</summary>
    public event EventHandler<ConversationId>? ConversationChanged;

    /// <summary><see cref="IsStorageBlocked"/> changed. Raised on worker threads.</summary>
    public event EventHandler? StorageStateChanged
    {
        add => _generation.StorageStateChanged += value;
        remove => _generation.StorageStateChanged -= value;
    }

    /// <summary>A core write failed and storage has not recovered: questions, retries and branches are refused (§9.3).</summary>
    public bool IsStorageBlocked => _generation.IsStorageBlocked;

    /// <summary>
    /// Conversations were archived, restored or deleted: what is hidden changed for these. Each of them also gets a
    /// <see cref="ConversationChanged"/>. Raised on worker threads.
    /// </summary>
    public event EventHandler<IReadOnlyList<ConversationId>>? VisibilityChanged;

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        _store.ListAsync(workspace, cancellationToken);

    public async Task<ConversationContent?> LoadAsync(ConversationId id, CancellationToken cancellationToken)
    {
        var conversation = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
        {
            return null;
        }

        var messages = await _store.GetMessagesAsync(id, cancellationToken).ConfigureAwait(false);
        var compressed = _summaries is null ? null : await _summaries.CompressedAnswersAsync(id, cancellationToken).ConfigureAwait(false);
        return new ConversationContent(conversation, messages, compressed);
    }

    public LiveAnswer? Observe(MessageId answer) => _generation.Observe(answer);

    /// <summary>The context the conversation would send now with the active model, or null when no model is configured.</summary>
    public async Task<ContextUsage?> GetContextUsageAsync(ConversationId id, CancellationToken cancellationToken)
    {
        var model = await _models.GetActiveSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            return null;
        }

        var history = await _store.GetMessagesAsync(id, cancellationToken).ConfigureAwait(false);
        var budget = ContextBudget.For(model).Available;
        var ancestors = await _branches.GetAncestorContextAsync(id, cancellationToken).ConfigureAwait(false);
        var source = ancestors.Count == 0 ? null : await _branches.GetSourceAsync(id, cancellationToken).ConfigureAwait(false);
        var own = new ContextSegment(id, source?.Quote, [.. history.Where(m => m.IsFinished)]);
        var full = TokenEstimator.PerRequest + BranchContext.Assemble(ancestors, own).Sum(i => TokenEstimator.Message(i.Text));
        if (own.Messages.Count == 0 && ancestors.Count == 0)
        {
            return new ContextUsage(0, 0, budget, false, false);
        }

        var planned = await PlanAsync(id, own.Messages, model, cancellationToken).ConfigureAwait(false);
        return planned.Plan is { } plan
            ? new ContextUsage(plan.EstimatedTokens, full, budget, plan.IsCompressed, false)
            : new ContextUsage(full, full, budget, true, true);
    }

    /// <summary>Creates a conversation from its first question and starts answering it.</summary>
    public async Task<SendOutcome> StartAsync(WorkspaceId workspace, string question, CancellationToken cancellationToken)
    {
        var id = ConversationId.New();
        if (await CheckAsync(question, id, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        var model = (await _models.GetActiveSnapshotAsync(cancellationToken).ConfigureAwait(false))!;
        var now = _clock.UtcNow;
        var conversation = new Conversation(id, workspace, null, TitleSource.None, now, now);
        var user = NewMessage(id, 0, MessageRole.User, question.Trim(), MessageState.Complete, null, now);
        var answer = NewMessage(id, 1, MessageRole.Assistant, string.Empty, MessageState.Pending, null, now);
        var planned = await PlanAsync(id, [user], model, cancellationToken).ConfigureAwait(false);
        if (planned.Plan is not { } plan)
        {
            return SendOutcome.Reject(Rejection(planned.Rejection!.Value), id);
        }

        var task = NewTask(GenerationTaskKind.Answer, id, answer.Id, model, [user.Id], now);
        try
        {
            await _store.CreateAsync(conversation, user, answer, task, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageWriteException ex)
        {
            return StorageRefused(ex, id);
        }

        Enqueue(task, model, plan, answer);
        ConversationChanged?.Invoke(this, id);
        return new SendOutcome(null, id, answer.Id);
    }

    public async Task<SendOutcome> AskAsync(ConversationId id, string question, CancellationToken cancellationToken)
    {
        if (await CheckAsync(question, id, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        var history = await _store.GetMessagesAsync(id, cancellationToken).ConfigureAwait(false);
        if (ConversationRules.HasUnfinishedAnswer(history))
        {
            return SendOutcome.Reject(SendRejection.AnswerInProgress, id);
        }

        var model = (await _models.GetActiveSnapshotAsync(cancellationToken).ConfigureAwait(false))!;
        var now = _clock.UtcNow;
        var user = NewMessage(id, 0, MessageRole.User, question.Trim(), MessageState.Complete, null, now);
        var answer = NewMessage(id, 0, MessageRole.Assistant, string.Empty, MessageState.Pending, null, now);
        var planned = await PlanAsync(id, [.. history, user], model, cancellationToken).ConfigureAwait(false);
        if (planned.Plan is not { } plan)
        {
            return SendOutcome.Reject(Rejection(planned.Rejection!.Value), id);
        }

        var task = NewTask(GenerationTaskKind.Answer, id, answer.Id, model, [.. plan.RawItems.Select(c => c.Message.Id)], now);

        Message storedAnswer;
        try
        {
            (_, storedAnswer) = await _store.AppendQuestionAsync(user, answer, task, cancellationToken).ConfigureAwait(false);
        }
        catch (AnswerInProgressException)
        {
            return SendOutcome.Reject(SendRejection.AnswerInProgress, id);
        }
        catch (StorageWriteException ex)
        {
            return StorageRefused(ex, id);
        }
        catch (ConversationArchivedException)
        {
            return SendOutcome.Reject(SendRejection.ConversationArchived, id);
        }
        catch (ConversationMissingException)
        {
            return SendOutcome.Reject(SendRejection.ConversationMissing, id);
        }

        Enqueue(task, model, plan, storedAnswer);
        ConversationChanged?.Invoke(this, id);
        return new SendOutcome(null, id, storedAnswer.Id);
    }

    /// <summary>Retry (failed/stopped/interrupted) or regenerate (complete) — only the last answer (U1, U4).</summary>
    public async Task<SendOutcome> RetryAsync(ConversationId id, MessageId answerToReplace, CancellationToken cancellationToken)
    {
        if (await CheckReadyAsync(id, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        var history = await _store.GetMessagesAsync(id, cancellationToken).ConfigureAwait(false);
        if (!ConversationRules.CanRetry(history, answerToReplace))
        {
            return SendOutcome.Reject(SendRejection.NotRetryable, id);
        }

        // The answer is regenerated for the last question, from the history before that question.
        var question = history.Last(m => m.Role == MessageRole.User);
        var model = (await _models.GetActiveSnapshotAsync(cancellationToken).ConfigureAwait(false))!;
        var planned = await PlanAsync(id, [.. history.Where(m => m.Seq <= question.Seq)], model, cancellationToken).ConfigureAwait(false);
        if (planned.Plan is not { } plan)
        {
            return SendOutcome.Reject(Rejection(planned.Rejection!.Value), id);
        }

        var now = _clock.UtcNow;
        var answer = NewMessage(id, 0, MessageRole.Assistant, string.Empty, MessageState.Pending, answerToReplace, now);
        var task = NewTask(GenerationTaskKind.Answer, id, answer.Id, model, [.. plan.RawItems.Select(c => c.Message.Id)], now);

        Message stored;
        try
        {
            stored = await _store.AddRetryAsync(answer, task, cancellationToken).ConfigureAwait(false);
        }
        catch (AnswerInProgressException)
        {
            return SendOutcome.Reject(SendRejection.NotRetryable, id);
        }
        catch (StorageWriteException ex)
        {
            return StorageRefused(ex, id);
        }
        catch (ConversationArchivedException)
        {
            return SendOutcome.Reject(SendRejection.ConversationArchived, id);
        }
        catch (ConversationMissingException)
        {
            return SendOutcome.Reject(SendRejection.ConversationMissing, id);
        }

        Enqueue(task, model, plan, stored);
        ConversationChanged?.Invoke(this, id);
        return new SendOutcome(null, id, stored.Id);
    }

    /// <summary>
    /// Creates a branch conversation from a passage of a finished answer (§6.2). The child, its source and its
    /// frozen ancestor ranges are stored together; nothing is asked yet.
    /// </summary>
    public async Task<BranchOutcome> CreateBranchAsync(ConversationId parent, BranchSelection selection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var child = ConversationId.New();
        if (BranchRules.CheckSelection(selection) is { } invalid)
        {
            return new BranchOutcome(invalid, child);
        }

        if (_generation.IsStorageBlocked)
        {
            return new BranchOutcome(BranchRejection.StorageUnavailable, child);
        }

        try
        {
            await _branches.CreateAsync(parent, child, selection, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (BranchRejectedException ex)
        {
            return new BranchOutcome(ex.Reason, child);
        }
        catch (StorageWriteException ex)
        {
            _generation.ReportStorageFailure(ex);
            return new BranchOutcome(BranchRejection.StorageUnavailable, child);
        }

        ConversationChanged?.Invoke(this, parent);
        ConversationChanged?.Invoke(this, child);
        return new BranchOutcome(null, child);
    }

    public Task<BranchSource?> GetSourceAsync(ConversationId child, CancellationToken cancellationToken) =>
        _branches.GetSourceAsync(child, cancellationToken);

    public Task<IReadOnlyList<BranchSource>> ListBranchesAsync(ConversationId parent, CancellationToken cancellationToken) =>
        _branches.ListChildrenAsync(parent, cancellationToken);

    public Task<IReadOnlyList<LineageEntry>> GetLineageAsync(ConversationId conversation, CancellationToken cancellationToken) =>
        _branches.GetLineageAsync(conversation, cancellationToken);

    public Task StopAsync(MessageId answer) => _generation.StopAsync(answer);

    /// <summary>
    /// Archives a conversation and so hides its subtree (requirements §9.5). Titles and naming in the subtree never
    /// block it (MW-C14): the queued ones are cancelled in the archive's own commit and the running ones stopped right
    /// after it. Answers and summaries are not stopped for it: while one runs, archiving is refused and nothing in
    /// the background is touched.
    /// </summary>
    public async Task<ArchiveOutcome> ArchiveAsync(ConversationId id, CancellationToken cancellationToken)
    {
        var archive = RequireArchive();
        ArchiveResult result;
        try
        {
            result = await archive.ArchiveAsync(id, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (ConversationMissingException)
        {
            return new ArchiveOutcome([], Missing: true);
        }

        if (result.Archived)
        {
            // A running title that finishes before it stops writes nothing to a hidden conversation (the store refuses).
            // Its "requested" marks go too: a title cancelled by the commit never reports itself, and the next answer
            // after a restore asks again.
            await _generation.CancelBackgroundAsync(result.Subtree.ToHashSet()).ConfigureAwait(false);
            foreach (var hidden in result.Subtree)
            {
                _titleRequested.TryRemove(hidden, out _);
            }

            NotifyVisibility(result.Subtree);
        }

        return new ArchiveOutcome(result.Busy);
    }

    /// <summary>What restoring would clear and bring back; the page asks first when archived ancestors are cleared too (D-021).</summary>
    public Task<RestorePlan> PlanRestoreAsync(ConversationId id, CancellationToken cancellationToken) =>
        _archive is null ? Task.FromResult(new RestorePlan(id, [], 0)) : _archive.PlanRestoreAsync(id, cancellationToken);

    /// <summary>Restores the conversation and the archived ancestors on its path; archives off the path stay (D-021).</summary>
    public async Task<IReadOnlyList<ConversationId>> RestoreAsync(ConversationId id, CancellationToken cancellationToken)
    {
        var shown = await RequireArchive().RestoreAsync(id, cancellationToken).ConfigureAwait(false);
        NotifyVisibility(shown);
        return shown;
    }

    /// <summary>
    /// Deletes a conversation that nothing depends on, irreversibly, with its unsent drafts (requirements §9.5). The
    /// check and the delete are one transaction; a refusal names every reason and leaves its titles and naming alone.
    /// Their task rows go with the delete, and the running ones are stopped after it: their late writes find nothing.
    /// </summary>
    public async Task<DeleteResult> DeleteAsync(ConversationId id, CancellationToken cancellationToken)
    {
        var result = await RequireArchive().DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        if (result.Deleted)
        {
            await _generation.CancelBackgroundAsync(new HashSet<ConversationId> { id }).ConfigureAwait(false);
            _titleRequested.TryRemove(id, out _);
            if (result.Parent is { } parent)
            {
                ConversationChanged?.Invoke(this, parent);
            }

            NotifyVisibility([id]);
        }

        return result;
    }

    /// <summary>Those of <paramref name="conversations"/> the user archived (markers on branch entries).</summary>
    public async Task<IReadOnlySet<ConversationId>> ArchivedAmongAsync(IReadOnlyCollection<ConversationId> conversations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        return _archive is null || conversations.Count == 0
            ? new HashSet<ConversationId>()
            : await _archive.ArchivedAmongAsync(conversations, cancellationToken).ConfigureAwait(false);
    }

    private IConversationArchiveStore RequireArchive() =>
        _archive ?? throw new InvalidOperationException("No archive store is configured.");

    private void NotifyVisibility(IReadOnlyList<ConversationId> conversations)
    {
        foreach (var id in conversations)
        {
            ConversationChanged?.Invoke(this, id);
        }

        if (conversations.Count > 0)
        {
            VisibilityChanged?.Invoke(this, conversations);
        }
    }

    /// <summary>Manual title; protected from automatic titling from now on. Blank input is ignored.</summary>
    public async Task RenameAsync(ConversationId id, string title, CancellationToken cancellationToken)
    {
        var clean = ConversationRules.Shorten(title ?? string.Empty, ConversationRules.MaxTitleLength);
        if (clean.Length == 0)
        {
            return;
        }

        await _store.SetManualTitleAsync(id, clean, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        ConversationChanged?.Invoke(this, id);
    }

    /// <summary>The question was not stored (nothing of it was): it stays in the input, and storage-dependent work pauses.</summary>
    private SendOutcome StorageRefused(StorageWriteException exception, ConversationId id)
    {
        _generation.ReportStorageFailure(exception);
        return SendOutcome.Reject(SendRejection.StorageUnavailable, id);
    }

    private async Task<SendOutcome?> CheckAsync(string question, ConversationId id, CancellationToken cancellationToken)
    {
        var trimmed = (question ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return SendOutcome.Reject(SendRejection.EmptyQuestion, id);
        }

        if (trimmed.Length > MaxQuestionLength)
        {
            return SendOutcome.Reject(SendRejection.QuestionTooLong, id);
        }

        return await CheckReadyAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SendOutcome?> CheckReadyAsync(ConversationId id, CancellationToken cancellationToken)
    {
        if (_generation.IsStorageBlocked)
        {
            return SendOutcome.Reject(SendRejection.StorageUnavailable, id);
        }

        if (await _models.GetActiveSnapshotAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return SendOutcome.Reject(SendRejection.NotConfigured, id);
        }

        return _generation.HasUnfinishedAnswer(id) ? SendOutcome.Reject(SendRejection.AnswerInProgress, id) : null;
    }

    /// <summary>
    /// What is sent for <paramref name="own"/>: the frozen ancestor chain, then the conversation itself, fitted to
    /// the model's budget (S3). Stored summaries count with their real size; missing ones are written later, in the
    /// answer task's own slot.
    /// </summary>
    private async Task<PlanOutcome> PlanAsync(ConversationId id, IReadOnlyList<Message> own, ModelSnapshot model, CancellationToken cancellationToken)
    {
        var ancestors = await _branches.GetAncestorContextAsync(id, cancellationToken).ConfigureAwait(false);
        var source = ancestors.Count == 0 ? null : await _branches.GetSourceAsync(id, cancellationToken).ConfigureAwait(false);
        var stored = _summaries is null
            ? []
            : await _summaries.ListAsync([.. ancestors.Select(a => a.Conversation), id], cancellationToken).ConfigureAwait(false);
        var known = stored.ToDictionary(s => s.Key, s => ContextPlanner.SummaryTokens(s.Key, s.Text));
        return ContextPlanner.Plan(ancestors, new ContextSegment(id, source?.Quote, own), ContextBudget.For(model).Available, key => known.TryGetValue(key, out var tokens) ? tokens : null);
    }

    private static SendRejection Rejection(ContextRejection rejection) => rejection switch
    {
        ContextRejection.QuestionTooLarge => SendRejection.QuestionTooLargeForModel,
        _ => SendRejection.HistoryTooLongForModel,
    };

    private void Enqueue(GenerationTaskRecord task, ModelSnapshot model, ContextPlan plan, Message answer)
    {
        var request = new ChatRequest(model, ContextCompressor.Unsummarized(plan).Turns);
        _generation.Enqueue(new AnswerWork(task, request, new LiveAnswer(answer.Id, answer.ConversationId), plan, ContextBudget.For(model)));
    }

    private async void OnAnswerFinished(object? sender, AnswerOutcome e)
    {
        ConversationChanged?.Invoke(this, e.ConversationId);
        if (e.State != MessageState.Complete)
        {
            return;
        }

        try
        {
            await ScheduleTitleAsync(e).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Titles are best effort; an event handler must not throw.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogTitleNotScheduled(_logger, ex, e.ConversationId);
        }
    }

    private async Task ScheduleTitleAsync(AnswerOutcome e)
    {
        var conversation = await _store.GetAsync(e.ConversationId, CancellationToken.None).ConfigureAwait(false);
        if (conversation is not { TitleSource: TitleSource.None } || !_titleRequested.TryAdd(e.ConversationId, 0))
        {
            return;
        }

        var model = await _models.GetActiveSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        if (model is null || _generation.IsStorageBlocked)
        {
            return;
        }

        var messages = await _store.GetMessagesAsync(e.ConversationId, CancellationToken.None).ConfigureAwait(false);
        var question = messages.First(m => m.Role == MessageRole.User);
        var source = await _branches.GetSourceAsync(e.ConversationId, CancellationToken.None).ConfigureAwait(false);
        var asked = source is null ? question.Text : BranchContext.QuotedQuestion(source.Quote, question.Text);
        var excerpt = e.Text.Length > TitleAnswerExcerpt ? e.Text[..TitleAnswerExcerpt] : e.Text;
        var now = _clock.UtcNow;
        var task = NewTask(GenerationTaskKind.Title, e.ConversationId, null, model, [question.Id, e.MessageId], now);
        if (_generation.IsClosed)
        {
            // An exit is under way (the answer completed while it was stopping): ask again after the next answer.
            _titleRequested.TryRemove(e.ConversationId, out _);
            return;
        }

        try
        {
            await _store.AddTaskAsync(task, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ConversationArchivedException)
        {
            // Archived meanwhile: no title while hidden; the next answer after a restore asks again.
            _titleRequested.TryRemove(e.ConversationId, out _);
            return;
        }

        try
        {
            _generation.Enqueue(new TitleWork(task, new ChatRequest(model with { MaxOutputTokens = TitleOutputTokens },
            [
                new ChatTurn(ChatRole.System, "为下面的对话起一个简短标题，概括提问的主题。只输出标题本身，不加引号、序号或句末标点；不超过 20 个字；使用提问所用的语言。"),
                new ChatTurn(ChatRole.User, $"提问：{asked}\n\n回答开头：{excerpt}"),
            ])));
        }
        catch (InvalidOperationException)
        {
            // Closed between the check and the enqueue: no queued row is left behind, and the next answer asks again.
            _titleRequested.TryRemove(e.ConversationId, out _);
            await _store.FinishTaskAsync(task.Id, GenerationTaskState.Cancelled, null, null, _clock.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static Message NewMessage(ConversationId conversation, int seq, MessageRole role, string body, MessageState state, MessageId? supersedes, DateTimeOffset at) =>
        new(MessageId.New(), conversation, seq, role, ContentBlocks.FromText(body), state, 0, supersedes, null, null, at, state == MessageState.Complete ? at : null);

    private static GenerationTaskRecord NewTask(GenerationTaskKind kind, ConversationId conversation, MessageId? target, ModelSnapshot model, IReadOnlyList<MessageId> input, DateTimeOffset at) =>
        new(GenerationTaskId.New(), kind, conversation, target, model, input, at);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automatic title for {ConversationId} not scheduled")]
    private static partial void LogTitleNotScheduled(ILogger logger, Exception exception, ConversationId conversationId);
}
