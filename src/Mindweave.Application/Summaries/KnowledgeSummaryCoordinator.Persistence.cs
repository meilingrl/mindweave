using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.Summaries;

public sealed partial class KnowledgeSummaryCoordinator
{
    public async Task RepairPersistenceAsync() => await FlushAsync(explicitRepair: true).ConfigureAwait(false);

    private async Task FlushAsync(bool explicitRepair)
    {
        await persistence.WaitAsync().ConfigureAwait(false);
        var epoch = failure.Epoch;
        List<KnowledgeSummaryCommitted> notifications = [];
        try
        {
            if (failure.HasFailure && !explicitRepair) throw new InvalidOperationException("总结保存失败，请重试保存。");
            AcceptanceIntent[] unknown;
            lock (sync) unknown = unknownAcceptances.Values.ToArray();
            foreach (var intent in unknown)
            {
                // A missing operation proves no accepted task. Never retry acceptance against newer source.
                var record = await store.FindKnowledgeSummaryOperationAsync(intent.OperationId).ConfigureAwait(false);
                if (record is not null && (record.ConversationId != intent.ConversationId || record.InputPlan.Configuration != intent.Configuration))
                    throw new InvalidDataException("总结操作身份不一致。");
                lock (sync)
                {
                    if (record is not null)
                    {
                        var job = AddJob(record);
                        if (!accepting) { Stop(job); Cancel(job); }
                    }
                    unknownAcceptances.Remove(intent.OperationId);
                }
            }
            if (explicitRepair)
            {
                bool verifyConfiguration;
                lock (sync) verifyConfiguration = configurationReadFailed;
                if (verifyConfiguration)
                {
                    _ = await chatStore.LoadActiveConfigurationAsync().ConfigureAwait(false);
                    lock (sync) configurationReadFailed = false;
                }
                Job[] preparation;
                lock (sync) preparation = jobs.Where(j => j.NeedsPreparationRepair && Active(j.Status) && !j.StopRequested).ToArray();
                foreach (var job in preparation) await PrepareAsync(job).ConfigureAwait(false);
            }
            Job[] pending;
            lock (sync) pending = jobs.Where(j => j.Dirty || j.Completion is not null).ToArray();
            foreach (var job in pending)
            {
                SummaryGenerationCompletion? completion;
                lock (sync) completion = job.Completion;
                if (completion is not null)
                {
                    var result = await store.CompleteKnowledgeSummaryAsync(completion).ConfigureAwait(false);
                    if (Success(result.Kind) && result.Value is { } committed)
                    {
                        lock (sync)
                        {
                            job.Record = job.Record with { Status = GenerationStatus.Completed, Revision = checked(completion.ExpectedRevision + 1),
                                OutputVersionId = committed.VersionId, ActualModelId = completion.ActualModelId, ErrorCode = null };
                            job.Status = GenerationStatus.Completed; job.Error = null; job.Dirty = false; job.Completion = null; job.Result = committed;
                            job.Demand?.Dispose();
                            if (!job.Published) { job.Published = true; notifications.Add(new(job.Record.WorkspaceId, job.Record.ConversationId, committed)); }
                        }
                    }
                    else await ReconcileOutcomeAsync(job, result.Code ?? "SummaryCompletionRejected").ConfigureAwait(false);
                }
                lock (sync) if (!job.Dirty) continue;
                await PersistTerminalAsync(job).ConfigureAwait(false);
            }
            if (explicitRepair) failure.TryClear(epoch);
        }
        catch { failure.Report(); throw; }
        finally
        {
            persistence.Release();
            foreach (var notification in notifications) Publish(notification);
            Notify();
        }
    }

    private async Task PersistTerminalAsync(Job job)
    {
        SummaryGenerationMetadata metadata;
        lock (sync) metadata = new(job.Record.Id, job.Record.Revision, job.Status, job.Error);
        var result = await store.UpdateKnowledgeSummaryMetadataAsync(metadata).ConfigureAwait(false);
        if (Success(result.Kind) && result.Value is { } current)
        {
            lock (sync) { job.Record = current; job.Status = current.Status; job.Error = current.ErrorCode; job.Dirty = false; }
            return;
        }
        // Expected CAS conflict is business state, not an IO failure. Read the authoritative winner.
        var saved = await store.LoadKnowledgeSummaryJobAsync(job.Record.Id).ConfigureAwait(false);
        lock (sync)
        {
            if (saved is null) { job.Status = GenerationStatus.Failed; job.Error = "SummaryTaskMissing"; job.Dirty = false; return; }
            job.Record = saved;
            if (!Active(saved.Status)) { job.Status = saved.Status; job.Error = saved.ErrorCode; job.Dirty = false; job.Completion = null; }
            else { job.Status = GenerationStatus.Failed; job.Error = result.Code ?? "SummaryMetadataRejected"; }
        }
        // An active row with a newer revision can be a lost acknowledgement. Use its current revision once.
        if (Active(saved.Status))
        {
            var retry = await store.UpdateKnowledgeSummaryMetadataAsync(new(saved.Id, saved.Revision, job.Status, job.Error)).ConfigureAwait(false);
            if (Success(retry.Kind) && retry.Value is { } terminal)
                lock (sync) { job.Record = terminal; job.Status = terminal.Status; job.Error = terminal.ErrorCode; job.Dirty = false; }
            else throw new InvalidDataException("总结任务终态无法核实。");
        }
    }

    // Called only with the persistence gate held. Typed rejection remains a typed task failure;
    // thrown read/source corruption is retained behind the persistence failure gate.
    private async Task ReconcileOutcomeAsync(Job job, string code)
    {
        var current = await store.LoadKnowledgeSummaryJobAsync(job.Record.Id).ConfigureAwait(false);
        lock (sync)
        {
            job.Completion = null;
            if (current is null) { job.Status = GenerationStatus.Failed; job.Error = "SummaryTaskMissing"; job.Dirty = false; }
            else
            {
                job.Record = current;
                job.Status = Active(current.Status) ? GenerationStatus.Failed : current.Status;
                job.Error = Active(current.Status) ? code : current.ErrorCode;
                job.Dirty = Active(current.Status);
            }
            job.Demand?.Dispose();
        }
        if (job.Dirty) await PersistTerminalAsync(job).ConfigureAwait(false);
    }

    public async Task ShutdownAsync()
    {
        await acceptance.WaitAsync().ConfigureAwait(false);
        Task[] tails; Job[] snapshot;
        try
        {
            lock (sync)
            {
                accepting = false; snapshot = jobs.ToArray();
                foreach (var job in snapshot) Stop(job);
                tails = executions.ToArray();
            }
            foreach (var job in snapshot) Cancel(job);
        }
        finally { acceptance.Release(); Notify(); }
        // Failure leaves memory/registrations/dependencies alive for Return, copy and explicit repair.
        await FlushAsync(explicitRepair: true).ConfigureAwait(false);
        await Task.WhenAll(tails).ConfigureAwait(false);
        await FlushAsync(explicitRepair: true).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync) if (disposed) return;
        await ShutdownAsync().ConfigureAwait(false);
        lock (sync) disposed = true;
        Runtime.Changed -= RuntimeChanged; registration.Dispose();
        foreach (var job in jobs) { job.Demand?.Dispose(); job.Cancellation.Dispose(); }
    }
}
