using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Languages;
using Mindweave.Application.Models;
using Mindweave.Application.Storage;
using Mindweave.Core.Branches;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;
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

    /// <summary>More than <see cref="Resources.ResourceLimits.MaxAttachmentsPerMessage"/> attachments.</summary>
    TooManyAttachments,

    /// <summary>An image is attached and the active model does not declare image input (requirements §10.4.3).</summary>
    ImagesNotSupported,

    /// <summary>An attachment is no longer there (removed meanwhile); the composer reloads its draft.</summary>
    AttachmentMissing,
}

public sealed record SendOutcome(SendRejection? Rejection, ConversationId Conversation, MessageId? Answer)
{
    public bool Accepted => Rejection is null;

    internal static SendOutcome Reject(SendRejection rejection, ConversationId conversation = default) => new(rejection, conversation, null);
}

/// <param name="CompressedAnswers">Answers whose request sent older context as a summary (shown with a light note).</param>
/// <param name="ReducedAttachments">Answers whose request sent some attachment as only its summary or parts, and how (requirements §10.4.3).</param>
/// <param name="Runs">Answers that were runs (requirements §10.4.4): their steps and the limit reached, for the activity line.</param>
public sealed record ConversationContent(
    Conversation Conversation,
    IReadOnlyList<Message> Messages,
    IReadOnlySet<MessageId>? CompressedAnswers = null,
    IReadOnlyDictionary<MessageId, IReadOnlyList<ResourceInput>>? ReducedAttachments = null,
    IReadOnlyDictionary<MessageId, RunRecord>? Runs = null);

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
public sealed partial class ConversationService : IConversations
{
    public const int MaxQuestionLength = 100_000;

    private readonly IConversationStore _store;
    private readonly IBranchStore _branches;
    private readonly IContextSummaryStore? _summaries;
    private readonly IConversationArchiveStore? _archive;
    private readonly GenerationCoordinator _generation;
    private readonly ContextBudgets _budgets;
    private readonly StorageHealth _health;
    private readonly ModelSettingsService _models;
    private readonly IClock _clock;
    private readonly ConversationTitler _titler;
    private readonly ResourceContext? _resources;
    private readonly IResourceStore? _resourceStore;
    private readonly Tools.ToolRegistry? _tools;
    private readonly IRunStore? _runs;
    private readonly Preferences.IToolSettings? _toolSettings;
    private readonly Preferences.IWebSearchSettings? _webSearch;

    public ConversationService(
        IConversationStore store,
        IBranchStore branches,
        GenerationCoordinator generation,
        ContextBudgets budgets,
        StorageHealth health,
        ModelSettingsService models,
        IClock clock,
        ILogger<ConversationService> logger,
        IContextSummaryStore? summaries = null,
        IConversationArchiveStore? archive = null,
        ResourceContext? resources = null,
        IResourceStore? resourceStore = null,
        Tools.ToolRegistry? tools = null,
        Preferences.IToolSettings? toolSettings = null,
        IRunStore? runs = null,
        Preferences.IWebSearchSettings? webSearch = null)
    {
        _webSearch = webSearch;
        _runs = runs;
        _tools = tools;
        _toolSettings = toolSettings;
        _resources = resources;
        _resourceStore = resourceStore;
        _store = store;
        _branches = branches;
        _summaries = summaries;
        _archive = archive;
        _generation = generation;
        _budgets = budgets;
        _health = health;
        _models = models;
        _clock = clock;
        _logger = logger;
        _generation.AnswerFinished += OnAnswerFinished;
        _generation.TitleSaved += (_, id) =>
        {
            RaiseFromWorker(id);
            try
            {
                TitleSet?.Invoke(this, id);
            }
#pragma warning disable CA1031 // Raised on the generation worker: see RaiseFromWorker.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSubscriberFailed(_logger, ex, id);
            }
        };
        _titler = new ConversationTitler(store, branches, generation, health, models, clock, logger);
        _models.Changed += (_, _) => ModelChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The active model or its declared context capacity changed, so context budgets differ. Any thread.</summary>
    public event EventHandler? ModelChanged;

    /// <summary>A conversation's stored content or title changed. Raised on worker threads.</summary>
    public event EventHandler<ConversationId>? ConversationChanged;

    /// <summary>
    /// A conversation was given a title, automatic or manual, in any workspace (after it was stored). The workspace's names
    /// are prepared from these (2026-11-11). Raised on worker threads.
    /// </summary>
    public event EventHandler<ConversationId>? TitleSet;

    /// <summary>A core write failed and storage has not recovered: questions, retries, branches and archive commits are refused (§9.3).</summary>
    public bool IsStorageBlocked => _health.IsBlocked;

    /// <summary>
    /// Conversations were archived, restored, deleted or imported: what is hidden changed for these. Each of them also gets a
    /// <see cref="ConversationChanged"/>. Raised on worker threads.
    /// </summary>
    public event EventHandler<IReadOnlyList<ConversationId>>? VisibilityChanged;

    /// <summary>A conversation was deleted for good (raised before its <see cref="VisibilityChanged"/>). Raised on worker threads.</summary>
    public event EventHandler<ConversationId>? Deleted;

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        _store.ListAsync(workspace, cancellationToken);

    public async Task<WorkspaceId?> WorkspaceOfAsync(ConversationId id, CancellationToken cancellationToken) =>
        (await _store.GetAsync(id, cancellationToken).ConfigureAwait(false))?.WorkspaceId;

    public async Task<ConversationContent?> LoadAsync(ConversationId id, CancellationToken cancellationToken)
    {
        var conversation = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
        {
            return null;
        }

        var messages = await _store.GetMessagesAsync(id, cancellationToken).ConfigureAwait(false);
        var compressed = _summaries is null ? null : await _summaries.CompressedAnswersAsync(id, cancellationToken).ConfigureAwait(false);
        var reduced = _resourceStore is null ? null : await _resourceStore.ReducedInputsAsync(id, cancellationToken).ConfigureAwait(false);
        var runs = _runs is null ? null : await _runs.RunsAsync(id, cancellationToken).ConfigureAwait(false);
        return new ConversationContent(conversation, messages, compressed, reduced, runs);
    }

    public LiveAnswer? Observe(MessageId answer) => _generation.Observe(answer);

    /// <summary>An answer of the conversation is queued or still being generated.</summary>
    public bool HasUnfinishedAnswer(ConversationId conversation) => _generation.HasUnfinishedAnswer(conversation);

    /// <summary>What is queued or running changed (an answer started or finished). Raised on worker threads.</summary>
    public event EventHandler? ActivityChanged
    {
        add => _generation.Changed += value;
        remove => _generation.Changed -= value;
    }

    /// <summary>The context the conversation would send now with the active model, or null when no model is configured.</summary>
    public async Task<ContextUsage?> GetContextUsageAsync(ConversationId id, CancellationToken cancellationToken)
    {
        var model = await _models.GetActiveSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return model is null
            ? null
            : await ContextUsageAsync(id, await _store.GetMessagesAsync(id, cancellationToken).ConfigureAwait(false), model, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The same for content a page has just read (<see cref="LoadAsync"/>): its messages are not read again (health check
    /// 2026-11-12, performance-5).
    /// </summary>
    public async Task<ContextUsage?> GetContextUsageAsync(ConversationContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var model = await _models.GetActiveSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return model is null ? null : await ContextUsageAsync(content.Conversation.Id, content.Messages, model, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ContextUsage?> ContextUsageAsync(ConversationId id, IReadOnlyList<Message> history, ModelSnapshot model, CancellationToken cancellationToken)
    {
        var budget = await _budgets.BudgetForAsync(model, cancellationToken).ConfigureAwait(false);
        var ancestors = await _branches.GetAncestorContextAsync(id, cancellationToken).ConfigureAwait(false);
        var source = ancestors.Count == 0 ? null : await _branches.GetSourceAsync(id, cancellationToken).ConfigureAwait(false);
        var own = new ContextSegment(id, source?.Quote, [.. history.Where(m => m.IsFinished)]);
        if (own.Messages.Count == 0 && ancestors.Count == 0)
        {
            return new ContextUsage(0, 0, budget.AvailableTokens, false, false);
        }

        // Shown in billed tokens: the estimate through what this model was seen to bill. With tools, what the next question
        // sends is planned with room for the run, and the run's instructions and tool definitions go with it.
        var (planned, inputs, known) = await PlanAsync(id, own.Messages, budget, model, cancellationToken, ancestors).ConfigureAwait(false);
        var run = await RunPlanAsync(null, id, inputs, model, null, string.Empty, cancellationToken).ConfigureAwait(false);
        var system = AnswerPrompt.WithRun(inputs.Instructions, run is null ? string.Empty : RunPrompt.For(run.Tools));
        var instructions = TokenEstimator.PerRequest + (system.Length == 0 ? 0 : TokenEstimator.Message(system));
        var tools = run?.Tools.Select(Tools.ToolRegistry.Definition).Sum(ChatTurns.Estimate) ?? 0;
        var items = BranchContext.Assemble(ancestors, own, ModelPhrases.Current);
        var raw = items.Sum(i => TokenEstimator.Message(i.Text));
        var attachments = inputs.Resources.Sum(r => r.WholeTokens);
        var full = budget.Predict(instructions + tools + raw + attachments);
        if (planned.Plan is not { } whole)
        {
            return new ContextUsage(full, full, budget.AvailableTokens, true, true)
            {
                Parts = ContextUsage.Share(PartsOf(items, [], attachments), full),
            };
        }

        var (plan, _) = WithRoomForRun(whole, budget, inputs, known, run);
        var summaries = plan.Summaries.Sum(c => ContextPlanner.TokensOf(c, key => known.TryGetValue(key, out var size) ? size : null, ModelPhrases.Current));
        var sentRaw = plan.RawItems.Sum(i => TokenEstimator.Message(i.Text));
        var estimate = plan.EstimatedTokens - TokenEstimator.PerRequest + instructions + tools;
        var sent = budget.Predict(estimate);
        return new ContextUsage(sent, full, budget.AvailableTokens, plan.IsCompressed || plan.IsResourceReduced, false)
        {
            Parts = ContextUsage.Share(PartsOf([.. plan.RawItems], [(ContextUsageKind.ContextSummaries, summaries)], plan.EstimatedTokens - TokenEstimator.PerRequest - sentRaw - summaries), sent),
        };

        List<(ContextUsageKind Kind, int Estimate)> PartsOf(IReadOnlyList<ContextItem> sentItems, IReadOnlyList<(ContextUsageKind Kind, int Estimate)> others, int attached) =>
        [
            (ContextUsageKind.Conversation, sentItems.Where(i => i.Message.ConversationId == id).Sum(i => TokenEstimator.Message(i.Text))),
            (ContextUsageKind.Ancestors, sentItems.Where(i => i.Message.ConversationId != id).Sum(i => TokenEstimator.Message(i.Text))),
            .. others,
            (ContextUsageKind.Attachments, attached),
            (ContextUsageKind.Instructions, instructions),
            (ContextUsageKind.Tools, tools),
        ];
    }

    /// <summary>Creates a conversation from its first question and starts answering it.</summary>
    public Task<SendOutcome> StartAsync(WorkspaceId workspace, string question, CancellationToken cancellationToken) =>
        StartAsync(workspace, question, null, cancellationToken);

    public Task<SendOutcome> StartAsync(WorkspaceId workspace, string question, Recommendations.StartingRecommendation? from, CancellationToken cancellationToken) =>
        StartAsync(workspace, question, [], from, cancellationToken);

    public Task<SendOutcome> StartAsync(
        WorkspaceId workspace, string question, IReadOnlyList<ResourceId> attachments, Recommendations.StartingRecommendation? from, CancellationToken cancellationToken) =>
        StartAsync(workspace, question, attachments, from, null, cancellationToken);

    /// <summary>The same, with the composer's choices for this question (the web search switch, requirements §10.4.5).</summary>
    public Task<SendOutcome> StartAsync(
        WorkspaceId workspace, string question, IReadOnlyList<ResourceId> attachments, Recommendations.StartingRecommendation? from, SendOptions? options, CancellationToken cancellationToken) =>
        StartAsync(workspace, null, question, attachments, from, options, cancellationToken);

    /// <summary>
    /// A conversation stored with its first question: a root, or (<paramref name="parent"/>) the user's own conversation that
    /// continues a received one (decision I4): a branch that left from its last message, with all of it as its context. Of an
    /// empty one it is a branch with no source and no context.
    /// </summary>
    private async Task<SendOutcome> StartAsync(
        WorkspaceId workspace, ConversationId? parent, string question, IReadOnlyList<ResourceId> attachments, Recommendations.StartingRecommendation? from, SendOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        var id = ConversationId.New();
        if (await CheckAsync(question, attachments, id, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        var model = (await _models.GetActiveSnapshotAsync(cancellationToken).ConfigureAwait(false))!;
        var (blocks, refused) = await AttachmentBlocksAsync(attachments, model, cancellationToken, parent).ConfigureAwait(false);
        if (refused is { } attachmentRejection)
        {
            return SendOutcome.Reject(attachmentRejection, id);
        }

        // What the received conversation holds now is what the question was asked about, whatever a file adds meanwhile.
        var chain = parent is { } left ? await ContinuedAsync(left, cancellationToken).ConfigureAwait(false) : [];

        var now = _clock.UtcNow;
        var conversation = new Conversation(id, workspace, null, TitleSource.None, now, now);
        var user = QuestionMessage(id, question, blocks, now);
        var answer = NewMessage(id, 1, MessageRole.Assistant, string.Empty, MessageState.Pending, null, now);
        var budget = await _budgets.BudgetForAsync(model, cancellationToken).ConfigureAwait(false);
        var (planned, inputs, known) = await PlanAsync(id, [user], budget, model, cancellationToken, chain).ConfigureAwait(false);
        if (planned.Plan is not { } plan)
        {
            return SendOutcome.Reject(Rejection(planned.Rejection!.Value), id);
        }

        var run = await RunPlanAsync(workspace, id, inputs, model, options, question, cancellationToken).ConfigureAwait(false);
        var whole = budget;
        (plan, budget) = WithRoomForRun(plan, budget, inputs, known, run);
        var task = NewTask(GenerationTaskKind.Answer, id, answer.Id, model, [.. plan.RawItems.Select(c => c.Message.Id)], now, run);
        try
        {
            await (parent is { } received
                ? _store.CreateBranchAsync(received, chain, conversation, user, answer, task, cancellationToken)
                : _store.CreateAsync(conversation, user, answer, task, from, cancellationToken)).ConfigureAwait(false);
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

        await EnqueueAsync(task, model, plan, budget, answer, inputs, run, whole);
        if (parent is { } continued)
        {
            ConversationChanged?.Invoke(this, continued);
        }

        ConversationChanged?.Invoke(this, id);
        return new SendOutcome(null, id, answer.Id);
    }

    /// <summary>The context of a conversation that continues a received one: what that one may use, and then all of it. Empty for an empty one.</summary>
    private async Task<IReadOnlyList<ContextSegment>> ContinuedAsync(ConversationId received, CancellationToken cancellationToken)
    {
        var messages = await _store.GetMessagesAsync(received, cancellationToken).ConfigureAwait(false);
        if (messages.Count == 0)
        {
            return [];
        }

        var above = await _branches.GetAncestorContextAsync(received, cancellationToken).ConfigureAwait(false);
        var source = await _branches.GetSourceAsync(received, cancellationToken).ConfigureAwait(false);
        return [.. above, new ContextSegment(received, source?.Quote, messages) { IsReceived = true }];
    }

    public Task<SendOutcome> AskAsync(ConversationId id, string question, CancellationToken cancellationToken) =>
        AskAsync(id, question, [], cancellationToken);

    public Task<SendOutcome> AskAsync(ConversationId id, string question, IReadOnlyList<ResourceId> attachments, CancellationToken cancellationToken) =>
        AskAsync(id, question, attachments, null, cancellationToken);

    /// <summary>The same, with the composer's choices for this question (the web search switch, requirements §10.4.5).</summary>
    public async Task<SendOutcome> AskAsync(ConversationId id, string question, IReadOnlyList<ResourceId> attachments, SendOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        // A received conversation takes no message: the question continues it in a conversation of the user's own (decision I4).
        if (await _store.GetAsync(id, cancellationToken).ConfigureAwait(false) is { IsImported: true } received)
        {
            return await StartAsync(received.WorkspaceId, id, question, attachments, null, options, cancellationToken).ConfigureAwait(false);
        }

        if (await CheckAsync(question, attachments, id, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        var history = await _store.GetMessagesAsync(id, cancellationToken).ConfigureAwait(false);
        if (ConversationRules.HasUnfinishedAnswer(history))
        {
            return SendOutcome.Reject(SendRejection.AnswerInProgress, id);
        }

        var model = (await _models.GetActiveSnapshotAsync(cancellationToken).ConfigureAwait(false))!;
        var (blocks, refused) = await AttachmentBlocksAsync(attachments, model, cancellationToken).ConfigureAwait(false);
        if (refused is { } attachmentRejection)
        {
            return SendOutcome.Reject(attachmentRejection, id);
        }

        var now = _clock.UtcNow;
        var user = QuestionMessage(id, question, blocks, now);
        var answer = NewMessage(id, 0, MessageRole.Assistant, string.Empty, MessageState.Pending, null, now);
        var budget = await _budgets.BudgetForAsync(model, cancellationToken).ConfigureAwait(false);
        var (planned, inputs, known) = await PlanAsync(id, [.. history, user], budget, model, cancellationToken).ConfigureAwait(false);
        if (planned.Plan is not { } plan)
        {
            return SendOutcome.Reject(Rejection(planned.Rejection!.Value), id);
        }

        var run = await RunPlanAsync(null, id, inputs, model, options, question, cancellationToken).ConfigureAwait(false);
        var whole = budget;
        (plan, budget) = WithRoomForRun(plan, budget, inputs, known, run);
        var task = NewTask(GenerationTaskKind.Answer, id, answer.Id, model, [.. plan.RawItems.Select(c => c.Message.Id)], now, run);

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

        await EnqueueAsync(task, model, plan, budget, storedAnswer, inputs, run, whole);
        ConversationChanged?.Invoke(this, id);
        return new SendOutcome(null, id, storedAnswer.Id);
    }

    /// <summary>Retry (failed/stopped/interrupted) or regenerate (complete) — only the last answer (U1, U4).</summary>
    public Task<SendOutcome> RetryAsync(ConversationId id, MessageId answerToReplace, CancellationToken cancellationToken) =>
        RetryAsync(id, answerToReplace, null, cancellationToken);

    /// <summary>The same, with the composer's choices at the moment of the retry (decision W3).</summary>
    public async Task<SendOutcome> RetryAsync(ConversationId id, MessageId answerToReplace, SendOptions? options, CancellationToken cancellationToken)
    {
        if (await CheckReadyAsync(id, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        // A received answer is never retried (decision I4).
        var history = await _store.GetMessagesAsync(id, cancellationToken).ConfigureAwait(false);
        if (!ConversationRules.CanRetry(history, answerToReplace) || await _store.GetAsync(id, cancellationToken).ConfigureAwait(false) is { IsImported: true })
        {
            return SendOutcome.Reject(SendRejection.NotRetryable, id);
        }

        // The answer is regenerated for the last question, from the history before that question.
        var question = history.Last(m => m.Role == MessageRole.User);
        var model = (await _models.GetActiveSnapshotAsync(cancellationToken).ConfigureAwait(false))!;
        var budget = await _budgets.BudgetForAsync(model, cancellationToken).ConfigureAwait(false);
        var (planned, inputs, known) = await PlanAsync(id, [.. history.Where(m => m.Seq <= question.Seq)], budget, model, cancellationToken).ConfigureAwait(false);
        if (planned.Plan is not { } plan)
        {
            return SendOutcome.Reject(Rejection(planned.Rejection!.Value), id);
        }

        var now = _clock.UtcNow;
        var answer = NewMessage(id, 0, MessageRole.Assistant, string.Empty, MessageState.Pending, answerToReplace, now);
        var run = await RunPlanAsync(null, id, inputs, model, options, ContentBlocks.TypedTextOf(question.Blocks), cancellationToken).ConfigureAwait(false);
        var whole = budget;
        (plan, budget) = WithRoomForRun(plan, budget, inputs, known, run);
        var task = NewTask(GenerationTaskKind.Answer, id, answer.Id, model, [.. plan.RawItems.Select(c => c.Message.Id)], now, run);

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

        await EnqueueAsync(task, model, plan, budget, stored, inputs, run, whole);
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

        if (_health.IsBlocked)
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
            _health.Report(ex);
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
        _health.ThrowIfBlocked();
        ArchiveResult result;
        try
        {
            result = await archive.ArchiveAsync(id, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (ConversationMissingException)
        {
            return new ArchiveOutcome([], Missing: true);
        }
        catch (StorageWriteException ex)
        {
            _health.Report(ex);
            throw;
        }

        if (result.Archived)
        {
            // A running title that finishes before it stops writes nothing to a hidden conversation (the store refuses).
            // Its "requested" marks go too: a title cancelled by the commit never reports itself, and the next answer
            // after a restore asks again.
            await _generation.CancelBackgroundAsync(result.Subtree.ToHashSet()).ConfigureAwait(false);
            _titler.Forget(result.Subtree);

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
        var shown = await CommitAsync(() => RequireArchive().RestoreAsync(id, cancellationToken)).ConfigureAwait(false);
        NotifyVisibility(shown);
        return shown;
    }

    /// <summary>
    /// A delete committed by <see cref="ConversationDeletionService"/>: every member of the scope is forgotten here (its
    /// title request mark), each gets <see cref="Deleted"/>, the parents the roots were branched from get
    /// <see cref="ConversationChanged"/> (their branch lists changed), and <see cref="VisibilityChanged"/> covers all.
    /// </summary>
    internal void NotifyDeleted(ConversationDeleteScope scope)
    {
        foreach (var id in scope.Conversations)
        {
            _titler.Forget([id]);
            Deleted?.Invoke(this, id);
        }

        foreach (var parent in scope.ParentOf.Values.Distinct())
        {
            ConversationChanged?.Invoke(this, parent);
        }

        NotifyVisibility(scope.Conversations);
    }

    /// <summary>
    /// Conversations arrived by import. The first stands for all that are new (a list is read whole, and no page shows any of
    /// them yet); each that was here and gained a branch, messages or a source is named. <see cref="VisibilityChanged"/> covers
    /// all: an identity that was deleted before may be back, in another workspace than it was in.
    /// </summary>
    internal void NotifyImported(Import.ImportResult result)
    {
        foreach (var id in result.Created.Take(1).Concat(result.Parents).Concat(result.Gained).Distinct())
        {
            ConversationChanged?.Invoke(this, id);
        }

        VisibilityChanged?.Invoke(this, [.. result.Created, .. result.Gained]);
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
        var clean = ConversationRules.Shorten(title ?? string.Empty, ConversationRules.TitleLimit);
        if (clean.Length == 0)
        {
            return;
        }

        await CommitAsync(async () =>
        {
            await _store.SetManualTitleAsync(id, clean, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
        ConversationChanged?.Invoke(this, id);
        TitleSet?.Invoke(this, id);
    }

    /// <summary>
    /// A commit that depends on storage (§9.3, D1): refused while storage is blocked, and its own storage failure blocks
    /// it. Either way a <see cref="StorageWriteException"/> reaches the caller and nothing was stored.
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

    /// <summary>The question was not stored (nothing of it was): it stays in the input, and storage-dependent work pauses.</summary>
    private SendOutcome StorageRefused(StorageWriteException exception, ConversationId id)
    {
        _health.Report(exception);
        return SendOutcome.Reject(SendRejection.StorageUnavailable, id);
    }

    private async Task<SendOutcome?> CheckAsync(string question, IReadOnlyList<ResourceId> attachments, ConversationId id, CancellationToken cancellationToken)
    {
        var trimmed = (question ?? string.Empty).Trim();

        // A question may be its attachments alone (requirements §10.4.3).
        if (trimmed.Length == 0 && attachments.Count == 0)
        {
            return SendOutcome.Reject(SendRejection.EmptyQuestion, id);
        }

        if (attachments.Count > Resources.ResourceLimits.MaxAttachmentsPerMessage)
        {
            return SendOutcome.Reject(SendRejection.TooManyAttachments, id);
        }

        if (trimmed.Length > MaxQuestionLength)
        {
            return SendOutcome.Reject(SendRejection.QuestionTooLong, id);
        }

        return await CheckReadyAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SendOutcome?> CheckReadyAsync(ConversationId id, CancellationToken cancellationToken)
    {
        if (_health.IsBlocked)
        {
            return SendOutcome.Reject(SendRejection.StorageUnavailable, id);
        }

        if (await _models.GetActiveSnapshotAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return SendOutcome.Reject(SendRejection.NotConfigured, id);
        }

        if (_generation.HasUnfinishedAnswer(id))
        {
            return SendOutcome.Reject(SendRejection.AnswerInProgress, id);
        }

        // The last answer may have ended on screen with its terminal write still landing: wait for it (normally
        // milliseconds), so the history is read whole and the store does not see an answer still streaming. Bounded:
        // past the bound the store decides, as before.
        try
        {
            await _generation.AnswerWritesSettledAsync(id).WaitAsync(EndingAnswerWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        return null;
    }

    /// <summary>How long a send waits for the previous answer's terminal write (<see cref="CheckReadyAsync"/>).</summary>
    internal static readonly TimeSpan EndingAnswerWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// What is sent for <paramref name="own"/>: the frozen ancestor chain, then the conversation itself, fitted to
    /// the model's budget (S3). Stored summaries count with their real size; missing ones are written later, in the
    /// answer task's own slot. <paramref name="chain"/> is the ancestor chain when the caller has it already: that of a
    /// conversation that is not stored yet, or one just read.
    /// </summary>
    private async Task<(PlanOutcome Outcome, ContextInputs Inputs, IReadOnlyDictionary<SummaryKey, int> Known)> PlanAsync(
        ConversationId id, IReadOnlyList<Message> own, ContextBudget budget, ModelSnapshot model, CancellationToken cancellationToken, IReadOnlyList<ContextSegment>? chain = null)
    {
        var ancestors = chain ?? await _branches.GetAncestorContextAsync(id, cancellationToken).ConfigureAwait(false);
        var source = ancestors.Count == 0 ? null : await _branches.GetSourceAsync(id, cancellationToken).ConfigureAwait(false);
        var stored = _summaries is null
            ? []
            : await _summaries.ListAsync([.. ancestors.Select(a => a.Conversation), id], cancellationToken).ConfigureAwait(false);
        var phrases = ModelPhrases.Current;
        var known = stored.ToDictionary(s => s.Key, s => ContextPlanner.SummaryTokens(s.Key, s.Text, phrases));
        var segment = new ContextSegment(id, source?.Quote, own);

        // The attachments of every message in the chain, planned as their own layer (architecture §15.4); their chunks are
        // ranked by the question the user typed.
        var question = own.LastOrDefault(m => m.Role == MessageRole.User) is { } last ? ContentBlocks.TypedTextOf(last.Blocks) : string.Empty;
        var candidates = _resources is null
            ? []
            : await _resources.CandidatesAsync(BranchContext.Assemble(ancestors, segment, phrases), question, model, cancellationToken).ConfigureAwait(false);
        // The answer instructions open every answer request and count in its budget (architecture §15.5); a branch from a
        // selected passage gets its own part (requirements §10.9).
        var inputs = new ContextInputs(ancestors, segment, candidates) { Instructions = AnswerPrompt.For(!string.IsNullOrWhiteSpace(segment.Quote)) };
        var outcome = ContextPlanner.Plan(ancestors, segment, budget.Available - inputs.InstructionTokens, key => known.TryGetValue(key, out var tokens) ? tokens : null, candidates, phrases);
        return (outcome, inputs, known);
    }

    /// <summary>Share of the input space a run's context plan leaves for tool results and later rounds (L8).</summary>
    internal const int RunRoomPercent = 25;

    /// <summary>
    /// The plan and budget of an answer (docs/design/agent-loop.md L8, architecture §15.4): a run's context is planned with
    /// <see cref="RunRoomPercent"/> of the space left for the run, unless the required items alone do not fit that, when the
    /// whole space is used as before; an answer without tools keeps <paramref name="plan"/>.
    /// </summary>
    private static (ContextPlan Plan, ContextBudget Budget) WithRoomForRun(ContextPlan plan, ContextBudget budget, ContextInputs inputs, IReadOnlyDictionary<SummaryKey, int> known, RunPlan? run)
    {
        if (run is null)
        {
            return (plan, budget);
        }

        var room = budget.LeavingRoom(RunRoomPercent);
        return ContextPlanner.Plan(inputs.Ancestors, inputs.Own, room.Available - inputs.InstructionTokens, key => known.TryGetValue(key, out var tokens) ? tokens : null, inputs.Resources, ModelPhrases.Current).Plan is { } reserved
            ? (reserved, room)
            : (plan, budget);
    }

    /// <summary>
    /// The blocks of the attachments, in order, or why they cannot be sent: one is gone, or is an image the model cannot see.
    /// Each must be in the conversation's workspace; the store checks that when it is sent.
    /// </summary>
    private async Task<(IReadOnlyList<ContentBlock> Blocks, SendRejection? Rejection)> AttachmentBlocksAsync(
        IReadOnlyList<ResourceId> attachments, ModelSnapshot model, CancellationToken cancellationToken, ConversationId? received = null)
    {
        var blocks = new List<ContentBlock>();
        foreach (var resource in attachments.Distinct())
        {
            if (_resourceStore is null || await _resourceStore.GetAsync(resource, cancellationToken).ConfigureAwait(false) is not { } stored)
            {
                return ([], SendRejection.AttachmentMissing);
            }

            // Asked in a received conversation, whose draft it is on: one that already went with a question is another conversation's now.
            if (received is not null && stored.Resource.ConversationId is { } owner && owner != received)
            {
                return ([], SendRejection.AttachmentMissing);
            }

            if (stored.Resource.IsImage && !model.Capabilities.Has(ModelCapability.ImageInput))
            {
                return ([], SendRejection.ImagesNotSupported);
            }

            blocks.Add(Resources.ResourceService.BlockFor(stored));
        }

        return (blocks, null);
    }

    /// <summary>A question: its attachments first (shown above its text), then the text the user typed, if any.</summary>
    private static Message QuestionMessage(ConversationId conversation, string question, IReadOnlyList<ContentBlock> attachments, DateTimeOffset at)
    {
        var text = (question ?? string.Empty).Trim();
        IReadOnlyList<ContentBlock> blocks = text.Length == 0 ? attachments : [.. attachments, new TextBlock(text)];
        return new(MessageId.New(), conversation, 0, MessageRole.User, blocks, MessageState.Complete, 0, null, null, null, at, at);
    }

    private static SendRejection Rejection(ContextRejection rejection) => rejection switch
    {
        ContextRejection.QuestionTooLarge => SendRejection.QuestionTooLargeForModel,
        _ => SendRejection.HistoryTooLongForModel,
    };

    private Task<bool> EnqueueAsync(GenerationTaskRecord task, ModelSnapshot model, ContextPlan plan, ContextBudget budget, Message answer, ContextInputs inputs, RunPlan? run, ContextBudget whole)
    {
        var request = new ChatRequest(model, ContextCompressor.Unsummarized(plan).Turns);
        return _generation.EnqueueOrCancelAsync(new AnswerWork(task, request, new LiveAnswer(answer.Id, answer.ConversationId), plan, budget, inputs, run) { RunBudget = whole });
    }

    /// <summary>
    /// Whether this answer is a run (requirements §10.4.4): the model declares tool calls, 使用工具 is on, and some tool has
    /// something to find (decision R5). The tools may reach the resources the frozen chain refers to: attachments with text
    /// and earlier tool results. Null: a single request, exactly as before.
    /// </summary>
    private async Task<RunPlan?> RunPlanAsync(
        WorkspaceId? workspace, ConversationId id, ContextInputs inputs, ModelSnapshot model, SendOptions? options, string question, CancellationToken cancellationToken)
    {
        if (_tools is null || _toolSettings is null || !model.Capabilities.Has(ModelCapability.ToolCalls)
            || !await _toolSettings.GetEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var space = workspace ?? (await _store.GetAsync(id, cancellationToken).ConfigureAwait(false))?.WorkspaceId;
        if (space is not { } ws)
        {
            return null;
        }

        var resources = inputs.Ancestors.Append(inputs.Own)
            .SelectMany(segment => segment.Messages)
            .SelectMany(m => m.Blocks)
            .Select(b => b switch
            {
                AttachmentBlock { Text: not null } a => a.Resource,
                ToolResultBlock { Resource: { } r } => r,
                _ => (ResourceId?)null,
            })
            .OfType<ResourceId>();
        var mode = options?.WebSearch ?? (_webSearch is null ? WebSearchMode.Auto : await _webSearch.GetDefaultModeAsync(cancellationToken).ConfigureAwait(false));
        var scope = new Tools.ToolScope(ws, id, resources)
        {
            Web = await WebRunAsync(inputs, options, question, cancellationToken).ConfigureAwait(false),
            NetworkAllowed = mode != WebSearchMode.Off, // Decision M8: third-party network reads follow the switch.
        };
        var offered = await _tools.OfferAsync(scope, cancellationToken).ConfigureAwait(false);
        if (offered.Count == 0)
        {
            return null;
        }

        return new RunPlan(offered, await _toolSettings.GetLimitsAsync(cancellationToken).ConfigureAwait(false), scope)
        {
            Reader = offered.Any(t => t.Name == Tools.SearchAttachmentsTool.ToolName) ? null : _tools.Find(Tools.SearchAttachmentsTool.ToolName),
        };
    }

    /// <summary>
    /// The run's web state when web search applies to this question (requirements §10.4.5): the switch is not 关 (the page's
    /// choice, or the default when a caller gives none) and a search key is set. The addresses in the user's questions and
    /// attachments along the frozen chain may be read without asking (Q10, decision W9), except in what arrived by import,
    /// which someone else wrote (decision I3). Null: no web tools.
    /// </summary>
    private async Task<Tools.WebRun?> WebRunAsync(ContextInputs inputs, SendOptions? options, string question, CancellationToken cancellationToken)
    {
        if (_webSearch is null)
        {
            return null;
        }

        var mode = options?.WebSearch ?? await _webSearch.GetDefaultModeAsync(cancellationToken).ConfigureAwait(false);
        if (mode == WebSearchMode.Off || !await _webSearch.HasKeyAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var texts = inputs.Ancestors.Append(inputs.Own)
            .Where(segment => !segment.IsReceived)
            .SelectMany(segment => segment.Messages)
            .Where(m => m.Role == MessageRole.User)
            .SelectMany(m => m.Blocks.Select(b => b switch
            {
                TextBlock t => t.Markdown,
                AttachmentBlock a => a.Text,
                _ => null,
            }))
            .Append(question);
        string? preSearch = null;
        if (mode == WebSearchMode.On)
        {
            // L9: a short question, or one that points back, searches with what it is about.
            var title = (await _store.GetAsync(inputs.Own.Conversation, cancellationToken).ConfigureAwait(false))?.Title;
            var questions = inputs.Own.Messages.Where(m => m.Role == MessageRole.User).Select(m => ContentBlocks.TypedTextOf(m.Blocks)).ToList();
            var previous = questions.Count > 1 ? questions[^2] : null;
            preSearch = Tools.WebRun.PreSearchQuery(question ?? string.Empty, title, inputs.Own.Quote, previous);
        }

        return new Tools.WebRun(mode, question ?? string.Empty, texts) { PreSearch = preSearch };
    }

    /// <summary>The user allowed or refused a call waiting for approval in a running answer (Q10). False when it no longer waits.</summary>
    public bool DecideApproval(MessageId answer, string callId, bool allow) =>
        _generation.Observe(answer)?.Decide(callId, allow) ?? false;

    private readonly ILogger<ConversationService> _logger;

    /// <summary>
    /// Raised on the generation worker. A throwing ConversationChanged subscriber is logged here (low findings
    /// concurrency-6, errors-7): it used to escape an async void handler on a worker thread, ending the process, and the
    /// title was never asked for. The titler handles its own failures.
    /// </summary>
    private void OnAnswerFinished(object? sender, AnswerOutcome e)
    {
        RaiseFromWorker(e.ConversationId);
        if (e.State == MessageState.Complete)
        {
            _ = _titler.OnAnswerCompletedAsync(e);
        }
    }

    /// <summary>ConversationChanged raised on the generation worker: a subscriber's failure is logged, never thrown into the run.</summary>
    private void RaiseFromWorker(ConversationId conversation)
    {
        try
        {
            ConversationChanged?.Invoke(this, conversation);
        }
#pragma warning disable CA1031 // A subscriber's failure must not reach the generation worker, fail its task or stop the title.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSubscriberFailed(_logger, ex, conversation);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A subscriber to conversation {ConversationId} changes failed")]
    private static partial void LogSubscriberFailed(ILogger logger, Exception exception, ConversationId conversationId);

    private static Message NewMessage(ConversationId conversation, int seq, MessageRole role, string body, MessageState state, MessageId? supersedes, DateTimeOffset at) =>
        new(MessageId.New(), conversation, seq, role, ContentBlocks.FromText(body), state, 0, supersedes, null, null, at, state == MessageState.Complete ? at : null);

    private static GenerationTaskRecord NewTask(GenerationTaskKind kind, ConversationId conversation, MessageId? target, ModelSnapshot model, IReadOnlyList<MessageId> input, DateTimeOffset at, RunPlan? run = null) =>
        new(GenerationTaskId.New(), kind, conversation, target, model, input, at, Tools: run?.Tools.Select(Tools.ToolRegistry.Frozen).ToList(), Limits: run?.Limits);
}
