using Mindweave.Application.Chat;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.Summaries;

public sealed partial class KnowledgeSummaryCoordinator
{
    private void Dispatch()
    {
        lock (sync)
        {
            if (!initialized || !accepting || disposed || Runtime.IsAdmissionPaused) return;
            executions.RemoveAll(t => t.IsCompleted);
            foreach (var job in jobs.Where(j => Active(j.Status) && !j.Busy && !j.StopRequested && j.Completion is null))
            {
                job.Busy = true;
                var execution = Task.Run(() => ExecuteAsync(job));
                executions.Add(execution);
                _ = execution.ContinueWith(_ => { Dispatch(); Notify(); }, TaskScheduler.Default);
            }
        }
    }

    private async Task ExecuteAsync(Job job)
    {
        try
        {
            // Source reconstruction and durable readiness never hold a physical request slot.
            await persistence.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (sync) if (!Active(job.Status) || job.StopRequested || Runtime.IsAdmissionPaused) return;
                await PrepareAsync(job).ConfigureAwait(false);
            }
            catch { lock (sync) job.NeedsPreparationRepair = true; failure.Report(); return; }
            finally { persistence.Release(); }
            lock (sync) if (!Active(job.Status) || job.StopRequested || job.Prepared is null) return;
            string secret;
            try { secret = await vault.ReadAsync(job.Prepared.Request.Configuration.CredentialReference).ConfigureAwait(false); }
            catch { await FailAsync(job, "CredentialUnavailable").ConfigureAwait(false); return; }
            SummaryContent? content = null;
            string? model = null;
            try
            {
                await job.Demand!.RunPhaseAsync(async () =>
                {
                    lock (sync)
                    {
                        if (!Active(job.Status) || job.StopRequested || Runtime.IsAdmissionPaused) return;
                        job.Status = GenerationStatus.Running;
                    }
                    Notify();
                    var payload = new KnowledgeSummaryPayloadBuffer();
                    string? finish = null;
                    // await foreach includes DisposeAsync. Parse only after both exhaustion and disposal succeeded.
                    await foreach (var item in transport.StreamAsync(job.Prepared.Request, secret, job.Cancellation.Token).ConfigureAwait(false))
                    {
                        lock (sync) if (job.StopRequested || !Active(job.Status)) continue;
                        if (item.Text is { } delta) payload.Append(delta);
                        if (item.ActualModelId is { } actual) model = actual;
                        if (item.FinishReason is { } reason)
                        {
                            if (finish is not null && finish != reason) throw new SummaryProtocolException("总结终止标记冲突。");
                            finish = reason;
                        }
                    }
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    if (finish != "stop") throw new SummaryProtocolException("总结未完整结束。");
                    content = KnowledgeSummaryProtocol.ParseComplete(payload.Complete());
                    if (model is { } m && (m.Length > 256 || m.Any(char.IsControl))) throw new SummaryProtocolException("总结模型标识无效。");
                }, job.Cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                await FailAsync(job, error is SummaryProtocolException ? "InvalidSummaryResponse" : "SummaryTransportFailed").ConfigureAwait(false);
                return;
            }
            lock (sync)
            {
                if (content is null || job.StopRequested || !Active(job.Status)) return;
                job.Completion = new(job.Record.Id, job.Record.Revision, content, model);
            }
            // No next AI phase: release logical priority while retaining the independent persistence tail.
            job.Demand?.Dispose();
            await FlushAsync(explicitRepair: false).ConfigureAwait(false);
        }
        catch { /* Durable helpers report their own failure; retained state is repaired explicitly. */ }
        finally
        {
            lock (sync) { job.Busy = false; if (!Active(job.Status) || job.Completion is not null) job.Demand?.Dispose(); }
            // This task remains in executions until complete, even if its visible status is terminal.
            Notify();
        }
    }

    // Caller holds persistence. Also used by explicit repair while dispatch is globally paused.
    private async Task PrepareAsync(Job job)
    {
        if (job.Prepared is null)
        {
            var prepared = await store.PrepareKnowledgeSummaryRequestAsync(job.Record.Id, job.Record.Revision).ConfigureAwait(false);
            if (!Success(prepared.Kind) || prepared.Value is null)
            {
                await ReconcileOutcomeAsync(job, prepared.Code ?? "SummaryPreparationRejected").ConfigureAwait(false);
                return;
            }
            lock (sync) job.Prepared = prepared.Value;
        }
        lock (sync) job.NeedsPreparationRepair = false;
    }

    private async Task FailAsync(Job job, string code)
    {
        lock (sync)
        {
            if (!Active(job.Status) || job.StopRequested) return;
            job.Status = GenerationStatus.Failed; job.Error = code; job.Dirty = true;
        }
        job.Demand?.Dispose(); Notify();
        await FlushAsync(explicitRepair: false).ConfigureAwait(false);
    }
}
