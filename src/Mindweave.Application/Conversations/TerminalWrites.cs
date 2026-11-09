using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Storage;

namespace Mindweave.Application.Conversations;

internal enum SaveResult
{
    Applied,
    NotApplied,
    Failed,
}

/// <summary>
/// The coordinator's core writes and the terminal writes it keeps when they fail: storage-refused ones hold a storage
/// obligation and are retried when storage recovers; defects are kept for the exit to report.
/// </summary>
internal sealed partial class TerminalWrites : IDisposable
{
    private readonly StorageHealth _health;
    private readonly ILogger _logger;

    // Terminal writes that storage refused, each with its obligation: storage does not count as recovered until they are saved.
    private readonly ConcurrentQueue<KeptWrite> _unsaved = new();

    // Terminal writes that failed for a reason other than storage (a defect): kept so the exit reports them unsaved, but
    // they neither pause requests nor hold up recovery, which only storage itself can bring about.
    private readonly ConcurrentQueue<KeptWrite> _unsavedDefects = new();

    // One pass over the failed terminal writes at a time: the recovery retry and an exit's final save must not
    // overlap, or the exit could see an empty queue while a dequeued write is still failing.
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    public TerminalWrites(StorageHealth health, ILogger logger)
    {
        _health = health;
        _logger = logger;
        health.RegisterRetry(_ => FlushAsync());
    }

    /// <summary>
    /// Runs one core write. A storage failure blocks new requests until storage recovers (see <see cref="StorageHealth"/>);
    /// terminal writes are kept and retried in the background so a finished answer is not lost while the app runs. Any
    /// other failure is a defect: the write is kept for the exit to report, and requests are not paused.
    /// </summary>
    public async Task<SaveResult> SaveAsync(Func<Task<bool>> write, bool retryOnFailure = false)
    {
        try
        {
            var applied = await write().ConfigureAwait(false);
            return applied ? SaveResult.Applied : SaveResult.NotApplied;
        }
        catch (StorageWriteException ex)
        {
            if (retryOnFailure)
            {
                _unsaved.Enqueue(new KeptWrite(write, _health.Retain(ex)));
            }
            else
            {
                _health.Report(ex);
            }

            return SaveResult.Failed;
        }
#pragma warning disable CA1031 // A defect in one write must not take down the run; the content stays in memory and is reported.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogWriteDefect(_logger, ex);
            if (retryOnFailure)
            {
                _unsavedDefects.Enqueue(new KeptWrite(write, null));
            }

            return SaveResult.Failed;
        }
    }

    /// <summary>
    /// One pass over the failed terminal writes kept for storage. True when none is left unsaved at the end of this pass.
    /// With <paramref name="includeDefects"/> (the exit's save) the writes that failed for other reasons are tried too
    /// and count as unsaved; recovery leaves them alone, as storage coming back does not fix them.
    /// </summary>
    public async Task<bool> FlushAsync(bool includeDefects = false)
    {
        await _flushGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await RetryAsync(_unsaved).ConfigureAwait(false);
            if (includeDefects)
            {
                await RetryAsync(_unsavedDefects).ConfigureAwait(false);
            }

            // Read while no other pass can have a write out of the queue.
            return _unsaved.IsEmpty && (!includeDefects || _unsavedDefects.IsEmpty);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task RetryAsync(ConcurrentQueue<KeptWrite> queue)
    {
        var count = queue.Count;
        for (var i = 0; i < count && queue.TryDequeue(out var kept); i++)
        {
            // While it is out of the queue its obligation stays open, so recovery cannot be declared over it.
            try
            {
                await kept.Write().ConfigureAwait(false);
                kept.Obligation?.Resolve();
            }
            catch (StorageWriteException ex)
            {
                if (kept.Obligation is { IsResolved: false })
                {
                    _health.Report(ex);
                    _unsaved.Enqueue(kept);
                }
                else
                {
                    _unsaved.Enqueue(kept with { Obligation = _health.Retain(ex) });
                }
            }
#pragma warning disable CA1031 // Still failing: keep it for the next attempt.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // Storage is no longer what stops it; the content stays kept, now as a defect the exit reports.
                LogWriteDefect(_logger, ex);
                kept.Obligation?.Resolve();
                _unsavedDefects.Enqueue(kept with { Obligation = null });
            }
        }
    }

    /// <summary>A terminal write kept after it failed, with the storage obligation it holds open (none for a defect).</summary>
    private sealed record KeptWrite(Func<Task<bool>> Write, StorageObligation? Obligation);

    public void Dispose() => _flushGate.Dispose();

    [LoggerMessage(Level = LogLevel.Error, Message = "A core write failed for a reason other than storage; its content is kept in memory and reported at exit")]
    private static partial void LogWriteDefect(ILogger logger, Exception exception);
}
