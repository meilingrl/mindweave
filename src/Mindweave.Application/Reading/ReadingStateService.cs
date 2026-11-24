using Microsoft.Extensions.Logging;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Reading;

namespace Mindweave.Application.Reading;

/// <summary>
/// Owner of reading positions and unsent drafts (architecture §2.2). The newest value of each
/// (conversation, slot) lives in memory, so a page reopened right after it was left never reads an older
/// stored value; writes are coalesced and trail by <see cref="SaveDelay"/>, and <see cref="FlushAsync"/>
/// writes everything before the window closes on exit. Losing page state (a reading position) never blocks reading or
/// asking; a draft is the user's input, core content (architecture §12.1), so storage refusing it pauses new work like
/// any core write until the draft is saved (architecture review R1).
/// </summary>
public sealed partial class ReadingStateService : IReadingState, IDisposable
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
    private readonly StorageHealth? _health;

    // New-exploration drafts (health check 2026-11-12, requirements-3): one per workspace, kept as a preference, since
    // no conversation exists yet for the draft table to point at.
    private readonly IPreferenceStore? _preferences;
    private readonly Dictionary<Core.Workspaces.WorkspaceId, string> _newDrafts = [];
    private readonly HashSet<Core.Workspaces.WorkspaceId> _dirtyNewDrafts = [];

    /// <summary>The preference key of a workspace's new-exploration draft.</summary>
    public static string NewDraftKey(Core.Workspaces.WorkspaceId workspace) => $"draft.new-exploration:{workspace}";

    // One open obligation per draft storage refused; resolved when that draft's newest value is saved.
    private readonly Dictionary<(ConversationId, ViewerSlotKey), StorageObligation> _draftObligations = [];
    private bool _scheduled;

    public ReadingStateService(IReadingStateStore store, IClock clock, ILogger<ReadingStateService> logger, TimeSpan? saveDelay = null, StorageHealth? health = null, IPreferenceStore? preferences = null)
    {
        _preferences = preferences;
        _store = store;
        _clock = clock;
        _logger = logger;
        _health = health;
        SaveDelay = saveDelay ?? TimeSpan.FromMilliseconds(500);
        health?.RegisterRetry(_ => FlushAsync());
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

    public async Task<string> GetNewDraftAsync(Core.Workspaces.WorkspaceId workspace, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_newDrafts.TryGetValue(workspace, out var known))
            {
                return known;
            }
        }

        if (_preferences is null)
        {
            return string.Empty;
        }

        try
        {
            return await _preferences.GetAsync(NewDraftKey(workspace), cancellationToken).ConfigureAwait(false) ?? string.Empty;
        }
#pragma warning disable CA1031 // See GetDraftAsync: a failed read opens the page with an empty box.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogNewDraftFailed(_logger, ex, workspace);
            return string.Empty;
        }
    }

    public void SetNewDraft(Core.Workspaces.WorkspaceId workspace, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            if (_newDrafts.TryGetValue(workspace, out var known) && known == text)
            {
                return;
            }

            _newDrafts[workspace] = text;
            _dirtyNewDrafts.Add(workspace);
        }

        ScheduleSave();
    }

    /// <summary>
    /// A deleted workspace's new-exploration draft goes with it, written or not. The store removes what is written in the
    /// delete's own transaction; a write that was already under way is waited for and removed here.
    /// </summary>
    public async Task ForgetNewDraftAsync(Core.Workspaces.WorkspaceId workspace)
    {
        await _flushing.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _newDrafts.Remove(workspace);
                _dirtyNewDrafts.Remove(workspace);
            }

            await SaveNewDraftAsync(workspace, string.Empty).ConfigureAwait(false);
        }
        finally
        {
            _flushing.Release();
        }
    }

    /// <summary>
    /// Writes every pending change now. Called on exit before the window closes; safe to call any time. A value whose
    /// write fails stays pending (dirty) for the next flush instead of being dropped. Returns false when a draft could
    /// not be written: drafts are the user's input, while a reading position is page state that may degrade.
    /// </summary>
    public async Task<bool> FlushAsync()
    {
        await _flushing.WaitAsync().ConfigureAwait(false);
        try
        {
            List<((ConversationId Conversation, ViewerSlotKey Slot) Key, ReadingPosition Value)> positions;
            List<((ConversationId Conversation, ViewerSlotKey Slot) Key, string Value)> drafts;
            List<(Core.Workspaces.WorkspaceId Workspace, string Value)> newDrafts;
            lock (_gate)
            {
                _scheduled = false;
                positions = [.. _dirtyPositions.Select(k => (k, _positions[k]))];
                drafts = [.. _dirtyDrafts.Select(k => (k, _drafts[k]))];
                newDrafts = [.. _dirtyNewDrafts.Select(w => (w, _newDrafts[w]))];
                _dirtyPositions.Clear();
                _dirtyDrafts.Clear();
                _dirtyNewDrafts.Clear();
            }

            var now = _clock.UtcNow;
            foreach (var (key, value) in positions)
            {
                if (!await SaveAsync(key.Conversation, () => _store.SavePositionAsync(key.Conversation, key.Slot, value, now, CancellationToken.None)).ConfigureAwait(false))
                {
                    MarkDirty(_dirtyPositions, key);
                }
            }

            var draftsSaved = true;
            foreach (var (key, value) in drafts)
            {
                if (!await SaveDraftAsync(key, () => _store.SaveDraftAsync(key.Conversation, key.Slot, value, now, CancellationToken.None)).ConfigureAwait(false))
                {
                    MarkDirty(_dirtyDrafts, key);
                    draftsSaved = false;
                }
            }

            foreach (var (workspace, value) in newDrafts)
            {
                if (!await SaveNewDraftAsync(workspace, value).ConfigureAwait(false))
                {
                    lock (_gate)
                    {
                        _dirtyNewDrafts.Add(workspace);
                    }

                    draftsSaved = false;
                }
            }

            return draftsSaved;
        }
        finally
        {
            _flushing.Release();
        }
    }

    public void Dispose() => _flushing.Dispose();

    /// <summary>Writes one new-exploration draft (an empty one removes it); storage refusing it is reported like any draft.</summary>
    private async Task<bool> SaveNewDraftAsync(Core.Workspaces.WorkspaceId workspace, string value)
    {
        if (_preferences is null)
        {
            return true;
        }

        try
        {
            if (value.Length == 0)
            {
                await _preferences.RemoveAsync(NewDraftKey(workspace), CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await _preferences.SetAsync(NewDraftKey(workspace), value, CancellationToken.None).ConfigureAwait(false);
            }

            return true;
        }
        catch (StorageWriteException ex)
        {
            LogNewDraftFailed(_logger, ex, workspace);
            _health?.Report(ex);
            return false;
        }
#pragma warning disable CA1031 // Any other failure: logged; the in-memory value stays current and pending.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogNewDraftFailed(_logger, ex, workspace);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "New-exploration draft of workspace {Workspace} could not be read or saved")]
    private static partial void LogNewDraftFailed(ILogger logger, Exception exception, Core.Workspaces.WorkspaceId workspace);

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

    /// <summary>
    /// Pending again after a failed write. A newer value set meanwhile is already pending and is what the next flush
    /// writes, since only the newest value per key is kept. No save is scheduled: a broken disk must not spin; the next
    /// change or flush tries again.
    /// </summary>
    private void MarkDirty(HashSet<(ConversationId, ViewerSlotKey)> dirty, (ConversationId, ViewerSlotKey) key)
    {
        lock (_gate)
        {
            dirty.Add(key);
        }
    }

    /// <summary>
    /// Writes one draft; flushes are serialized, so a success means the newest value then known is stored and any
    /// obligation for the draft is resolved. Storage refusing it opens one (or reports again while one is open).
    /// </summary>
    private async Task<bool> SaveDraftAsync((ConversationId Conversation, ViewerSlotKey Slot) key, Func<Task> write)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
        catch (StorageWriteException ex) when (_health is not null)
        {
            LogSaveFailed(_logger, ex, key.Conversation);
            lock (_gate)
            {
                if (!_draftObligations.ContainsKey(key))
                {
                    _draftObligations[key] = _health.Retain(ex);
                    return false;
                }
            }

            _health.Report(ex);
            return false;
        }
#pragma warning disable CA1031 // Any other failure: logged, and the in-memory value stays current and pending.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSaveFailed(_logger, ex, key.Conversation);
            return false;
        }

        StorageObligation? resolved;
        lock (_gate)
        {
            _draftObligations.Remove(key, out resolved);
        }

        resolved?.Resolve();
        return true;
    }

    private async Task<bool> SaveAsync(ConversationId conversation, Func<Task> write)
    {
        try
        {
            await write().ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // Page state is best effort: a failed write is logged and the in-memory value stays current and pending.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSaveFailed(_logger, ex, conversation);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading state for {ConversationId} could not be read")]
    private static partial void LogReadFailed(ILogger logger, Exception exception, ConversationId conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading state for {ConversationId} could not be saved")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, ConversationId conversationId);
}
