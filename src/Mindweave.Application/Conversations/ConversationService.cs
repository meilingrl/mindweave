using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Models;
using Mindweave.Application.Storage;
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
}

public sealed record SendOutcome(SendRejection? Rejection, ConversationId Conversation, MessageId? Answer)
{
    public bool Accepted => Rejection is null;

    internal static SendOutcome Reject(SendRejection rejection, ConversationId conversation = default) => new(rejection, conversation, null);
}

public sealed record ConversationContent(Conversation Conversation, IReadOnlyList<Message> Messages);

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
    private readonly GenerationCoordinator _generation;
    private readonly ModelSettingsService _models;
    private readonly IClock _clock;
    private readonly ILogger<ConversationService> _logger;
    private readonly ConcurrentDictionary<ConversationId, byte> _titleRequested = new();

    public ConversationService(
        IConversationStore store,
        GenerationCoordinator generation,
        ModelSettingsService models,
        IClock clock,
        ILogger<ConversationService> logger)
    {
        _store = store;
        _generation = generation;
        _models = models;
        _clock = clock;
        _logger = logger;
        _generation.AnswerFinished += OnAnswerFinished;
    }

    /// <summary>A conversation's stored content or title changed. Raised on worker threads.</summary>
    public event EventHandler<ConversationId>? ConversationChanged;

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
        return new ConversationContent(conversation, messages);
    }

    public LiveAnswer? Observe(MessageId answer) => _generation.Observe(answer);

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
        var task = NewTask(GenerationTaskKind.Answer, id, answer.Id, model, [user.Id], now);

        await _store.CreateAsync(conversation, user, answer, task, cancellationToken).ConfigureAwait(false);
        Enqueue(task, model, [user], answer);
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
        var context = ConversationRules.SelectContext(history);
        var user = NewMessage(id, 0, MessageRole.User, question.Trim(), MessageState.Complete, null, now);
        var answer = NewMessage(id, 0, MessageRole.Assistant, string.Empty, MessageState.Pending, null, now);
        var task = NewTask(GenerationTaskKind.Answer, id, answer.Id, model, [.. context.Select(m => m.Id), user.Id], now);

        Message storedAnswer;
        try
        {
            (_, storedAnswer) = await _store.AppendQuestionAsync(user, answer, task, cancellationToken).ConfigureAwait(false);
        }
        catch (AnswerInProgressException)
        {
            return SendOutcome.Reject(SendRejection.AnswerInProgress, id);
        }

        Enqueue(task, model, [.. context, user], storedAnswer);
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
        var context = ConversationRules.SelectContext([.. history.Where(m => m.Seq < question.Seq)]);
        var model = (await _models.GetActiveSnapshotAsync(cancellationToken).ConfigureAwait(false))!;
        var now = _clock.UtcNow;
        var answer = NewMessage(id, 0, MessageRole.Assistant, string.Empty, MessageState.Pending, answerToReplace, now);
        var task = NewTask(GenerationTaskKind.Answer, id, answer.Id, model, [.. context.Select(m => m.Id), question.Id], now);

        Message stored;
        try
        {
            stored = await _store.AddRetryAsync(answer, task, cancellationToken).ConfigureAwait(false);
        }
        catch (AnswerInProgressException)
        {
            return SendOutcome.Reject(SendRejection.NotRetryable, id);
        }

        Enqueue(task, model, [.. context, question], stored);
        ConversationChanged?.Invoke(this, id);
        return new SendOutcome(null, id, stored.Id);
    }

    public Task StopAsync(MessageId answer) => _generation.StopAsync(answer);

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

    private void Enqueue(GenerationTaskRecord task, ModelSnapshot model, IReadOnlyList<Message> input, Message answer)
    {
        var turns = input.Select(m => new ChatTurn(m.Role == MessageRole.User ? ChatRole.User : ChatRole.Assistant, m.Text)).ToArray();
        _generation.Enqueue(new AnswerWork(task, new ChatRequest(model, turns), new LiveAnswer(answer.Id, answer.ConversationId)));
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
        var excerpt = e.Text.Length > TitleAnswerExcerpt ? e.Text[..TitleAnswerExcerpt] : e.Text;
        var now = _clock.UtcNow;
        var task = NewTask(GenerationTaskKind.Title, e.ConversationId, null, model, [question.Id, e.MessageId], now);
        await _store.AddTaskAsync(task, CancellationToken.None).ConfigureAwait(false);
        _generation.Enqueue(new TitleWork(task, new ChatRequest(model with { MaxOutputTokens = TitleOutputTokens },
        [
            new ChatTurn(ChatRole.System, "为下面的对话起一个简短标题，概括提问的主题。只输出标题本身，不加引号、序号或句末标点；不超过 20 个字；使用提问所用的语言。"),
            new ChatTurn(ChatRole.User, $"提问：{question.Text}\n\n回答开头：{excerpt}"),
        ])));
    }

    private static Message NewMessage(ConversationId conversation, int seq, MessageRole role, string body, MessageState state, MessageId? supersedes, DateTimeOffset at) =>
        new(MessageId.New(), conversation, seq, role, ContentBlocks.FromText(body), state, 0, supersedes, null, null, at, state == MessageState.Complete ? at : null);

    private static GenerationTaskRecord NewTask(GenerationTaskKind kind, ConversationId conversation, MessageId? target, ModelSnapshot model, IReadOnlyList<MessageId> input, DateTimeOffset at) =>
        new(GenerationTaskId.New(), kind, conversation, target, model, input, at);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automatic title for {ConversationId} not scheduled")]
    private static partial void LogTitleNotScheduled(ILogger logger, Exception exception, ConversationId conversationId);
}
