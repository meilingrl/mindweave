using Microsoft.Extensions.Logging;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Conversations;

/// <summary>
/// Owner of the "new answer" marks. An answer that finishes complete or stopped with text while its conversation is not
/// on screen marks the conversation; showing the conversation clears it. The set of marked conversations is page state
/// like a reading position: kept in memory, stored under one preference key (no migration) so it survives a restart,
/// and never worth an error. While storage is paused a write is refused like any other (<see cref="StorageWriteException"/>
/// from <see cref="StorageHealth.ThrowIfBlocked"/>) and stays pending; the newest set is written once storage recovers.
/// The stored set is only ever replaced after it has been read, so a failed read at start cannot wipe it, and a deleted
/// conversation leaves it.
/// </summary>
public sealed partial class ConversationMarksService : IConversationMarks
{
    public const string UnreadKey = "conversations.unread";

    private readonly GenerationCoordinator _generation;
    private readonly ConversationService _conversations;
    private readonly IPreferenceStore _store;
    private readonly StorageHealth _storage;
    private readonly ILogger<ConversationMarksService> _logger;
    private readonly Lock _gate = new();
    private readonly HashSet<ConversationId> _unread = [];

    /// <summary>Seen (or deleted) before the stored set was read: the stored set must not bring these back.</summary>
    private readonly HashSet<ConversationId> _seenBeforeLoad = [];
    private bool _loaded;
    private ConversationId? _viewing;
    private long _version;
    private long _savedVersion;
    private Task _writes = Task.CompletedTask;

    public ConversationMarksService(
        GenerationCoordinator generation,
        ConversationService conversations,
        IPreferenceStore store,
        StorageHealth storage,
        ILogger<ConversationMarksService> logger)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(storage);
        _generation = generation;
        _conversations = conversations;
        _store = store;
        _storage = storage;
        _logger = logger;
        // Decided when the answer visibly ends, not after its terminal write: on a slow disk the reader may have moved on.
        _generation.AnswerEnded += OnAnswerEnded;
        conversations.Deleted += (_, id) => MarkSeen(id);
        _storage.Changed += (_, _) =>
        {
            if (!_storage.IsBlocked)
            {
                _ = FlushAsync();
            }
        };
    }

    public event EventHandler? Changed;

    public bool IsSummarizing(ConversationId conversation) => _generation.HasUnfinishedSummary(conversation);

    public bool IsUnread(ConversationId conversation)
    {
        lock (_gate)
        {
            return _unread.Contains(conversation);
        }
    }

    public void SetViewing(ConversationId? conversation)
    {
        bool changed;
        lock (_gate)
        {
            _viewing = conversation;
            changed = conversation is { } shown && Remove(shown);
        }

        Announce(changed);
    }

    public async Task<IReadOnlySet<WorkspaceId>> WorkspacesWithUnreadAsync(IReadOnlyCollection<WorkspaceId> workspaces, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        HashSet<ConversationId> unread;
        lock (_gate)
        {
            unread = [.. _unread];
        }

        var result = new HashSet<WorkspaceId>();
        if (unread.Count == 0)
        {
            return result;
        }

        foreach (var workspace in workspaces)
        {
            var listed = await _conversations.ListAsync(workspace, cancellationToken).ConfigureAwait(false);
            if (listed.Any(s => !s.IsHidden && unread.Contains(s.Conversation.Id)))
            {
                result.Add(workspace);
            }
        }

        return result;
    }

    /// <summary>Clears the mark without showing the conversation (a deleted conversation, seeding screenshots, tests).</summary>
    public void MarkSeen(ConversationId conversation)
    {
        bool changed;
        lock (_gate)
        {
            changed = Remove(conversation);
        }

        Announce(changed);
    }

    /// <summary>
    /// Reads the stored marks; called at start once the database is open. A failed read starts without them, and the
    /// next write reads again first instead of replacing what is stored.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!await TryLoadAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        bool pending;
        lock (_gate)
        {
            pending = _savedVersion != _version;
        }

        if (pending)
        {
            // Something seen before the read is still listed in storage: written again without it now, not at exit.
            _ = FlushAsync();
        }
    }

    /// <summary>Merges the stored set into memory once; true when it has been read (now or before).</summary>
    private async Task<bool> TryLoadAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return true;
            }
        }

        string? stored;
        try
        {
            stored = await _store.GetAsync(UnreadKey, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Marks are page state: a failed read starts without them.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogReadFailed(_logger, ex);
            return false;
        }

        var ids = Parse(stored);
        bool changed;
        lock (_gate)
        {
            if (_loaded)
            {
                return true;
            }

            // Whatever happened since start stays: what was seen meanwhile (and the conversation on screen) stays seen.
            var before = _unread.Count;
            _unread.UnionWith(ids.Where(id => id != _viewing && !_seenBeforeLoad.Contains(id)));
            changed = _unread.Count != before;
            if (ids.Any(id => id == _viewing || _seenBeforeLoad.Contains(id)))
            {
                // The stored set still lists something seen since start: it is written again without it.
                _version++;
            }

            _seenBeforeLoad.Clear();
            _loaded = true;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    /// <summary>
    /// Writes the newest marks if they changed since the last successful write. Serialized, so a finished flush means
    /// everything known before it was attempted. Called after each change and on exit; a failure is logged and the
    /// marks stay pending for the next change or flush.
    /// </summary>
    public Task FlushAsync()
    {
        lock (_gate)
        {
            // The write runs on the thread pool after the previous one, never inline under this lock.
            _writes = _writes.ContinueWith(_ => WriteAsync(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            return _writes;
        }
    }

    /// <summary>Never faults, so the chain of writes never breaks.</summary>
    private async Task WriteAsync()
    {
        lock (_gate)
        {
            if (_savedVersion == _version)
            {
                return;
            }
        }

        // The whole set replaces the stored one, so it must include what is stored: never written before a read.
        if (!await TryLoadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            return;
        }

        string value;
        long version;
        lock (_gate)
        {
            version = _version;
            value = string.Join(' ', _unread.Select(id => id.ToString()).Order(StringComparer.Ordinal));
        }

        try
        {
            // Refused while storage is paused (§9.3) like every other write; pending until it recovers.
            _storage.ThrowIfBlocked();
            await _store.SetAsync(UnreadKey, value, CancellationToken.None).ConfigureAwait(false);
        }
        catch (StorageWriteException ex) when (_storage.IsBlocked)
        {
            LogSavePaused(_logger, ex);
            return;
        }
#pragma warning disable CA1031 // Page state: logged, kept in memory and pending.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSaveFailed(_logger, ex);
            return;
        }

        lock (_gate)
        {
            _savedVersion = Math.Max(_savedVersion, version);
        }
    }

    internal static IReadOnlyList<ConversationId> Parse(string? stored)
    {
        var ids = new List<ConversationId>();
        foreach (var part in (stored ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guid.TryParseExact(part, "D", out var value))
            {
                ids.Add(new ConversationId(value));
            }
        }

        return ids;
    }

    private void OnAnswerEnded(object? sender, AnswerOutcome e)
    {
        if (e.State is not (MessageState.Complete or MessageState.Stopped) || string.IsNullOrWhiteSpace(e.Text))
        {
            return;
        }

        bool changed;
        lock (_gate)
        {
            changed = _viewing != e.ConversationId && _unread.Add(e.ConversationId);
            if (changed)
            {
                _version++;
            }
        }

        Announce(changed);
    }

    /// <summary>Under the lock.</summary>
    private bool Remove(ConversationId conversation)
    {
        if (!_loaded && _seenBeforeLoad.Add(conversation))
        {
            // The stored set (not read yet) may list it: the first write after the read leaves it out.
            _version++;
        }

        if (!_unread.Remove(conversation))
        {
            return false;
        }

        _version++;
        return true;
    }

    private void Announce(bool changed)
    {
        if (!changed)
        {
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        _ = FlushAsync();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unread conversation marks could not be read")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Unread conversation marks wait for storage to recover")]
    private static partial void LogSavePaused(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unread conversation marks could not be saved")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);
}
