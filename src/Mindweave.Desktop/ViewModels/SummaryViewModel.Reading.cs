using System.Collections.Immutable;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.ViewModels;

public sealed partial class SummaryViewModel
{
    private readonly SemaphoreSlim readWrites = new(1, 1);
    private (Guid Id, SummaryReadObservation Observation, SummaryReadLocation? Location)? pendingRead;
    private (Guid Id, long Epoch, long Revision, bool Value)? pendingManual;
    private bool readingInterrupted;
    private long readingInterruptionRevision;
    // Lifecycle edges must survive an outstanding store await; no I/O or observation lock here.
    public void InterruptReading() { readingInterruptionRevision++; readingInterrupted = true; }
    public SummaryReadingSnapshot? Reading { get; private set; }
    public async Task<bool> BeginReadingAsync(Guid id, string text)
    {
        var interruptionAtStart = readingInterruptionRevision;
        await readWrites.WaitAsync();
        try
        {
            if (blocked() || !CanRead || Version?.Header.Identity.Id != id) return false;
            await FlushReadingCoreAsync();
            var stored = await store.LoadSummaryReadingAsync(id);
            var result = await store.BeginSummaryReadingAsync(id, stored?.State.Revision ?? 0, text);
            if (Version?.Header.Identity.Id != id || !CanRead) return false;
            if (result.Kind is not (SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange)) return false;
            Reading = result.Value; readingInterrupted = readingInterruptionRevision != interruptionAtStart; RefreshAvailability(); return true;
        }
        catch { ErrorMessage = "阅读状态保存失败，请重试保存。"; failed(); return false; }
        finally { readWrites.Release(); }
    }
    public async Task ObserveReadingAsync(Guid id, long epoch, TimeSpan time, bool active, bool visible, bool fits,
        ImmutableArray<ReadableInterval> intervals, SummaryReadLocation? location, bool captureOnly = false)
    {
        var sampledInterruptionRevision = readingInterruptionRevision;
        await readWrites.WaitAsync();
        try
        {
            if (Version?.Header.Identity.Id != id || Reading?.State.Epoch != epoch) return;
            if (blocked() && !captureOnly) { InterruptReading(); return; }
            await FlushReadingCoreAsync();
            if (readingInterrupted)
            {
                var resetRevision = readingInterruptionRevision;
                var interrupted = Reading.State;
                pendingRead = (id, new(interrupted.Epoch, interrupted.Revision, interrupted.ObservationSequence + 1, time,
                    false, false, false, interrupted.TextHash, []), null);
                await FlushReadingCoreAsync();
                if (resetRevision == readingInterruptionRevision) readingInterrupted = false;
            }
            var evidenceStillCurrent = sampledInterruptionRevision == readingInterruptionRevision && !readingInterrupted;
            var state = Reading.State;
            pendingRead = (id, new(state.Epoch, state.Revision, state.ObservationSequence + 1, time,
                active && CanRead && evidenceStillCurrent, visible && CanRead && evidenceStillCurrent, fits, state.TextHash, evidenceStillCurrent ? intervals : []), location);
            await FlushReadingCoreAsync();
        }
        catch { InterruptReading(); ErrorMessage = "阅读位置保存失败，位置仍在内存，请重试保存。"; failed(); }
        finally { readWrites.Release(); }
    }
    private async Task SetReadAsync(bool value)
    {
        await readWrites.WaitAsync();
        try
        {
            if (Version is null || Reading is null || blocked()) return;
            await FlushReadingCoreAsync();
            pendingManual = (Version.Header.Identity.Id, Reading.State.Epoch, Reading.State.Revision, value);
            await FlushReadingCoreAsync();
        }
        catch { ErrorMessage = "已读状态保存失败，请重试保存。"; failed(); }
        finally { readWrites.Release(); }
    }
    public async Task FlushReadingAsync()
    {
        await readWrites.WaitAsync(); try { await FlushReadingCoreAsync(); } finally { readWrites.Release(); }
    }
    private async Task FlushReadingCoreAsync()
    {
        if (pendingRead is { } pending)
        {
            var result = await store.SaveSummaryReadingAsync(pending.Id, pending.Observation, pending.Location);
            if (result.Kind is SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange)
            { if (Version?.Header.Identity.Id == pending.Id) Reading = result.Value; }
            else if (result.Kind == SummaryOutcomeKind.Obsolete)
            { if (Version?.Header.Identity.Id == pending.Id) Reading = await store.LoadSummaryReadingAsync(pending.Id); }
            else if (result.Kind != SummaryOutcomeKind.NotFound) throw new InvalidOperationException("阅读状态冲突。");
            pendingRead = null;
        }
        if (pendingManual is { } manual)
        {
            var result = await store.SetSummaryReadAsync(manual.Id, manual.Epoch, manual.Revision, manual.Value);
            if (result.Kind is SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange)
            { if (Version?.Header.Identity.Id == manual.Id) Reading = result.Value; }
            else
            {
                var actual = await store.LoadSummaryReadingAsync(manual.Id);
                if (actual is not null && (actual.State.Epoch != manual.Epoch || actual.State.IsRead != manual.Value))
                    throw new InvalidOperationException("已读状态变化，请重新打开版本。");
                if (Version?.Header.Identity.Id == manual.Id) Reading = actual;
            }
            pendingManual = null;
        }
        RefreshAvailability();
    }
}
