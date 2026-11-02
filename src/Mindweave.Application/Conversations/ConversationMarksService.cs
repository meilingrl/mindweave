using Microsoft.Extensions.Logging;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Conversations;

/// <summary>
/// Owner of the "new answer" marks. An answer that finishes complete or stopped with text while its conversation is not
/// on screen marks the conversation; showing the conversation clears it. The set of marked conversations is page state
/// like a reading position: kept in memory, stored under one preference key (no migration) so it survives a restart,
/// and never worth an error. While storage is paused nothing is written; the newest set is written once it recovers.
/// </summary>
public sealed partial class ConversationMarksService : IConversationMarks
{
    public const string UnreadKey = "conversations.unread";

    private readonly GenerationCoordinator _generation;
    private readonly IPreferenceStore _store;
    private readonly IStorageStatus _storage;
    private readonly ILogger<ConversationMarksService> _logger;
    private readonly Lock _gate = new();
    private readonly HashSet<ConversationId> _unread = [];
    private ConversationId? _viewing;
    private long _version;
    private long _savedVersion;
    private Task _writes = Task.CompletedTask;

    public ConversationMarksService(GenerationCoordinator generation, IPreferenceStore store, IStorageStatus storage, ILogger<ConversationMarksService> logger)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(storage);
        _generation = generation;
        _store = store;
        _storage = storage;
        _logger = logger;
        _generation.AnswerFinished += OnAnswerFinished;
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

    /// <summary>Clears the mark without showing the conversation (seeding screenshots, tests).</summary>
    public void MarkSeen(ConversationId conversation)
    {
        bool changed;
        lock (_gate)
        {
            changed = Remove(conversation);
        }

        Announce(changed);
    }

    /// <summary>Reads the stored marks; called at start once the database is open. Unreadable means none.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
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
            return;
        }

        var ids = Parse(stored);
        bool changed;
        lock (_gate)
        {
            // Whatever happened since start (normally nothing) stays; the conversation on screen stays seen.
            var before = _unread.Count;
            _unread.UnionWith(ids.Where(id => id != _viewing));
            changed = _unread.Count != before;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
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
        string value;
        long version;
        lock (_gate)
        {
            if (_savedVersion == _version)
            {
                return;
            }

            version = _version;
            value = string.Join(' ', _unread.Select(id => id.ToString()).Order(StringComparer.Ordinal));
        }

        if (_storage.IsBlocked)
        {
            // Storage refuses writes while paused (§9.3); the marks are written when it recovers.
            return;
        }

        try
        {
            await _store.SetAsync(UnreadKey, value, CancellationToken.None).ConfigureAwait(false);
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

    private void OnAnswerFinished(object? sender, AnswerOutcome e)
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unread conversation marks could not be saved")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);
}
