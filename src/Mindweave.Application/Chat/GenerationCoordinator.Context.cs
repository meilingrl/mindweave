using System.Text;
using Mindweave.Core.Chat;

namespace Mindweave.Application.Chat;

public sealed partial class GenerationCoordinator
{
    private async Task PreparePhaseAsync(Job job)
    {
        try
        {
            await FlushAsync(throwOnFailure: false).ConfigureAwait(false);
            await persistence.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (sync) if (PersistencePaused || !accepting || !Active(job.Status)) return;
                if (job.Work.Task.Kind == GenerationKind.Title)
                {
                    var input = ChatRequestBudget.Inputs(job.Work.InputMessages, job.Work.SourceQuote).ToList();
                    input.Insert(0, new("system", TitleInstruction));
                    lock (sync) job.Request = new(job.Work.Task.Configuration, input.ToArray());
                    return;
                }
                var plan = job.Work.ContextPlan ?? throw new InvalidOperationException("回答缺少上下文计划。");
                var summaries = new Dictionary<string, ContextSummaryContent>();
                foreach (var part in plan.Parts.OfType<SummaryContextPart>())
                {
                    lock (sync) if (PersistencePaused || !Active(job.Status)) return;
                    var cached = await store.LoadCompletedSummaryAsync(job.Work.Task.Id, part.Chunk.CacheKey).ConfigureAwait(false);
                    if (cached is not null) { summaries.Add(part.Chunk.CacheKey, cached.Content); continue; }
                    ContextCompressionAttempt attempt;
                    lock (sync) job.UnresolvedChunkKey = part.Chunk.CacheKey;
                    try { attempt = await store.StartCompressionAsync(job.Work.Task.Id, part.Chunk.CacheKey).ConfigureAwait(false); }
                    catch (InvalidOperationException)
                    {
                        // Another sibling may have committed this source chunk after our cache read.
                        cached = await store.LoadCompletedSummaryAsync(job.Work.Task.Id, part.Chunk.CacheKey).ConfigureAwait(false);
                        if (cached is null) throw;
                        lock (sync) job.UnresolvedChunkKey = null;
                        summaries.Add(part.Chunk.CacheKey, cached.Content);
                        continue;
                    }
                    lock (sync)
                    {
                        job.UnresolvedChunkKey = null;
                        job.Attempt = attempt;
                        if (!Active(job.Status))
                        {
                            if (Active(attempt.Status))
                            {
                                job.Attempt = attempt with { Status = job.Status, Revision = checked(attempt.Revision + 1), ErrorCode = job.Error };
                                job.CompressionDirty = true;
                            }
                            return;
                        }
                    }
                    if (attempt.Status == GenerationStatus.Completed)
                    {
                        cached = await store.LoadCompletedSummaryAsync(job.Work.Task.Id, part.Chunk.CacheKey).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("完成压缩缺少有效缓存。");
                        summaries.Add(part.Chunk.CacheKey, cached.Content);
                        continue;
                    }
                    var request = ReplyContextPlanner.CreateSummaryRequest(attempt.SourceConversationId, attempt.Chunk, job.Work.InputMessages, attempt.Configuration);
                    lock (sync)
                    {
                        job.CompletedChunks = summaries.Count;
                        job.Phase = GenerationPhase.Compressing;
                        job.Request = request;
                    }
                    return;
                }
                lock (sync) job.CompletedChunks = summaries.Count;
                // Classify a valid but insufficiently short summary as a generation failure,
                // not a disk failure. Only the store-returned, durable Request is sent.
                try { _ = ReplyContextPlanner.ComposeInputs(plan, job.Work.InputMessages, summaries, job.Work.Task.Configuration); }
                catch (InvalidOperationException) { Fail(job, "ContextBudgetExceeded"); return; }
                lock (sync) if (PersistencePaused || !Active(job.Status)) return;
                var prepared = await store.PrepareReplyRequestAsync(job.Work.Task.Id).ConfigureAwait(false);
                lock (sync)
                {
                    job.CompletedChunks = summaries.Count;
                    job.Phase = GenerationPhase.Reply;
                    job.Attempt = null;
                    job.Request = prepared.Request;
                }
            }
            finally { persistence.Release(); }
        }
        catch { MarkPersistenceFailure(); }
        finally
        {
            await FlushAsync(throwOnFailure: false).ConfigureAwait(false);
            lock (sync) job.Busy = false;
            Dispatch(); Notify();
        }
    }

    private async Task ExecuteCompressionAsync(Job job, ChatRequest request, string secret)
    {
        string? finish = null;
        await foreach (var item in transport.StreamAsync(request, secret, job.Cancellation.Token).ConfigureAwait(false))
        {
            lock (sync)
            {
                if (!Active(job.Status)) continue;
                var attempt = job.Attempt!;
                var text = item.Text ?? "";
                // Keep both temporary allocation and saved partial output bounded. Counting the
                // combined string also handles a surrogate pair split across two deltas.
                if (text.Length > ContextSummaryProtocol.MaximumPayloadBytes ||
                    attempt.Payload.Length + text.Length > ContextSummaryProtocol.MaximumPayloadBytes ||
                    Encoding.UTF8.GetByteCount(attempt.Payload + text) > ContextSummaryProtocol.MaximumPayloadBytes)
                {
                    Terminate(job, GenerationStatus.Failed, "ContextSummaryTooLarge");
                }
                else
                {
                    if (item.FinishReason is not null) finish = item.FinishReason;
                    var model = item.ActualModelId;
                    job.Attempt = attempt with { Payload = attempt.Payload + text, Revision = checked(attempt.Revision + 1),
                        Status = GenerationStatus.Running,
                        ActualModelId = model is not null && !string.IsNullOrWhiteSpace(model) && model.Length <= 256 && !model.Any(char.IsControl)
                            ? model : attempt.ActualModelId };
                    job.CompressionDirty = true;
                }
            }
            Notify();
            if (!ActiveStatus(job)) { job.Cancellation.Cancel(); break; }
        }
        lock (sync)
        {
            if (!Active(job.Status)) return;
            var attempt = job.Attempt!;
            string? error = finish != "stop" ? finish is null ? "IncompleteContextSummary" : "UnsupportedContextFinish"
                : string.IsNullOrWhiteSpace(attempt.Payload) ? "EmptyContextSummary" : null;
            if (error is null)
            {
                try { _ = ContextSummaryProtocol.Parse(attempt.Payload); }
                catch (InvalidOperationException) { error = "InvalidContextSummary"; }
            }
            job.Attempt = attempt with { Revision = checked(attempt.Revision + 1), FinishReason = finish,
                Status = error is null ? GenerationStatus.Completed : GenerationStatus.Failed, ErrorCode = error };
            job.CompressionDirty = true;
            if (error is not null) Terminate(job, GenerationStatus.Failed, error);
        }
    }

    private bool ActiveStatus(Job job) { lock (sync) return Active(job.Status); }

    // Always called with the persistence semaphore; child evidence must commit while
    // its requester is still active on disk, even if memory has already stopped it.
    private async Task FlushCompressionAsync(Job job)
    {
        string? unresolved;
        lock (sync) unresolved = job.UnresolvedChunkKey;
        if (unresolved is not null)
        {
            var recovered = await store.LoadCompressionAttemptForReplyAsync(job.Work.Task.Id, unresolved).ConfigureAwait(false);
            lock (sync)
            {
                job.UnresolvedChunkKey = null;
                job.Attempt = recovered;
                if (recovered is not null && Active(recovered.Status) && !Active(job.Status))
                {
                    job.Attempt = recovered with { Status = job.Status, Revision = checked(recovered.Revision + 1), ErrorCode = job.Error };
                    job.CompressionDirty = true;
                }
            }
        }
        ContextCompressionAttempt? attempt;
        lock (sync) attempt = job.CompressionDirty ? job.Attempt : null;
        if (attempt is null) return;
        var saved = await store.WriteCompressionAsync(job.Work.Task.Id, attempt.Id, attempt.Revision, attempt.Payload,
            attempt.Status, attempt.FinishReason, attempt.ErrorCode, attempt.ActualModelId).ConfigureAwait(false);
        if (!saved)
        {
            var current = await store.LoadCompressionAttemptAsync(attempt.Id).ConfigureAwait(false);
            if (current is null || current.Revision != attempt.Revision || current.Status != attempt.Status ||
                current.Payload != attempt.Payload || current.FinishReason != attempt.FinishReason ||
                current.ErrorCode != attempt.ErrorCode || current.ActualModelId != attempt.ActualModelId)
                throw new InvalidOperationException("压缩尝试保存冲突。");
        }
        if (attempt.Status == GenerationStatus.Completed)
            _ = await store.LoadCompletedSummaryAsync(job.Work.Task.Id, attempt.Chunk.CacheKey).ConfigureAwait(false)
                ?? throw new InvalidOperationException("完成压缩缺少有效缓存。");
        lock (sync) if (job.Attempt?.Revision == attempt.Revision) job.CompressionDirty = false;
    }
}
