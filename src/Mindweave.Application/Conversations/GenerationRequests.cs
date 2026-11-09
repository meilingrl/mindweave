using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Credentials;
using Mindweave.Application.Diagnostics;
using Mindweave.Application.Storage;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;

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

    public GenerationRequests(
        IConversationStore store,
        IChatClient client,
        ICredentialStore credentials,
        IAiRequestStore requests,
        ContextBudgets budgets,
        StorageHealth health,
        IClock clock,
        ILogger<GenerationRequests> logger,
        ContextCompressor? compressor = null)
    {
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

    /// <summary>Records which summaries a task sent; only called after <see cref="PrepareCompressedAsync"/> succeeded.</summary>
    internal Task RecordCompressedInputAsync(GenerationTaskId task, IReadOnlyList<Guid> ids) => _compressor!.RecordTaskInputAsync(task, ids);

    /// <summary>
    /// One request whose output the program uses directly: only a normal, non-empty finish counts (D-015); a cut-off
    /// output is never used. Returns the text, or null with the reason.
    /// </summary>
    internal async Task<(string? Text, string? Problem)> RequestCompleteTextAsync(GenerationTaskRecord task, ChatRequest request, string apiKey, CancellationToken stop)
    {
        if (!_health.TryAdmit())
        {
            throw new StorageAdmissionRefusedException();
        }

        var trace = new RequestTrace(task.Id, _clock, request);
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
            await RecordAsync(trace.Finish(stopped: true, null, null)).ConfigureAwait(false);
            throw;
        }
        catch (ChatClientException ex)
        {
            trace.Fail(ex);
            await RecordAsync(trace.Finish(stopped: false, ex.Kind, ex.Message)).ConfigureAwait(false);
            throw;
        }

        var output = text.ToString().Trim();
        var problem = completed is null ? "回答流未正常结束。"
            : completed.Truncated ? "输出达到上限被截断，未保存。"
            : output.Length == 0 ? "服务返回了空内容。"
            : null;
        await RecordAsync(trace.Finish(stopped: false, problem is null ? null : GenerationErrorKind.InvalidResponse, problem)).ConfigureAwait(false);
        return problem is null ? (output, null) : (null, problem);
    }

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
            LogTraceFailed(_logger, ex, record.TaskId);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request trace for task {TaskId} was not saved")]
    private static partial void LogTraceFailed(ILogger logger, Exception exception, GenerationTaskId taskId);
}
