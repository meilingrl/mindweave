using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Credentials;
using Mindweave.Application.Diagnostics;
using Mindweave.Application.Languages;
using Mindweave.Application.Storage;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Application.Conversations;

/// <summary>
/// The request plumbing the generation runners share: reading the key, streaming, tracing each request, the
/// compression prelude, and a task's terminal row. The only generation code that reads credentials.
/// </summary>
public sealed partial class GenerationRequests
{
    private readonly IConversationStore _store;
    private readonly IChatClient _client;
    private readonly ICredentialStore _credentials;
    private readonly IAiRequestStore _requests;
    private readonly ContextBudgets _budgets;
    private readonly StorageHealth _health;
    private readonly IClock _clock;
    private readonly ILogger<GenerationRequests> _logger;
    private readonly ContextCompressor? _compressor;
    private readonly ResourceContext? _resources;

    public GenerationRequests(
        IConversationStore store,
        IChatClient client,
        ICredentialStore credentials,
        IAiRequestStore requests,
        ContextBudgets budgets,
        StorageHealth health,
        IClock clock,
        ILogger<GenerationRequests> logger,
        ContextCompressor? compressor = null,
        ResourceContext? resources = null,
        Runs.RunServices? runs = null)
    {
        Runs = runs;
        _resources = resources;
        _store = store;
        _client = client;
        _credentials = credentials;
        _requests = requests;
        _budgets = budgets;
        _health = health;
        _clock = clock;
        _logger = logger;
        _compressor = compressor;
    }

    /// <summary>Run steps and tool results (phase 2); null where runs are not wired (an answer then never has tools).</summary>
    internal Runs.RunServices? Runs { get; }

    internal IClock Clock => _clock;

    internal ContextBudgets Budgets => _budgets;

    internal IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, CancellationToken stop) =>
        _client.StreamAsync(request, apiKey, stop);

    /// <summary>
    /// Summarizes older context in the calling task's slot (the budget is computed when the work carries none). Each
    /// caller merges the prepared turns into its own request.
    /// </summary>
    internal async Task<PreparedContext> PrepareCompressedAsync(GenerationTaskRecord task, ContextPlan plan, ContextBudget? budget, string apiKey, CancellationToken stop)
    {
        var compressor = _compressor ?? throw new ContextCompressionException("No context compressor is configured.");
        return await compressor.PrepareAsync(task, plan, budget ?? await _budgets.BudgetForAsync(task.Model, CancellationToken.None).ConfigureAwait(false), apiKey, RecordAsync, stop, _health.TryAdmit).ConfigureAwait(false);
    }

    /// <summary>
    /// The answer's context (architecture §15.4) in the task's own slot: the attachment summaries the plan needs are written
    /// first (each once, recorded as resource_summary requests of this task), the plan is made again with their real sizes,
    /// then older conversation is summarized as before and every message gets its attachments' material. Returns the
    /// prepared turns and the plan they follow. A plan that no longer fits fails like a compression that does not fit.
    /// </summary>
    internal async Task<(PreparedContext Prepared, ContextPlan Plan)> PrepareContextAsync(
        GenerationTaskRecord task, ContextPlan plan, ContextBudget? budget, ContextInputs? inputs, string apiKey, CancellationToken stop)
    {
        var compressor = _compressor ?? throw new ContextCompressionException("No context compressor is configured.");
        budget ??= await _budgets.BudgetForAsync(task.Model, CancellationToken.None).ConfigureAwait(false);
        IReadOnlyDictionary<MessageId, TurnMaterials>? materials = null;
        if (_resources is not null && plan.Resources.Count > 0)
        {
            if (inputs is not null)
            {
                var candidates = inputs.Resources.ToList();
                for (var round = 0; round < 3 && plan.Resources.Any(r => r.NeedsSummary); round++)
                {
                    foreach (var placement in plan.Resources.Where(r => r.NeedsSummary).DistinctBy(r => r.Resource))
                    {
                        if (!_health.TryAdmit())
                        {
                            throw new StorageAdmissionRefusedException();
                        }

                        var tokens = await _resources.SummarizeAsync(
                                task.Model, placement.Attachment, r => RequestCompleteTextAsync(task, r, apiKey, stop, purpose: RequestPurpose.ResourceSummary), stop)
                            .ConfigureAwait(false);
                        candidates = [.. candidates.Select(c => c.Attachment.Resource == placement.Resource ? c with { SummaryTokens = tokens } : c)];
                    }

                    var known = await compressor.KnownSummaryTokensAsync([.. inputs.Ancestors.Select(a => a.Conversation), inputs.Own.Conversation]).ConfigureAwait(false);
                    plan = ContextPlanner.Plan(inputs.Ancestors, inputs.Own, budget.Available - inputs.InstructionTokens, known, candidates, ModelPhrases.Current).Plan
                        ?? throw new ContextCompressionException(T(
                            "The attachments' digests are longer than expected; the question and its attachments do not fit in what the model can take.",
                            "附件的摘要比预计的长，问题和附件放不进模型的可用容量。"));
                }
            }

            materials = await _resources.MaterialsAsync(plan, CancellationToken.None).ConfigureAwait(false);
        }

        var prepared = await compressor.PrepareAsync(task, plan, budget, apiKey, RecordAsync, stop, _health.TryAdmit, materials).ConfigureAwait(false);
        return (prepared, plan);
    }

    /// <summary>Records how the task sent its attachments (for the answer's note); only after the context was prepared.</summary>
    internal Task RecordResourceInputAsync(GenerationTaskId task, ContextPlan plan) =>
        _resources is null || plan.Resources.Count == 0 ? Task.CompletedTask : _resources.RecordAsync(task, plan);

    /// <summary>Records which summaries a task sent; only called after <see cref="PrepareCompressedAsync"/> succeeded.</summary>
    internal Task RecordCompressedInputAsync(GenerationTaskId task, IReadOnlyList<Guid> ids) => _compressor!.RecordTaskInputAsync(task, ids);

    /// <summary>
    /// One request whose output the program uses directly: only a normal, non-empty finish counts (D-015); a cut-off
    /// output is never used. Returns the text, or null with the reason. The request is recorded for its task, or, for a
    /// request without a task row (<see cref="TransientWork"/>), for <paramref name="attribution"/>: without either,
    /// <paramref name="record"/> false, nothing is recorded.
    /// </summary>
    internal async Task<(string? Text, string? Problem)> RequestCompleteTextAsync(
        GenerationTaskRecord task, ChatRequest request, string apiKey, CancellationToken stop, bool record = true, RequestAttribution? attribution = null, string? purpose = null)
    {
        if (!_health.TryAdmit())
        {
            throw new StorageAdmissionRefusedException();
        }

        var trace = attribution is null ? new RequestTrace(task.Id, _clock, request, purpose) : new RequestTrace(attribution, _clock, request);
        record = record || attribution is not null;
        var text = new System.Text.StringBuilder();
        ChatStreamEvent.Completed? completed = null;
        try
        {
            await foreach (var item in _client.StreamAsync(request, apiKey, stop).ConfigureAwait(false))
            {
                trace.Observe(item);
                if (item is ChatStreamEvent.TextDelta delta)
                {
                    text.Append(delta.Text);
                }
                else if (item is ChatStreamEvent.Completed done)
                {
                    completed = done;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            if (record)
            {
                await RecordAsync(trace.Finish(stopped: true, null, null)).ConfigureAwait(false);
            }

            throw;
        }
        catch (ChatClientException ex)
        {
            trace.Fail(ex);
            if (record)
            {
                await RecordAsync(trace.Finish(stopped: false, ex.Kind, ex.Message)).ConfigureAwait(false);
            }

            throw;
        }

        var output = ConversationRules.WithoutInlineReasoning(text.ToString());
        var problem = completed is null ? T("The answer stream did not end normally.", "回答流未正常结束。")
            : completed.Filtered ? FilteredProblem
            : completed.Truncated ? T("The output was cut off at its limit; nothing was saved.", "输出达到上限被截断，未保存。")
            : output.Length == 0 ? T("The service returned empty content.", "服务返回了空内容。")
            : null;
        if (record)
        {
            await RecordAsync(trace.Finish(stopped: false, problem is null ? null : GenerationErrorKind.InvalidResponse, problem)).ConfigureAwait(false);
        }

        return problem is null ? (output, null) : (null, problem);
    }

    /// <summary>The reason <see cref="RequestCompleteTextAsync"/> gives when content moderation stopped the output: asking again would only be stopped again.</summary>
    internal static string FilteredProblem => T("The service's content moderation stopped the output; nothing was saved.", "服务的内容审核中止了输出，未保存。");

    /// <summary>Diagnostics only: a failed trace write is logged and does not count as a storage failure.</summary>
    internal async Task RecordAsync(AiRequestRecord record)
    {
        try
        {
            await _requests.AddAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A lost trace must not affect the answer or the storage-health state.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogTraceFailed(_logger, ex, record.TaskId?.ToString() ?? record.Attribution?.Kind);
        }
    }

    internal async Task<(string? Secret, GenerationErrorKind? Error, string? Detail)> ReadKeyAsync(GenerationTaskRecord task)
    {
        try
        {
            var secret = await _credentials.ReadAsync(task.Model.CredentialRef, CancellationToken.None).ConfigureAwait(false);
            return secret is null
                ? (null, GenerationErrorKind.NotConfigured, "The API key for this connection is missing.")
                : (secret, null, null);
        }
        catch (CredentialUnavailableException ex)
        {
            return (null, GenerationErrorKind.CredentialUnavailable, ex.Message);
        }
    }

    internal async Task<bool> FinishTaskAsync(GenerationTaskId id, GenerationTaskState state, GenerationErrorKind? error, string? detail)
    {
        await _store.FinishTaskAsync(id, state, error, detail, _clock.UtcNow, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request trace for {Owner} was not saved")]
    private static partial void LogTraceFailed(ILogger logger, Exception exception, string? owner);
}
