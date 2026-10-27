using Mindweave.Core.Chat;

namespace Mindweave.Application.Chat;

public sealed partial class GenerationCoordinator
{
    private bool acceptingTitles;
    private const string TitleInstruction = "Generate a concise conversation title, at most 40 characters. Return only the title, without quotes or commentary.";

    private void Dispatch()
    {
        lock (sync)
        {
            if (!initialized || !accepting || PersistencePaused || disposed) return;
            if (pendingTitles.Count > 0 && !acceptingTitles)
            {
                acceptingTitles = true;
                TrackExecution(async () =>
                {
                    try { await AcceptTitlesAsync().ConfigureAwait(false); }
                    finally { lock (sync) acceptingTitles = false; Notify(); }
                });
            }
            // Preparation and durable writes do not reserve physical network capacity.
            foreach (var pending in jobs.Where(j => Active(j.Status) && !j.Busy && !j.StreamEnded && j.Request is null).ToArray())
            {
                pending.Busy = true;
                TrackExecution(() => PreparePhaseAsync(pending));
            }
            foreach (var job in jobs.Where(j => Active(j.Status) && !j.Busy && !j.StreamEnded && j.Request is not null).ToArray())
            {
                job.Busy = true;
                TrackExecution(() => ExecuteAsync(job));
            }
        }
    }

    private async Task ExecuteAsync(Job job)
    {
        try
        {
            lock (sync)
            {
                if (PersistencePaused || !Active(job.Status)) return;
                // Durable readiness is recorded without claiming physical capacity or changing the queued UI state.
                job.Revision = checked(job.Revision + 1); job.Dirty = true;
            }
            await FlushAsync(throwOnFailure: false).ConfigureAwait(false);
            lock (sync) if (PersistencePaused || !Active(job.Status)) return;
            string secret;
            try { secret = await vault.ReadAsync(job.Work.Task.Configuration.CredentialReference).ConfigureAwait(false); }
            catch { Fail(job, "CredentialUnavailable"); return; }
            lock (sync) if (PersistencePaused || !Active(job.Status)) return;
            await job.Demand!.RunPhaseAsync(async () =>
            {
                lock (sync)
                {
                    if (PersistencePaused || !Active(job.Status)) return;
                    job.HasSlot = true;
                    job.Status = GenerationStatus.Running;
                    job.Revision = checked(job.Revision + 1); job.Dirty = true;
                }
                Notify();
                lock (sync) if (PersistencePaused || !Active(job.Status)) return;
                await ExecutePhysicalAsync(job, job.Request!, secret).ConfigureAwait(false);
            }, job.Cancellation.Token).ConfigureAwait(false);
        }
        catch (ChatTransportException error) { Fail(job, SafeCode(error.ErrorCode)); }
        catch (OperationCanceledException) { Fail(job, "Cancelled"); }
        catch { Fail(job, "TransportError"); }
        finally
        {
            lock (sync)
            {
                if (!Active(job.Status)) { job.StreamEnded = true; job.Demand?.Dispose(); }
                job.HasSlot = false; job.Request = null;
            }
            Notify(); Dispatch();
            try
            {
                await FlushAsync(throwOnFailure: false).ConfigureAwait(false);
                await AcceptTitlesAsync().ConfigureAwait(false);
            }
            finally
            {
                lock (sync) job.Busy = false;
                Dispatch(); Notify();
            }
        }
    }

    private async Task ExecutePhysicalAsync(Job job, ChatRequest request, string secret)
    {
        if (job.Phase == GenerationPhase.Compressing)
        {
            await ExecuteCompressionAsync(job, request, secret).ConfigureAwait(false);
            return;
        }
        string? finish = null;
        // The slot remains held through enumeration and DisposeAsync, including cancellation-ignoring adapters.
        await foreach (var item in transport.StreamAsync(request, secret, job.Cancellation.Token).ConfigureAwait(false))
        {
            bool checkpoint;
            lock (sync)
            {
                if (!Active(job.Status)) continue;
                if (item.Text is not null) job.Body.Append(item.Text);
                if (item.FinishReason is not null) finish = item.FinishReason;
                if (item.ActualModelId is { } model && !string.IsNullOrWhiteSpace(model) && model.Length <= 256 && !model.Any(char.IsControl))
                    job.ActualModel = model;
                job.Revision = checked(job.Revision + 1);
                job.Dirty = true;
                checkpoint = job.Body.Length - job.PersistedLength >= 4096;
            }
            Notify();
            // A growth checkpoint is scheduled independently; stream consumption never waits for disk.
            if (checkpoint) RequestCheckpoint();
        }
        lock (sync)
        {
            if (Active(job.Status))
            {
                if (finish != "stop") Terminate(job, GenerationStatus.Failed, finish is null ? "IncompleteResponse" : "UnsupportedFinish");
                else if (string.IsNullOrWhiteSpace(job.Body.ToString())) Terminate(job, GenerationStatus.Failed, "EmptyResponse");
                else if (job.Work.Task.Kind == GenerationKind.Title && job.Body.ToString().Trim().Length > 160)
                    Terminate(job, GenerationStatus.Failed, "InvalidTitle");
                else Terminate(job, GenerationStatus.Completed, null);
            }
        }
    }

    private void TrackExecution(Func<Task> action)
    {
        executions.RemoveAll(task => task.IsCompleted);
        executions.Add(Task.Run(action));
    }

    private void Fail(Job job, string error)
    { lock (sync) if (Active(job.Status)) Terminate(job, GenerationStatus.Failed, error); }
    private static string SafeCode(string code) => code.Length is > 0 and <= 64 && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? code : "TransportError";

    private async Task AcceptTitlesAsync()
    {
        await acceptance.WaitAsync().ConfigureAwait(false);
        try
        {
            while (true)
            {
                Guid candidate;
                lock (sync)
                {
                    if (!accepting || PersistencePaused || pendingTitles.Count == 0) return;
                    candidate = pendingTitles.First();
                }
                try
                {
                    // This title is a new task: current settings govern its acceptance/budget.
                    // Only already accepted work keeps an older immutable configuration snapshot.
                    var active = await store.LoadActiveConfigurationAsync().ConfigureAwait(false);
                    if (active is null)
                    {
                        lock (sync) pendingTitles.Remove(candidate);
                        continue;
                    }
                    var configuration = active with { MaxOutputTokens = Math.Min(128, active.MaxOutputTokens) };
                    var messages = await store.ReadMessagesAsync(candidate).ConfigureAwait(false);
                    var answer = ChatHistory.LatestInput(messages).FirstOrDefault(m => m.Role == MessageRole.Assistant && m.Status == GenerationStatus.Completed);
                    GenerationWork? work = null;
                    if (answer is not null)
                    {
                        var user = messages.Single(m => m.Id == answer.ReplyToUserId);
                        ChatInput[] input = [new("system", TitleInstruction), new("user", user.Body), new("assistant", answer.Body)];
                        if (ChatRequestBudget.Fits(configuration, input))
                        {
                            Task<GenerationWork?> accepted;
                            lock (sync)
                            {
                                if (!accepting || PersistencePaused) return;
                                accepted = store.AcceptTitleAsync(candidate, configuration);
                            }
                            work = await accepted.ConfigureAwait(false);
                        }
                    }
                    lock (sync)
                    {
                        pendingTitles.Remove(candidate);
                        if (work is not null) jobs.Add(CreateJob(work));
                    }
                }
                catch { MarkPersistenceFailure(); return; }
            }
        }
        finally { acceptance.Release(); Dispatch(); Notify(); }
    }
}
