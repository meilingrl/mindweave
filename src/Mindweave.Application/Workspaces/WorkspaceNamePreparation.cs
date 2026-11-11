using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Conversations;
using Mindweave.Application.Preferences;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Workspaces;

/// <summary>
/// A workspace's last suggested names (2026-11-11): 2–3 candidates, when they were made, and from how many titled
/// conversations, so a rename can show one at once and the background knows when they are worth making again.
/// </summary>
public sealed record PreparedNames(IReadOnlyList<WorkspaceName> Names, DateTimeOffset At, int TitledConversations);

/// <summary>
/// Each workspace's last suggested names, kept across restarts in the preference store (no migration; per data directory
/// like every preference). Best effort: a failed read is no names, a failed write is logged and forgotten.
/// </summary>
public interface IWorkspaceNameCache
{
    /// <summary>A workspace's names were stored, changed or forgotten. Raised on worker threads.</summary>
    event EventHandler<WorkspaceId>? Changed;

    /// <summary>The names kept for the workspace, without <paramref name="current"/> (the name it has now); null when none were kept.</summary>
    Task<PreparedNames?> GetAsync(WorkspaceId workspace, WorkspaceName? current, CancellationToken cancellationToken);

    Task SetAsync(WorkspaceId workspace, PreparedNames names, CancellationToken cancellationToken);

    /// <summary>The workspace now has <paramref name="name"/>: a kept candidate equal to it is dropped (it was taken).</summary>
    Task RenamedAsync(WorkspaceId workspace, WorkspaceName name, CancellationToken cancellationToken);

    /// <summary>The workspace was deleted: its names go too.</summary>
    Task ForgetAsync(WorkspaceId workspace, CancellationToken cancellationToken);
}

/// <summary>One preference key per workspace, holding <c>{"names":[…],"at":ms,"count":n}</c>.</summary>
public sealed partial class WorkspaceNameCache(IPreferenceStore store, ILogger<WorkspaceNameCache> logger) : IWorkspaceNameCache
{
    public const string KeyPrefix = "workspace.names:";

    // Workspaces deleted during this run (review 2026-11-12): a request still running when its workspace was deleted
    // answers afterwards, and its names must not be kept for a workspace that is gone. Ids are never reused.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<WorkspaceId, byte> _forgotten = new();

    public event EventHandler<WorkspaceId>? Changed;

    public static string KeyFor(WorkspaceId workspace) => KeyPrefix + workspace;

    public async Task<PreparedNames?> GetAsync(WorkspaceId workspace, WorkspaceName? current, CancellationToken cancellationToken)
    {
        string? raw;
        try
        {
            raw = await store.GetAsync(KeyFor(workspace), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Names are a convenience: unreadable means none were kept.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReadFailed(logger, ex);
            return null;
        }

        var kept = Parse(raw);
        return kept is null || current is null
            ? kept
            : kept with { Names = [.. kept.Names.Where(n => !string.Equals(n.Value, current.Value, StringComparison.OrdinalIgnoreCase))] };
    }

    public async Task SetAsync(WorkspaceId workspace, PreparedNames names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (_forgotten.ContainsKey(workspace))
        {
            return;
        }

        try
        {
            await store.SetAsync(KeyFor(workspace), Format(names), cancellationToken).ConfigureAwait(false);

            // Deleted while this was written: the forgetting may have run first, so it is done again.
            if (_forgotten.ContainsKey(workspace))
            {
                await store.RemoveAsync(KeyFor(workspace), cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Not kept this time: the next request makes them again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogWriteFailed(logger, ex);
            return;
        }

        Changed?.Invoke(this, workspace);
    }

    public async Task RenamedAsync(WorkspaceId workspace, WorkspaceName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        var kept = await GetAsync(workspace, null, cancellationToken).ConfigureAwait(false);
        var left = await GetAsync(workspace, name, cancellationToken).ConfigureAwait(false);
        if (kept is not null && left is not null && left.Names.Count != kept.Names.Count)
        {
            await SetAsync(workspace, left, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ForgetAsync(WorkspaceId workspace, CancellationToken cancellationToken)
    {
        _forgotten.TryAdd(workspace, 0);
        try
        {
            await store.RemoveAsync(KeyFor(workspace), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A leftover key of a deleted workspace is never read again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogWriteFailed(logger, ex);
            return;
        }

        Changed?.Invoke(this, workspace);
    }

    /// <summary>The stored form; anything unreadable or invalid is no names (a name that is no longer valid is dropped).</summary>
    public static PreparedNames? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(raw);
            var root = json.RootElement;
            var names = new List<WorkspaceName>();
            foreach (var item in root.GetProperty("names").EnumerateArray())
            {
                if (item.GetString() is { } value && WorkspaceName.TryCreate(value, out var name, out _))
                {
                    names.Add(name!);
                }
            }

            return new PreparedNames(
                names,
                DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("at").GetInt64()),
                Math.Max(0, root.GetProperty("count").GetInt32()));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static string Format(PreparedNames names)
    {
        ArgumentNullException.ThrowIfNull(names);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("names");
            foreach (var name in names.Names)
            {
                writer.WriteStringValue(name.Value);
            }

            writer.WriteEndArray();
            writer.WriteNumber("at", names.At.ToUnixTimeMilliseconds());
            writer.WriteNumber("count", names.TitledConversations);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kept workspace names could not be read")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workspace names could not be kept")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception);
}

/// <summary>
/// When a workspace's names are worth making again (user decision of 2026-11-11). Titled conversations count: those with
/// an automatic or manual title, archived ones (and those under them) left out. The first names come at
/// <see cref="FirstAt"/>; after that, once the count has grown by <see cref="Threshold"/> of the count now, which grows
/// with the workspace so a large one is not asked about again for every few conversations.
/// </summary>
public static class NameRefreshRule
{
    /// <summary>Names are first made once this many conversations have titles.</summary>
    public const int FirstAt = 3;

    /// <summary>The largest growth ever waited for.</summary>
    public const int MaxThreshold = 20;

    /// <summary>T(n): 3 up to 20 titled conversations, then 15 % of them rounded up, at most <see cref="MaxThreshold"/>.</summary>
    public static int Threshold(int titled)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(titled);
        return titled <= 20 ? 3 : Math.Min(MaxThreshold, ((titled * 15) + 99) / 100);
    }

    /// <summary>
    /// No names yet and at least <see cref="FirstAt"/> titled conversations, or the count grew since the kept names by at
    /// least <see cref="Threshold"/> (deleted or archived conversations lower the count; growth is measured from the kept one).
    /// </summary>
    public static bool IsDue(int titled, PreparedNames? kept) =>
        kept is null ? titled >= FirstAt : titled - kept.TitledConversations >= Threshold(titled);

    public static int TitledConversations(IEnumerable<ConversationSummary> conversations)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        return conversations.Count(c => c.Conversation.Title is not null && !c.IsHidden);
    }
}

/// <summary>How long the background waits after the last new title, and the clock it waits on (tests stand it still).</summary>
public sealed class WorkspaceNamePreparationOptions
{
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public TimeSpan Wait { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Makes a workspace's names in the background (2026-11-11, on unless turned off in 设置 › 提示), so a rename shows one at
/// once. Listens to titles being set in any workspace; when <see cref="NameRefreshRule.IsDue"/> holds, it waits
/// <see cref="WorkspaceNamePreparationOptions.Wait"/> after the last new title (each restarts the wait), then asks once,
/// at idle priority: never ahead of, and never keeping a slot from, answers, summaries, titles or naming. One such
/// request at a time in the whole application; none while storage is paused, during an exit, or without a model.
/// Failures are only logged (by kind); the next new title tries again.
/// </summary>
public sealed partial class WorkspaceNamePreparer : IDisposable
{
    private readonly ConversationService _conversations;
    private readonly IConversationStore _store;
    private readonly IWorkspaceStore _workspaces;
    private readonly IWorkspaceNameCache _cache;
    private readonly INewsAlerts _settings;
    private readonly WorkspaceNameSuggester _suggester;
    private readonly GenerationCoordinator _generation;
    private readonly StorageHealth _health;
    private readonly WorkspaceNamePreparationOptions _options;
    private readonly ILogger<WorkspaceNamePreparer> _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<WorkspaceId, ITimer> _timers = [];
    private readonly Dictionary<WorkspaceId, int> _seen = [];
    private readonly List<WorkspaceId> _waiting = [];
    private readonly HashSet<Task> _inFlight = [];
    private WorkspaceId? _running;
    private bool _disposed;
    private int _sent;

    public WorkspaceNamePreparer(
        ConversationService conversations,
        IConversationStore store,
        IWorkspaceStore workspaces,
        IWorkspaceNameCache cache,
        INewsAlerts settings,
        WorkspaceNameSuggester suggester,
        GenerationCoordinator generation,
        StorageHealth health,
        WorkspaceNamePreparationOptions options,
        ILogger<WorkspaceNamePreparer> logger)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        _conversations = conversations;
        _store = store;
        _workspaces = workspaces;
        _cache = cache;
        _settings = settings;
        _suggester = suggester;
        _generation = generation;
        _health = health;
        _options = options;
        _logger = logger;
        conversations.TitleSet += OnTitleSet;
    }

    /// <summary>Requests sent so far (tests).</summary>
    public int Sent => Volatile.Read(ref _sent);

    /// <summary>A wait is running for this workspace (tests).</summary>
    public bool IsWaiting(WorkspaceId workspace)
    {
        lock (_gate)
        {
            return _timers.ContainsKey(workspace);
        }
    }

    /// <summary>Completes once nothing this does is in flight: no title being looked at, no request running (tests).</summary>
    public async Task SettledAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_gate)
            {
                pending = [.. _inFlight];
            }

            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    private void OnTitleSet(object? sender, ConversationId conversation) => Track(ConsiderAsync(conversation));

    /// <summary>A title was set: if the workspace's names are due, the wait starts again from now.</summary>
    private async Task ConsiderAsync(ConversationId conversation)
    {
        if (_disposed || !await _settings.GetPrepareNamesAsync(CancellationToken.None).ConfigureAwait(false))
        {
            return;
        }

        var stored = await _store.GetAsync(conversation, CancellationToken.None).ConfigureAwait(false);
        if (stored is null)
        {
            return;
        }

        var workspace = stored.WorkspaceId;
        var titled = NameRefreshRule.TitledConversations(await _store.ListAsync(workspace, CancellationToken.None).ConfigureAwait(false));
        var kept = await _cache.GetAsync(workspace, null, CancellationToken.None).ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // A conversation titled again (renamed) does not restart a wait that runs; a newly titled one does.
            var grew = !_seen.TryGetValue(workspace, out var before) || titled > before;
            _seen[workspace] = titled;
            if (!NameRefreshRule.IsDue(titled, kept) || (!grew && _timers.ContainsKey(workspace)))
            {
                return;
            }

            ArmLocked(workspace);
        }
    }

    private void ArmLocked(WorkspaceId workspace)
    {
        if (_timers.Remove(workspace, out var old))
        {
            old.Dispose();
        }

        var slot = new StrongBox<ITimer?>();
        slot.Value = _options.Time.CreateTimer(_ => Fire(workspace, slot), null, _options.Wait, Timeout.InfiniteTimeSpan);
        _timers[workspace] = slot.Value;
    }

    /// <summary>A wait ended: only the workspace's current wait counts (one replaced meanwhile may still call back once).</summary>
    private void Fire(WorkspaceId workspace, StrongBox<ITimer?>? slot)
    {
        lock (_gate)
        {
            if (slot is not null)
            {
                if (!_timers.TryGetValue(workspace, out var current) || !ReferenceEquals(current, slot.Value))
                {
                    return;
                }

                _timers.Remove(workspace);
                current.Dispose();
            }

            if (_disposed)
            {
                return;
            }

            // One request at a time in the whole application: the others wait their turn.
            if (_running is not null)
            {
                if (!_waiting.Contains(workspace) && _running != workspace)
                {
                    _waiting.Add(workspace);
                }

                return;
            }

            _running = workspace;
        }

        Track(RunAsync(workspace));
    }

    private async Task RunAsync(WorkspaceId workspace)
    {
        try
        {
            await PrepareAsync(workspace).ConfigureAwait(false);
        }
        finally
        {
            WorkspaceId? next = null;
            lock (_gate)
            {
                _running = null;
                if (!_disposed && _waiting.Count > 0)
                {
                    next = _waiting[0];
                    _waiting.RemoveAt(0);
                }
            }

            if (next is { } then)
            {
                Fire(then, null);
            }
        }
    }

    private async Task PrepareAsync(WorkspaceId workspace)
    {
        // Looked at again when the wait ends: the setting, an exit, storage and the count may all have changed.
        if (_stop.IsCancellationRequested || _generation.IsClosed || _health.IsBlocked
            || !await _settings.GetPrepareNamesAsync(CancellationToken.None).ConfigureAwait(false))
        {
            return;
        }

        var name = (await _workspaces.ListAsync(CancellationToken.None).ConfigureAwait(false)).FirstOrDefault(w => w.Id == workspace)?.Name;
        if (name is null)
        {
            return;
        }

        var titled = NameRefreshRule.TitledConversations(await _store.ListAsync(workspace, CancellationToken.None).ConfigureAwait(false));
        if (!NameRefreshRule.IsDue(titled, await _cache.GetAsync(workspace, null, CancellationToken.None).ConfigureAwait(false)))
        {
            return;
        }

        NameSuggestions result;
        try
        {
            Interlocked.Increment(ref _sent);
            result = await _suggester.SuggestAsync(workspace, name, NameRequestMode.Prepared, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped by an exit or the application closing: nothing to show, nothing to retry now.
            return;
        }

        switch (result.Problem)
        {
            case NameSuggestionProblem.Deferred:
                // Gave its slot to the user's work: tried again after the usual wait.
                lock (_gate)
                {
                    if (!_disposed && !_timers.ContainsKey(workspace))
                    {
                        ArmLocked(workspace);
                    }
                }

                break;
            case NameSuggestionProblem.Failed:
                LogFailed(_logger, result.Error);
                break;
            case { } other:
                LogSkipped(_logger, other);
                break;
        }
    }

    private void Track(Task task)
    {
        lock (_gate)
        {
            _inFlight.Add(task);
        }

        _ = task.ContinueWith(
            t =>
            {
                lock (_gate)
                {
                    _inFlight.Remove(t);
                }

                if (t.Exception is { } ex)
                {
                    LogCrashed(_logger, ex.InnerException ?? ex);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Stops waiting and any request running (the application is closing).</summary>
    public void Dispose()
    {
        List<ITimer> timers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            timers = [.. _timers.Values];
            _timers.Clear();
            _waiting.Clear();
        }

        _conversations.TitleSet -= OnTitleSet;
        foreach (var timer in timers)
        {
            timer.Dispose();
        }

        // Cancelled, not disposed: a request ending meanwhile may still read the token.
        _stop.Cancel();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Background workspace names failed: {Kind}")]
    private static partial void LogFailed(ILogger logger, GenerationErrorKind? kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Background workspace names skipped: {Reason}")]
    private static partial void LogSkipped(ILogger logger, NameSuggestionProblem reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Background workspace names stopped unexpectedly")]
    private static partial void LogCrashed(ILogger logger, Exception exception);
}
