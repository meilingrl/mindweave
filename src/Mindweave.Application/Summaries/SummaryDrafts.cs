using Microsoft.Extensions.Logging;
using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.Summaries;

/// <summary>What an edit session opened on: a draft to continue, a draft whose baseline is no longer current, or none.</summary>
public abstract record DraftRead(long Generation, long Revision)
{
    public sealed record None(long Generation, long Revision) : DraftRead(Generation, Revision);

    public sealed record Draft(long Generation, long Revision, SummaryVersionId Baseline, string Title, string Body) : DraftRead(Generation, Revision);

    /// <summary>Kept input whose baseline is no longer current: shown, never dropped or silently moved onto the new version.</summary>
    public sealed record Conflict(long Generation, long Revision, SummaryVersionId Baseline, string Title, string Body) : DraftRead(Generation, Revision);
}

/// <summary>
/// Owner of summary edit drafts (architecture §2: "总结编辑草稿 · 总结用例 · 独立自动保存"; review F2). One draft per node,
/// as stored. The newest input lives here, not in a page, so a page reopened after its draft failed to save shows that
/// input, and no page decides which leftover wins. An edit session is bound to the node's generation (bumped by every
/// commit, discard and version operation, and when the node is found deleted) and to the revision it last set, so a stale page can neither overwrite nor commit or discard
/// newer input. Writes are coalesced and trail by <see cref="SaveDelay"/>.
/// </summary>
/// <remarks>
/// Lock order: a node's gate (held across that node's draft write, commit, discard or version operation), then the
/// record lock (field updates only, never awaited under), then the storage-health lock. Never two node gates at once;
/// change notifications and naming run after the gate is released.
/// </remarks>
public sealed partial class SummaryDrafts : IDisposable
{
    private readonly ISummaryStore _store;
    private readonly IClock _clock;
    private readonly StorageHealth _health;
    private readonly ILogger<SummaryDrafts> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<SummaryNodeId, Record> _records = [];

    public SummaryDrafts(ISummaryStore store, IClock clock, StorageHealth health, ILogger<SummaryDrafts> logger, TimeSpan? saveDelay = null)
    {
        _store = store;
        _clock = clock;
        _health = health;
        _logger = logger;
        SaveDelay = saveDelay ?? TimeSpan.FromSeconds(1);
        health.RegisterRetry(_ => FlushAsync());
    }

    public TimeSpan SaveDelay { get; }

    private enum DraftStatus
    {
        /// <summary>No draft in memory (one may be stored, not yet read).</summary>
        None,

        /// <summary>Memory matches the stored draft.</summary>
        Clean,

        /// <summary>Newer than what is stored; a write is scheduled.</summary>
        Dirty,

        /// <summary>Storage refused the newest write; an obligation is open.</summary>
        Failed,

        /// <summary>Its baseline is no longer current: kept in memory, cannot be stored as a draft or saved as a version.</summary>
        Conflict,

        /// <summary>Discarded by the user; the stored row is not yet deleted. Suppresses stale reads and writes.</summary>
        Deleting,
    }

    /// <summary>
    /// Opens an edit of the node's <paramref name="current"/> version: memory first; otherwise the stored draft, unless a
    /// change in memory overtook the read, in which case memory answers.
    /// </summary>
    public async Task<DraftRead> OpenAsync(SummaryNodeId node, SummaryVersionId current, CancellationToken cancellationToken)
    {
        long generation;
        long revision;
        lock (_lock)
        {
            var record = RecordOf(node);
            if (record.Status != DraftStatus.None)
            {
                return ReadOf(record, current);
            }

            generation = record.Generation;
            revision = record.Revision;
        }

        var stored = (await _store.GetAsync(node, cancellationToken).ConfigureAwait(false))?.Draft;
        lock (_lock)
        {
            var record = RecordOf(node);
            if (record.Generation != generation || record.Revision != revision || record.Status != DraftStatus.None)
            {
                return ReadOf(record, current);
            }

            if (stored is not null)
            {
                record.Content = new DraftContent(stored.Baseline, stored.Title, stored.Body);
                record.Status = stored.Baseline == current ? DraftStatus.Clean : DraftStatus.Conflict;
                record.SavedRevision = record.Revision;
            }

            return ReadOf(record, current);
        }
    }

    /// <summary>
    /// The newest input of an edit session; written after <see cref="SaveDelay"/>. Null when the session is stale (the
    /// draft was committed or discarded since it opened): nothing changes, and the page keeps its text.
    /// </summary>
    public long? Set(SummaryNodeId node, long generation, SummaryVersionId baseline, string title, string body)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        bool schedule;
        long revision;
        lock (_lock)
        {
            var record = RecordOf(node);
            if (record.Generation != generation)
            {
                return null;
            }

            record.Revision++;
            record.Content = new DraftContent(baseline, title, body);
            if (record.Status is not DraftStatus.Failed)
            {
                record.Status = DraftStatus.Dirty;
            }

            revision = record.Revision;
            schedule = !record.Scheduled;
            record.Scheduled = true;
        }

        if (schedule)
        {
            _ = Task.Delay(SaveDelay).ContinueWith(_ => WriteAsync(node), TaskScheduler.Default).Unwrap();
        }

        return revision;
    }

    /// <summary>
    /// Writes every draft newer than what is stored, and retries pending deletes. True when nothing is left unsaved;
    /// a conflicted draft cannot be stored and counts as unsaved (the exit then keeps the window open).
    /// </summary>
    public async Task<bool> FlushAsync()
    {
        SummaryNodeId[] nodes;
        lock (_lock)
        {
            nodes = [.. _records.Where(r => r.Value.Status is DraftStatus.Dirty or DraftStatus.Failed or DraftStatus.Deleting).Select(r => r.Key)];
        }

        var saved = true;
        foreach (var node in nodes)
        {
            saved &= await WriteAsync(node).ConfigureAwait(false);
        }

        // An edit that already could not be saved goes with its summary if that was deleted since (review 2026-11-15):
        // deleting a conversation removes the stored rows without telling this class, and no page is left to discard it.
        SummaryNodeId[] conflicted;
        lock (_lock)
        {
            conflicted = [.. _records.Where(r => r.Value.Status == DraftStatus.Conflict).Select(r => r.Key)];
        }

        foreach (var node in conflicted)
        {
            await DropIfGoneAsync(node).ConfigureAwait(false);
        }

        lock (_lock)
        {
            return saved && !_records.Values.Any(r => r.Status is DraftStatus.Dirty or DraftStatus.Failed or DraftStatus.Deleting or DraftStatus.Conflict);
        }
    }

    /// <summary>
    /// The nodes whose input can be neither stored nor saved as a version (its baseline stopped being current while it
    /// was typed): only a discard on its page clears it. The exit names them (low finding gap-summary-3).
    /// </summary>
    public IReadOnlyList<SummaryNodeId> Unsaveable()
    {
        lock (_lock)
        {
            return [.. _records.Where(r => r.Value.Status == DraftStatus.Conflict).Select(r => r.Key)];
        }
    }

    /// <summary>
    /// Saves the session's draft as a version through <paramref name="saveEdit"/> (which removes the stored draft in the
    /// same transaction). Refused with <see cref="SummaryRefusal.Changed"/> when the session is stale or another page set
    /// newer input. If newer input arrives while the version is being written, it is kept as a conflict, not dropped.
    /// </summary>
    public async Task<T> CommitAsync<T>(SummaryNodeId node, long generation, long revision, Func<Task<T>> saveEdit)
    {
        ArgumentNullException.ThrowIfNull(saveEdit);
        var gate = GateOf(node);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                var record = RecordOf(node);
                if (record.Generation != generation || record.Revision != revision)
                {
                    throw new SummaryRefusedException(SummaryRefusal.Changed);
                }
            }

            var result = await saveEdit().ConfigureAwait(false);
            StorageObligation? resolved;
            lock (_lock)
            {
                var record = RecordOf(node);
                record.Generation++;
                resolved = record.Obligation;
                record.Obligation = null;
                if (record.Revision == revision)
                {
                    record.Content = null;
                    record.Status = DraftStatus.None;
                }
                else
                {
                    // Typed meanwhile on another page: its text is based on the version just replaced.
                    record.Status = DraftStatus.Conflict;
                }
            }

            resolved?.Resolve();
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Discards the session's draft. Refused with <see cref="SummaryRefusal.Changed"/> for a stale session or newer
    /// input from another page. A delete storage refuses is kept and retried; the discarded text is not kept, by the
    /// user's choice, and a stale stored row never comes back meanwhile.
    /// </summary>
    public async Task DiscardAsync(SummaryNodeId node, long generation, long revision)
    {
        var gate = GateOf(node);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                var record = RecordOf(node);
                if (record.Generation != generation || record.Revision != revision)
                {
                    throw new SummaryRefusedException(SummaryRefusal.Changed);
                }

                record.Generation++;
                record.Content = null;
                record.Status = DraftStatus.Deleting;
            }

            await DeleteAsync(node).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Runs a version operation that must not pass an unsaved edit (adopt, set current, delete versions). Refused with
    /// <see cref="SummaryRefusal.HasDraft"/> while unsaved input exists in memory; a stored draft is still refused by the
    /// store's own check. A pending discard is completed first.
    /// </summary>
    public async Task<T> RunVersionOperationAsync<T>(SummaryNodeId node, Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var gate = GateOf(node);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool deleting;
            lock (_lock)
            {
                var record = RecordOf(node);
                if (record.Status is DraftStatus.Dirty or DraftStatus.Failed or DraftStatus.Conflict)
                {
                    throw new SummaryRefusedException(SummaryRefusal.HasDraft);
                }

                deleting = record.Status == DraftStatus.Deleting;
            }

            if (deleting && !await DeleteAsync(node).ConfigureAwait(false))
            {
                _health.ThrowIfBlocked();
            }

            var result = await operation().ConfigureAwait(false);

            // The current version may have changed: an edit opened before it, with nothing typed yet, is now stale, so its
            // next input is refused at once instead of becoming a conflict that cannot be saved (low finding gap-summary-3).
            lock (_lock)
            {
                RecordOf(node).Generation++;
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var record in _records.Values)
            {
                record.Gate.Dispose();
            }
        }
    }

    private static DraftRead ReadOf(Record record, SummaryVersionId current) => record.Content is { } content && record.Status != DraftStatus.Deleting
        ? record.Status == DraftStatus.Conflict || content.Baseline != current
            ? new DraftRead.Conflict(record.Generation, record.Revision, content.Baseline, content.Title, content.Body)
            : new DraftRead.Draft(record.Generation, record.Revision, content.Baseline, content.Title, content.Body)
        : new DraftRead.None(record.Generation, record.Revision);

    /// <summary>Drops a conflicted draft whose node no longer exists.</summary>
    private async Task DropIfGoneAsync(SummaryNodeId node)
    {
        var gate = GateOf(node);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!await IsGoneAsync(node).ConfigureAwait(false))
            {
                return;
            }

            lock (_lock)
            {
                var record = RecordOf(node);
                if (record.Status == DraftStatus.Conflict)
                {
                    record.Generation++;
                    record.Content = null;
                    record.Status = DraftStatus.None;
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The node no longer exists. A failed read answers no: the draft is then kept.</summary>
    private async Task<bool> IsGoneAsync(SummaryNodeId node)
    {
        try
        {
            return await _store.GetAsync(node, CancellationToken.None).ConfigureAwait(false) is null;
        }
#pragma warning disable CA1031 // Unknown means kept: dropping input on a read error would lose it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogWriteFailed(_logger, ex, node);
            return false;
        }
    }

    private Record RecordOf(SummaryNodeId node)
    {
        if (!_records.TryGetValue(node, out var record))
        {
            record = new Record();
            _records[node] = record;
        }

        return record;
    }

    private SemaphoreSlim GateOf(SummaryNodeId node)
    {
        lock (_lock)
        {
            return RecordOf(node).Gate;
        }
    }

    /// <summary>One write of the node's newest draft (or its pending delete) under its gate. True when nothing is left unsaved.</summary>
    private async Task<bool> WriteAsync(SummaryNodeId node)
    {
        var gate = GateOf(node);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            long revision;
            DraftContent content;
            lock (_lock)
            {
                var record = RecordOf(node);
                record.Scheduled = false;
                if (record.Status == DraftStatus.Deleting)
                {
                    content = null!;
                    revision = -1;
                }
                else if (record.Status is DraftStatus.Dirty or DraftStatus.Failed && record.Content is { } newest)
                {
                    content = newest;
                    revision = record.Revision;
                }
                else
                {
                    return record.Status != DraftStatus.Conflict;
                }
            }

            if (revision < 0)
            {
                return await DeleteAsync(node).ConfigureAwait(false);
            }

            try
            {
                await _store.SaveDraftAsync(new SummaryEditDraft(node, content.Baseline, content.Title, content.Body, _clock.UtcNow), CancellationToken.None).ConfigureAwait(false);
            }
            catch (StorageWriteException ex)
            {
                Failed(node, ex);
                return false;
            }
            catch (SummaryRefusedException)
            {
                // The summary itself is gone (its conversation was deleted before this was written): the draft goes with
                // it. Kept, it could never be written and no page is left to discard it, so the exit would wait for good
                // (low finding gap-summary-3).
                var gone = await IsGoneAsync(node).ConfigureAwait(false);

                // Otherwise the baseline is gone (another version became current): a domain conflict, not a storage failure.
                StorageObligation? resolved;
                lock (_lock)
                {
                    var record = RecordOf(node);
                    resolved = record.Obligation;
                    record.Obligation = null;
                    if (gone)
                    {
                        record.Generation++;
                        record.Content = null;
                        record.Status = DraftStatus.None;
                    }
                    else
                    {
                        record.Status = DraftStatus.Conflict;
                    }
                }

                resolved?.Resolve();
                return gone;
            }
#pragma warning disable CA1031 // Any other failure is a defect: logged, and the input stays in memory, pending.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogWriteFailed(_logger, ex, node);
                return false;
            }

            StorageObligation? done = null;
            lock (_lock)
            {
                var record = RecordOf(node);
                record.SavedRevision = Math.Max(record.SavedRevision, revision);
                if (record.Revision == revision)
                {
                    record.Status = DraftStatus.Clean;
                }
                else if (record.Status == DraftStatus.Failed)
                {
                    // Newer input arrived during the write: it is ordinary pending input now, not a failure.
                    record.Status = DraftStatus.Dirty;
                }

                if (record.Obligation is not null && revision >= record.FailedRevision)
                {
                    done = record.Obligation;
                    record.Obligation = null;
                }
            }

            done?.Resolve();
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Deletes the stored draft of a discarded node; called under the node's gate.</summary>
    private async Task<bool> DeleteAsync(SummaryNodeId node)
    {
        try
        {
            await _store.DeleteDraftAsync(node, CancellationToken.None).ConfigureAwait(false);
        }
        catch (StorageWriteException ex)
        {
            Failed(node, ex);
            return false;
        }

        StorageObligation? resolved;
        lock (_lock)
        {
            var record = RecordOf(node);
            if (record.Status == DraftStatus.Deleting)
            {
                record.Status = DraftStatus.None;
            }

            resolved = record.Obligation;
            record.Obligation = null;
        }

        resolved?.Resolve();
        return true;
    }

    private void Failed(SummaryNodeId node, StorageWriteException failure)
    {
        lock (_lock)
        {
            var record = RecordOf(node);
            if (record.Status != DraftStatus.Deleting)
            {
                record.Status = DraftStatus.Failed;
            }

            record.FailedRevision = record.Revision;
            if (record.Obligation is null)
            {
                record.Obligation = _health.Retain(failure);
                return;
            }
        }

        _health.Report(failure);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Summary draft of {Node} could not be saved for a reason other than storage; it stays in memory")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception, SummaryNodeId node);

    private sealed record DraftContent(SummaryVersionId Baseline, string Title, string Body);

    private sealed class Record
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public long Generation { get; set; }

        public long Revision { get; set; }

        public DraftContent? Content { get; set; }

        public long SavedRevision { get; set; }

        public long FailedRevision { get; set; }

        public DraftStatus Status { get; set; }

        public StorageObligation? Obligation { get; set; }

        public bool Scheduled { get; set; }
    }
}
