using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Models;
using Mindweave.Application.Storage;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Conversations;

/// <summary>
/// Automatic conversation titles: after a completed answer, an untitled conversation gets one best-effort title
/// request. Owns the "title requested" marks: a conversation is asked for at most once until its request is
/// abandoned (exit), cancelled (archive) or the conversation is forgotten (archive, delete).
/// </summary>
internal sealed partial class ConversationTitler
{
    private const int TitleAnswerExcerpt = 2000;

    /// <summary>How much of the first question (with its quote, for a branch) a title request sends.</summary>
    internal const int TitleQuestionExcerpt = 1_000;

    /// <summary>
    /// Output cap for a title request. Reasoning models count hidden reasoning against it: with 64 tokens,
    /// a real reasoning model spent the whole budget reasoning and returned no title (finish_reason "length";
    /// see the development findings, 2026-10-26).
    /// The title itself is still cut to <see cref="ConversationRules.MaxTitleLength"/>.
    /// </summary>
    private const int TitleOutputTokens = 1024;

    private const string TitlePrompt = "为下面的对话起一个简短标题，概括提问的主题。只输出标题本身，不加引号、序号或句末标点；不超过 20 个字；使用提问所用的语言。";

    private readonly IConversationStore _store;
    private readonly IBranchStore _branches;
    private readonly GenerationCoordinator _generation;
    private readonly StorageHealth _health;
    private readonly ModelSettingsService _models;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<ConversationId, byte> _titleRequested = new();

    public ConversationTitler(
        IConversationStore store,
        IBranchStore branches,
        GenerationCoordinator generation,
        StorageHealth health,
        ModelSettingsService models,
        IClock clock,
        ILogger logger)
    {
        _store = store;
        _branches = branches;
        _generation = generation;
        _health = health;
        _models = models;
        _clock = clock;
        _logger = logger;
        // A title stopped by an exit is asked for again when the next answer completes (never on restart by itself).
        _generation.TitleAbandoned += (_, id) => _titleRequested.TryRemove(id, out byte _);
    }

    /// <summary>
    /// Drops the "requested" marks of conversations that were archived or deleted: a title cancelled by the archive
    /// commit never reports itself, and the next answer after a restore asks again.
    /// </summary>
    public void Forget(IEnumerable<ConversationId> conversations)
    {
        foreach (var id in conversations)
        {
            _titleRequested.TryRemove(id, out _);
        }
    }

    /// <summary>Schedules a title for a completed answer when its conversation has none yet. Never throws.</summary>
    public async Task OnAnswerCompletedAsync(AnswerOutcome e)
    {
        try
        {
            await ScheduleTitleAsync(e).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Titles are best effort; the answer-finished handler must not throw.
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
        if (model is null || _health.IsBlocked)
        {
            return;
        }

        var messages = await _store.GetMessagesAsync(e.ConversationId, CancellationToken.None).ConfigureAwait(false);
        var question = messages.First(m => m.Role == MessageRole.User);
        var source = await _branches.GetSourceAsync(e.ConversationId, CancellationToken.None).ConfigureAwait(false);
        // A title needs the gist, not a pasted document (low finding gap-unprompted-4): the question is cut like the answer.
        var asked = ConversationRules.FirstTextElements(source is null ? question.Text : BranchContext.QuotedQuestion(source.Quote, question.Text), TitleQuestionExcerpt);
        var excerpt = ConversationRules.FirstTextElements(e.Text, TitleAnswerExcerpt);
        var now = _clock.UtcNow;
        var task = new GenerationTaskRecord(GenerationTaskId.New(), GenerationTaskKind.Title, e.ConversationId, null, model, [question.Id, e.MessageId], now);
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
                new ChatTurn(ChatRole.System, TitlePrompt),
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automatic title for {ConversationId} not scheduled")]
    private static partial void LogTitleNotScheduled(ILogger logger, Exception exception, ConversationId conversationId);
}
