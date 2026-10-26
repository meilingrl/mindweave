using Microsoft.Extensions.Logging;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Reading;

namespace Mindweave.Application.Reading;

/// <summary>
/// Owner of reading positions and unsent drafts (architecture §2.2). The newest value of each
/// (conversation, slot) lives in memory, so a page reopened right after it was left never reads an older
/// stored value; writes are coalesced and trail by <see cref="SaveDelay"/>, and <see cref="FlushAsync"/>
/// writes everything before the database closes. Losing page state never blocks reading or asking.
/// </summary>
public sealed partial class ReadingStateService : IDisposable
{
    private readonly IReadingStateStore _store;
    private readonly IClock _clock;
    private readonly ILogger<ReadingStateService> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<(ConversationId, ViewerSlotKey), ReadingPosition> _positions = [];
    private readonly Dictionary<(ConversationId, ViewerSlotKey), string> _drafts = [];
    private readonly HashSet<(ConversationId, ViewerSlotKey)> _dirtyPositions = [];
    private readonly HashSet<(ConversationId, ViewerSlotKey)> _dirtyDrafts = [];
    private readonly SemaphoreSlim _flushing = new(1, 1);
    private bool _scheduled;

    public ReadingStateService(IReadingStateStore store, IClock clock, ILogger<ReadingStateService> logger, TimeSpan? saveDelay = null)
    {
        _store = store;
        _clock = clock;
        _logger = logger;
        SaveDelay = saveDelay ?? TimeSpan.FromMilliseconds(500);
    }

    public TimeSpan SaveDelay { get; }

    public async Task<ReadingPosition?> GetPositionAsync(ConversationId conversation, ViewerSlotKey slot, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_positions.TryGetValue((conversation, slot), out var known))
            {
                return known;
            }
        }

        try
        {
            return await _store.GetPositionAsync(conversation, slot, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Page state is best effort; a failed read opens the page at its default place.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogReadFailed(_logger, ex, conversation);
            return null;
        }
    }

    public void SetPosition(ConversationId conversation, ViewerSlotKey slot, ReadingPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (position.Locator is { Length: > ReadingPosition.MaxLocatorLength })
        {
            position = position with { Locator = null };
        }

        lock (_gate)
        {
            if (_positions.TryGetValue((conversation, slot), out var known) && known == position)
            {
                return;
            }

            _positions[(conversation, slot)] = position;
            _dirtyPositions.Add((conversation, slot));
        }

        ScheduleSave();
    }

    public async Task<string> GetDraftAsync(ConversationId conversation, ViewerSlotKey slot, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_drafts.TryGetValue((conversation, slot), out var known))
            {
                return known;
            }
        }

        try
        {
            return await _store.GetDraftAsync(conversation, slot, cancellationToken).ConfigureAwait(false) ?? string.Empty;
        }
#pragma warning disable CA1031 // See GetPositionAsync.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogReadFailed(_logger, ex, conversation);
            return string.Empty;
        }
    }

    public void SetDraft(ConversationId conversation, ViewerSlotKey slot, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            if (_drafts.TryGetValue((conversation, slot), out var known) && known == text)
            {
                return;
            }

            _drafts[(conversation, slot)] = text;
            _dirtyDrafts.Add((conversation, slot));
        }

        ScheduleSave();
    }

    /// <summary>Writes every pending change now. Called on exit before the database closes; safe to call any time.</summary>
    public async Task FlushAsync()
    {
        await _flushing.WaitAsync().ConfigureAwait(false);
        try
        {
            List<((ConversationId Conversation, ViewerSlotKey Slot) Key, ReadingPosition Value)> positions;
            List<((ConversationId Conversation, ViewerSlotKey Slot) Key, string Value)> drafts;
            lock (_gate)
            {
                _scheduled = false;
                positions = [.. _dirtyPositions.Select(k => (k, _positions[k]))];
                drafts = [.. _dirtyDrafts.Select(k => (k, _drafts[k]))];
                _dirtyPositions.Clear();
                _dirtyDrafts.Clear();
            }

            var now = _clock.UtcNow;
            foreach (var (key, value) in positions)
            {
                await SaveAsync(key.Conversation, () => _store.SavePositionAsync(key.Conversation, key.Slot, value, now, CancellationToken.None)).ConfigureAwait(false);
            }

            foreach (var (key, value) in drafts)
            {
                await SaveAsync(key.Conversation, () => _store.SaveDraftAsync(key.Conversation, key.Slot, value, now, CancellationToken.None)).ConfigureAwait(false);
            }
        }
        finally
        {
            _flushing.Release();
        }
    }

    public void Dispose() => _flushing.Dispose();

    private void ScheduleSave()
    {
        lock (_gate)
        {
            if (_scheduled)
            {
                return;
            }

            _scheduled = true;
        }

        _ = Task.Delay(SaveDelay).ContinueWith(_ => FlushAsync(), TaskScheduler.Default).Unwrap();
    }

    private async Task SaveAsync(ConversationId conversation, Func<Task> write)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Page state is best effort: a failed write is logged and the in-memory value stays current.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSaveFailed(_logger, ex, conversation);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading state for {ConversationId} could not be read")]
    private static partial void LogReadFailed(ILogger logger, Exception exception, ConversationId conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading state for {ConversationId} could not be saved")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, ConversationId conversationId);
}
