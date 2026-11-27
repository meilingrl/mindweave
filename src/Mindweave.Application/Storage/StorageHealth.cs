using Microsoft.Extensions.Logging;

namespace Mindweave.Application.Storage;

/// <summary>Whether storage takes core writes (requirements §9.3), as the pages read it.</summary>
public interface IStorageStatus
{
    /// <summary>True from a failed core write until storage takes writes again and nothing kept is left unsaved.</summary>
    bool IsBlocked { get; }

    /// <summary><see cref="IsBlocked"/> changed. Raised on worker threads.</summary>
    event EventHandler? Changed;
}

/// <summary>Asks whether storage takes writes again; throws <see cref="StorageWriteException"/> while it does not.</summary>
public interface IStorageProbe
{
    Task ProbeWriteAsync(CancellationToken cancellationToken);
}

public sealed class StorageHealthOptions
{
    /// <summary>How often kept writes are retried, and storage probed, while storage is unavailable.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Content an owner kept after a storage failure, still to be saved. Storage does not count as recovered while any is
/// open. Only the owner that saves the content resolves it; resolving twice is harmless.
/// </summary>
public sealed class StorageObligation
{
    private readonly StorageHealth _health;
    private int _resolved;

    internal StorageObligation(StorageHealth health) => _health = health;

    public bool IsResolved => Volatile.Read(ref _resolved) != 0;

    public void Resolve()
    {
        if (Interlocked.Exchange(ref _resolved, 1) == 0)
        {
            _health.OnResolved();
        }
    }
}

/// <summary>
/// Application-wide storage health (requirements §9.3; architecture review F1). A failed core write blocks new work:
/// questions, retries, branches, summary and archive commits, and AI requests not yet sent. Owners keep what they could
/// not save and register how to retry it; while blocked, a loop retries those writes and probes storage, and clears the
/// block only when the probe succeeds, nothing failed since the pass began, and no kept content is still unsaved.
/// </summary>
/// <remarks>
/// The lock here is a leaf: nothing is awaited or called back while it is held, so owners may report from inside their
/// own locks. Retry callbacks are registered while the application is composed, before <see cref="Start"/>.
/// </remarks>
public sealed partial class StorageHealth : IStorageStatus, IAsyncDisposable
{
    private readonly IStorageProbe _probe;
    private readonly StorageHealthOptions _options;
    private readonly ILogger<StorageHealth> _logger;
    private readonly Lock _lock = new();
    private readonly List<Func<CancellationToken, Task>> _retries = [];
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private bool _loopRunning;
    private bool _started;
    private bool _stopped;
    private bool _blocked;
    private long _epoch;
    private int _outstanding;
    private StorageFailureKind _lastKind = StorageFailureKind.IoError;

    // The log of one pause (low finding observability-6): its first failure in full, then only counts, in one line when it ends.
    private DateTimeOffset _pausedAt;
    private int _laterFailures;
    private int _failedChecks;

    public StorageHealth(IStorageProbe probe, StorageHealthOptions options, ILogger<StorageHealth> logger)
    {
        _probe = probe;
        _options = options;
        _logger = logger;
    }

    public event EventHandler? Changed;

    public bool IsBlocked
    {
        get
        {
            lock (_lock)
            {
                return _blocked;
            }
        }
    }

    /// <summary>Open kept writes; for tests and diagnostics.</summary>
    public int Outstanding
    {
        get
        {
            lock (_lock)
            {
                return _outstanding;
            }
        }
    }

    /// <summary>Whether new external work may start now: false while blocked.</summary>
    public bool TryAdmit() => !IsBlocked;

    /// <summary>
    /// Refuses a new commit while storage is blocked, as a <see cref="StorageWriteException"/> naming the last failure:
    /// callers already handle that as "nothing was stored". Not a new failure, so it does not delay recovery.
    /// </summary>
    public void ThrowIfBlocked()
    {
        StorageFailureKind kind;
        lock (_lock)
        {
            if (!_blocked)
            {
                return;
            }

            kind = _lastKind;
        }

        throw new StorageWriteException(kind, "Storage is paused after a failed write; nothing was stored.", null);
    }

    /// <summary>A core write failed and nothing of it is kept (the input stays with the user).</summary>
    public void Report(StorageWriteException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Block(failure, keep: false);
    }

    /// <summary>A core write failed and its content is kept to be saved later; recovery waits for it.</summary>
    public StorageObligation Retain(StorageWriteException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var obligation = new StorageObligation(this);
        Block(failure, keep: true);
        return obligation;
    }

    /// <summary>How an owner saves what it kept; called by the recovery loop, never under this class's lock.</summary>
    public void RegisterRetry(Func<CancellationToken, Task> retryKept)
    {
        ArgumentNullException.ThrowIfNull(retryKept);
        lock (_lock)
        {
            if (_started)
            {
                throw new InvalidOperationException("Retries are registered while the application is composed, before Start.");
            }

            _retries.Add(retryKept);
        }
    }

    /// <summary>Ends registration; called once startup is about to admit work.</summary>
    public void Start()
    {
        lock (_lock)
        {
            _started = true;
        }
    }

    /// <summary>
    /// Stops the recovery loop and waits for a pass in progress (a probe or retry already running), so nothing touches
    /// storage afterwards. Called before the database closes. Idempotent; the blocked state is kept.
    /// </summary>
    public async Task StopAsync()
    {
        Task loop;
        bool already;
        lock (_lock)
        {
            already = _stopped;
            _stopped = true;
            loop = _loop;
        }

        if (!already)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (!already && TakePauseCounts() is { } counts)
        {
            LogStillPaused(_logger, counts.Seconds, counts.LaterFailures, counts.FailedChecks);
        }
    }

    /// <summary>Stops the loop (see <see cref="StopAsync"/>); touches no storage.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    /// <summary>Runs every registered retry once, in order, now.</summary>
    public async Task RetryKeptAsync()
    {
        foreach (var retry in Retries())
        {
            await RunRetryAsync(retry, CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal void OnResolved()
    {
        lock (_lock)
        {
            _outstanding--;
        }
    }

    private void Block(StorageWriteException failure, bool keep)
    {
        bool changed;
        lock (_lock)
        {
            // The linearization point: a recovery pass that began before this sees the epoch move and does not clear.
            _epoch++;
            if (keep)
            {
                _outstanding++;
            }

            _lastKind = failure.Kind;
            changed = !_blocked;
            _blocked = true;
            if (changed)
            {
                _pausedAt = DateTimeOffset.UtcNow;
                _laterFailures = 0;
                _failedChecks = 0;
            }
            else
            {
                _laterFailures++;
            }

            EnsureLoop();
        }

        if (changed)
        {
            // One line per pause (low finding observability-6); the failures after it are counted in the line that ends it.
            LogFailure(_logger, failure);
            RaiseChanged();
        }
    }

    /// <summary>How long the pause has lasted and what failed during it, once; null when storage is not paused.</summary>
    private (long Seconds, int LaterFailures, int FailedChecks)? TakePauseCounts()
    {
        lock (_lock)
        {
            if (!_blocked)
            {
                return null;
            }

            return ((long)(DateTimeOffset.UtcNow - _pausedAt).TotalSeconds, _laterFailures, _failedChecks);
        }
    }

    /// <summary>
    /// A throwing subscriber is logged (low finding errors-7): raised from the recovery loop, it used to end the loop while
    /// it still counted as running, so storage never recovered; raised from a report, it reached the reporter.
    /// </summary>
    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
#pragma warning disable CA1031 // A subscriber's failure must not stop recovery or reach whoever reported a failure.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSubscriberFailed(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A storage health subscriber failed")]
    private static partial void LogSubscriberFailed(ILogger logger, Exception exception);

    private void EnsureLoop()
    {
        if (_loopRunning || _stopped)
        {
            return;
        }

        _loopRunning = true;
        _loop = Task.Run(RecoverAsync);
    }

    private async Task RecoverAsync()
    {
        while (true)
        {
            long epoch;
            lock (_lock)
            {
                if (!_blocked || _stopped)
                {
                    _loopRunning = false;
                    return;
                }

                epoch = _epoch;
            }

            try
            {
                await Task.Delay(_options.RetryInterval, _stop.Token).ConfigureAwait(false);
                lock (_lock)
                {
                    // Failures between the previous pass and this delay belong to this pass.
                    epoch = _epoch;
                }

                foreach (var retry in Retries())
                {
                    await RunRetryAsync(retry, _stop.Token).ConfigureAwait(false);
                }

                await _probe.ProbeWriteAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                lock (_lock)
                {
                    _loopRunning = false;
                }

                return;
            }
#pragma warning disable CA1031 // Still failing: stay blocked and try again after the interval.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Counted, not logged: every interval would write the same exception again (low finding observability-6).
                lock (_lock)
                {
                    _failedChecks++;
                }

                continue;
            }

            bool cleared;
            (long Seconds, int LaterFailures, int FailedChecks) counts = default;
            lock (_lock)
            {
                cleared = !_stopped && _epoch == epoch && _outstanding == 0 && _blocked;
                if (cleared)
                {
                    _blocked = false;
                    counts = ((long)(DateTimeOffset.UtcNow - _pausedAt).TotalSeconds, _laterFailures, _failedChecks);
                }
            }

            if (cleared)
            {
                LogRecovered(_logger, counts.Seconds, counts.LaterFailures, counts.FailedChecks);
                RaiseChanged();
            }
        }
    }

    private Func<CancellationToken, Task>[] Retries()
    {
        lock (_lock)
        {
            return [.. _retries];
        }
    }

    private async Task RunRetryAsync(Func<CancellationToken, Task> retry, CancellationToken cancellationToken)
    {
        try
        {
            await retry(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // An owner's retry reports its own storage failures; anything else is logged and the pass goes on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRetryFailed(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Core write failed; new work is paused until storage recovers")]
    private static partial void LogFailure(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closing while storage is still paused, after {Seconds} s: {LaterFailures} more failed writes, {FailedChecks} failed checks")]
    private static partial void LogStillPaused(ILogger logger, long seconds, int laterFailures, int failedChecks);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retrying kept writes failed unexpectedly")]
    private static partial void LogRetryFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Storage takes writes again after {Seconds} s; new work resumes. During the pause: {LaterFailures} more failed writes, {FailedChecks} failed checks")]
    private static partial void LogRecovered(ILogger logger, long seconds, int laterFailures, int failedChecks);
}
