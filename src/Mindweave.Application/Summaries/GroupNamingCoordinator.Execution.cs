using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;
namespace Mindweave.Application.Summaries;

public sealed partial class GroupNamingCoordinator
{
    private async Task ExecuteAsync(Job job)
    {
        try
        {
            await control.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (sync) if (job.Stopped || !Active(job.Record.Status) || Runtime.IsAdmissionPaused) return;
                await PrepareAsync(job).ConfigureAwait(false);
            }
            catch { job.PreparationFailed = true; failure.Report(); return; }
            finally { control.Release(); }
            lock (sync) if (job.Prepared is null || job.Stopped || !Active(job.Record.Status)) return;
            string secret;
            try { secret = await vault.ReadAsync(job.Prepared.Request.Configuration.CredentialReference).ConfigureAwait(false); }
            catch { await FailAsync(job, "CredentialUnavailable").ConfigureAwait(false); return; }
            System.Collections.Immutable.ImmutableArray<NodeNameProposal>? names = null;
            try
            {
                await job.Demand!.RunPhaseAsync(async () =>
                {
                    lock (sync) { if (job.Stopped || !Active(job.Record.Status) || Runtime.IsAdmissionPaused) return; }
                    var buffer = new KnowledgeSummaryPayloadBuffer(); string? finish = null;
                    await foreach (var item in transport.StreamAsync(job.Prepared.Request, secret, job.Cancellation.Token).ConfigureAwait(false))
                    {
                        lock (sync) if (job.Stopped) continue;
                        if (item.Text is { } text) buffer.Append(text);
                        if (item.FinishReason is { } reason) { if (finish is not null && finish != reason) throw new SummaryProtocolException("命名终止标志冲突。"); finish = reason; }
                    }
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    if (finish != "stop") throw new SummaryProtocolException("命名未完整结束。");
                    names = NodeNamingProtocol.ParseComplete(buffer.Complete(), job.Record.Snapshot);
                }, job.Cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested) { return; }
            catch (Exception error) { await FailAsync(job, error is SummaryProtocolException ? "InvalidNamingResponse" : "NamingTransportFailed").ConfigureAwait(false); return; }
            lock (sync) { if (names is null || job.Stopped || !Active(job.Record.Status)) return; job.Completion = new(job.Record.Id, job.Record.Revision, job.Record.Snapshot.SnapshotHash, names.Value); job.Demand?.Dispose(); }
            await FlushAsync(false).ConfigureAwait(false);
        }
        catch {/* Durable failure source retains exact work for explicit repair. */}
        finally
        {
            lock (sync) { job.Busy = false; if (!Active(job.Record.Status) || job.Completion is not null) job.Demand?.Dispose(); }
            try { await FinishGroupAsync(job).ConfigureAwait(false); } catch { failure.Report(); }
            Notify();
        }
    }
    private async Task PrepareAsync(Job job)
    {
        // Rebuild on every dispatch, including gate-closed retries. Existing DisplayName is part of request identity.
        var saved = await store.LoadNodeNamingJobAsync(job.Record.Id).ConfigureAwait(false);
        if (saved is null) { await ReconcileAsync(job, "NamingTaskMissing").ConfigureAwait(false); return; }
        lock (sync) if (!job.Dirty) job.Record = saved;
        var result = await store.PrepareNodeNamingRequestAsync(job.Record.Id, job.Record.Revision).ConfigureAwait(false);
        if (!Success(result.Kind) || result.Value is null) { await ReconcileAsync(job, result.Code ?? "NamingPreparationRejected").ConfigureAwait(false); return; }
        lock (sync) { job.Prepared = result.Value; job.Record = job.Record with { Revision = result.Value.Revision }; job.PreparationFailed = false; }
    }
    private async Task FailAsync(Job job, string error)
    {
        lock (sync) { if (job.Stopped || !Active(job.Record.Status)) return; job.Record = job.Record with { Status = GenerationStatus.Failed, ErrorCode = error }; job.Dirty = true; job.Demand?.Dispose(); }
        await FlushAsync(false).ConfigureAwait(false);
    }
    private async Task FinishGroupAsync(Job job)
    {
        await control.WaitAsync().ConfigureAwait(false);
        try
        {
            Group group = job.Group; Pending? pending;
            lock (sync)
            {
                if (group.Active != job || Active(job.Record.Status) || job.Dirty || job.Completion is not null || job.Busy) return;
                group.Active = null; pending = group.Pending;
            }
            if (pending is not null)
            {
                if (group.FollowupUsed || pending.Cancelled || !accepting)
                {
                    pending.Cancelled = true;
                    await store.ClearNodeNamingIntentAsync(pending.Intent.Snapshot.WorkspaceId, pending.Intent.Snapshot.GroupKey, pending.Intent.OperationId).ConfigureAwait(false);
                    lock (sync) group.Pending = null;
                }
                else { group.FollowupUsed = true; await AcceptPendingAsync(group).ConfigureAwait(false); }
            }
            lock (sync) if (group.Active is null && group.Pending is null) groups.Remove((job.Record.Snapshot.WorkspaceId, job.Record.Snapshot.GroupKey));
        }
        finally { control.Release(); }
    }
}
