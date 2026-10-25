using Mindweave.Core.Chat;

namespace Mindweave.Application.Chat;

public sealed partial class GenerationCoordinator
{
    private const string TitleInstruction = "Generate a concise conversation title, at most 40 characters. Return only the title, without quotes or commentary.";

    private void Dispatch()
    {
        lock (sync)
        {
            if (!initialized || !accepting || persistenceFailure || disposed) return;
            while (slots < 2)
            {
                var job = jobs.Where(j => Active(j.Status) && !j.HasSlot && !j.StreamEnded)
                    .OrderBy(j => j.Work.Task.Kind == GenerationKind.Reply ? 0 : 1).FirstOrDefault();
                if (job is null) break;
                slots++;
                job.HasSlot = true;
                job.Status = GenerationStatus.Running;
                job.Revision = checked(job.Revision + 1);
                job.Dirty = true;
                job.Execution = Task.Run(() => ExecuteAsync(job));
            }
        }
    }

    private async Task ExecuteAsync(Job job)
    {
        try
        {
            await FlushAsync(throwOnFailure: false).ConfigureAwait(false);
            lock (sync) if (persistenceFailure || !Active(job.Status)) return;
            string secret;
            try { secret = await vault.ReadAsync(job.Work.Task.Configuration.CredentialReference).ConfigureAwait(false); }
            catch { Fail(job, "CredentialUnavailable"); return; }
            lock (sync) if (persistenceFailure || !Active(job.Status)) return;
            var input = ChatRequestBudget.Inputs(job.Work.InputMessages).ToList();
            if (job.Work.Task.Kind == GenerationKind.Title) input.Insert(0, new("system", TitleInstruction));
            var request = new ChatRequest(job.Work.Task.Configuration, input.ToArray());
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
        catch (ChatTransportException error) { Fail(job, SafeCode(error.ErrorCode)); }
        catch (OperationCanceledException) { Fail(job, "Cancelled"); }
        catch { Fail(job, "TransportError"); }
        finally
        {
            lock (sync)
            {
                // A pre-dispatch save failure leaves this job resumable, with no physical request ever started.
                if (!Active(job.Status)) job.StreamEnded = true;
                job.HasSlot = false;
                slots--;
            }
            Notify();
            // Physical network slot is now free. Reply dispatch can proceed before low-priority title acceptance.
            Dispatch();
            await FlushAsync(throwOnFailure: false).ConfigureAwait(false);
            await AcceptTitlesAsync().ConfigureAwait(false);
            Dispatch(); Notify();
        }
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
                KeyValuePair<Guid, AiConfiguration> candidate;
                lock (sync)
                {
                    if (!accepting || persistenceFailure || pendingTitles.Count == 0) return;
                    candidate = pendingTitles.First();
                }
                try
                {
                    var configuration = candidate.Value with { MaxOutputTokens = Math.Min(128, candidate.Value.MaxOutputTokens) };
                    var messages = await store.ReadMessagesAsync(candidate.Key).ConfigureAwait(false);
                    var answer = ChatHistory.LatestInput(messages).FirstOrDefault(m => m.Role == MessageRole.Assistant && m.Status == GenerationStatus.Completed);
                    GenerationWork? work = null;
                    if (answer is not null)
                    {
                        var user = messages.Single(m => m.Id == answer.ReplyToUserId);
                        ChatInput[] input = [new("system", TitleInstruction), new("user", user.Body), new("assistant", answer.Body)];
                        if (ChatRequestBudget.Fits(configuration, input))
                            work = await store.AcceptTitleAsync(candidate.Key, configuration).ConfigureAwait(false);
                    }
                    lock (sync)
                    {
                        pendingTitles.Remove(candidate.Key);
                        if (work is not null) jobs.Add(new(work));
                    }
                }
                catch { MarkPersistenceFailure(); return; }
            }
        }
        finally { acceptance.Release(); Dispatch(); Notify(); }
    }
}
