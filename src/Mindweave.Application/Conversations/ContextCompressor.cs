using System.Text;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Diagnostics;
using Mindweave.Application.Languages;
using Mindweave.Application.Prompts;
using Mindweave.Application.Storage;
using Mindweave.Core.Branches;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Application.Conversations;

/// <summary>What an answer request finally sends, and which stored summaries it used.</summary>
public sealed record PreparedContext(IReadOnlyList<ChatTurn> Turns, IReadOnlyList<Guid> SummaryIds);

/// <summary>Summarizing older context failed; the answer fails and can be retried. The message is safe to show.</summary>
public sealed class ContextCompressionException : Exception
{
    public ContextCompressionException()
    {
    }

    public ContextCompressionException(string message)
        : base(message)
    {
    }

    public ContextCompressionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Turns a <see cref="ContextPlan"/> into the turns of an answer request (architecture §6.1–6.3). Runs inside the
/// answer task's own slot, before the answer request: stored summaries are reused, missing ones are written from
/// the original messages (never from other summaries) with the task's frozen model and key. Every summary request
/// is traced under the answer task.
/// </summary>
public sealed partial class ContextCompressor(IContextSummaryStore summaries, IChatClient client, IClock clock, ILogger<ContextCompressor> logger)
{
    /// <summary>
    /// Room for reasoning models, which spend part of the output limit before writing anything. With 4,096 a real
    /// reasoning model replaying the same summary request reasoned 718–4,096 tokens: 2 of 8 runs hit the limit, one
    /// with no text and one cut off after 179 characters (development findings, 2026-10-27).
    /// </summary>
    public const int SummaryOutputTokens = 16_384;

    /// <summary>The least output a chunk summary asks for, however full the window already is (600 characters or 400 words of summary, and some reasoning).</summary>
    public const int MinimumSummaryOutputTokens = 2_048;

    /// <summary>A summary that comes back cut off or empty is requested again once, within the same answer task.</summary>
    public const int SummaryAttempts = 2;

    private static string FilteredProblem => T("stopped by the service's content moderation", "被服务的内容审核中止");

    public static PreparedContext Unsummarized(ContextPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new PreparedContext([.. plan.RawItems.Select(Turn)], []);
    }

    /// <param name="admit">Asked before each summarizing request; when it refuses, nothing more is sent.</param>
    /// <param name="materials">The attachments' material per message (architecture §15.4), put before that message's text.</param>
    internal async Task<PreparedContext> PrepareAsync(
        GenerationTaskRecord task,
        ContextPlan plan,
        ContextBudget budget,
        string apiKey,
        Func<AiRequestRecord, Task> record,
        CancellationToken stop,
        Func<bool>? admit = null,
        IReadOnlyDictionary<MessageId, TurnMaterials>? materials = null)
    {
        materials ??= new Dictionary<MessageId, TurnMaterials>();
        var known = (await summaries.ListAsync([.. plan.Summaries.Select(c => c.Key.Conversation)], CancellationToken.None).ConfigureAwait(false))
            .ToDictionary(s => s.Key);
        var used = new Dictionary<SummaryKey, ContextSummary>();
        foreach (var chunk in plan.Summaries)
        {
            if (!known.TryGetValue(chunk.Key, out var summary))
            {
                if (admit is not null && !admit())
                {
                    throw new StorageAdmissionRefusedException();
                }

                var text = await SummarizeAsync(task, chunk, apiKey, record, stop).ConfigureAwait(false);
                summary = await summaries.AddAsync(new ContextSummary(Guid.CreateVersion7(), chunk.Key, task.Model.Model, text, clock.UtcNow), CancellationToken.None).ConfigureAwait(false);
            }

            used[chunk.Key] = summary;
        }

        var phrases = ModelPhrases.Current;
        var turns = plan.Parts.Select(part => part switch
        {
            RawPart raw => Turn(raw.Item, materials),
            SummaryPart s => new ChatTurn(ChatRole.System, WithMaterials(ContextPlanner.SummaryMessage(s.Chunk.Key, used[s.Chunk.Key].Text, phrases), s.Chunk.Items, materials)),
            _ => throw new InvalidOperationException("Unknown context part."),
        }).ToArray();

        // Summaries were budgeted at an assumed size; a much longer one may still not fit. The margin absorbs small
        // overruns, but the output reserve is never given up.
        var estimate = ChatTurns.Estimate(turns);
        if (!budget.Fits(estimate))
        {
            throw new ContextCompressionException(T(
                $"After compression the context is about {budget.Predict(estimate)} tokens, still more than the model can take.",
                $"压缩后的上下文约 {budget.Predict(estimate)} token，仍超出模型可用容量。"));
        }

        return new PreparedContext(turns, [.. used.Values.Select(s => s.Id).Distinct()]);
    }

    /// <summary>The sizes of the stored summaries of these conversations, for planning again.</summary>
    internal async Task<Func<SummaryKey, int?>> KnownSummaryTokensAsync(IReadOnlyList<ConversationId> conversations)
    {
        var stored = await summaries.ListAsync(conversations, CancellationToken.None).ConfigureAwait(false);
        var known = stored.ToDictionary(s => s.Key, s => ContextPlanner.SummaryTokens(s.Key, s.Text, ModelPhrases.Current));
        return key => known.TryGetValue(key, out var tokens) ? tokens : null;
    }

    /// <summary>Records which summaries the task sent. Diagnostic and for the "compressed" notice: a failed write is only logged.</summary>
    internal async Task RecordTaskInputAsync(GenerationTaskId task, IReadOnlyList<Guid> ids)
    {
        try
        {
            await summaries.RecordTaskInputAsync(task, ids, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Losing the record must not fail an answer whose context is ready.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogInputNotRecorded(logger, ex, task);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Summary inputs of task {TaskId} were not recorded")]
    private static partial void LogInputNotRecorded(ILogger logger, Exception exception, GenerationTaskId taskId);

    private static ChatTurn Turn(ContextItem item) =>
        new(item.Message.Role == MessageRole.User ? ChatRole.User : ChatRole.Assistant, item.Text);

    /// <summary>A question with attachments: their material first, then the question; images as parts of the same turn.</summary>
    private static ChatTurn Turn(ContextItem item, IReadOnlyDictionary<MessageId, TurnMaterials> materials) =>
        item.Message.Role == MessageRole.User && materials.TryGetValue(item.Message.Id, out var m)
            ? new ChatTurn(ChatRole.User, m.Text + "\n\n" + item.Text, m.Images.Count > 0 ? m.Images : null)
            : Turn(item);

    /// <summary>Attachments of messages a summary stands in for still go out (as text; their images only as their names).</summary>
    private static string WithMaterials(string summary, IReadOnlyList<ContextItem> items, IReadOnlyDictionary<MessageId, TurnMaterials> materials)
    {
        var texts = items.Select(i => materials.TryGetValue(i.Message.Id, out var m) ? m.Text : null).OfType<string>().ToList();
        return texts.Count == 0 ? summary : summary + "\n\n" + string.Join("\n\n", texts);
    }

    private async Task<string> SummarizeAsync(GenerationTaskRecord task, ContextChunk chunk, string apiKey, Func<AiRequestRecord, Task> record, CancellationToken stop)
    {
        var transcript = new StringBuilder();
        foreach (var item in chunk.Items)
        {
            transcript.Append(item.Message.Role == MessageRole.User ? T("User: ", "用户：") : T("Assistant: ", "助手：")).Append(item.Text).Append("\n\n");
        }

        var turns = new[] { new ChatTurn(ChatRole.System, PromptLibrary.Get("compression")), new ChatTurn(ChatRole.User, transcript.ToString()) };
        var request = new ChatRequest(task.Model with { MaxOutputTokens = SummaryOutputCap(task.Model, TokenEstimator.Request(turns.Select(t => t.Content))) }, turns);

        // A summary is used only when the service says it finished normally: a cut-off summary would silently stand in
        // for the conversation it was meant to keep. Such a result is requested once more; this re-sends an internal
        // step of the running task, never the user's question.
        string? problem = null;
        for (var attempt = 1; attempt <= SummaryAttempts; attempt++)
        {
            var (summary, outcome) = await RequestSummaryAsync(task, request, apiKey, record, stop).ConfigureAwait(false);
            if (summary is not null)
            {
                return summary;
            }

            problem = outcome;
            if (outcome == FilteredProblem)
            {
                // The same chunk would be filtered again: asking once more only costs another request.
                break;
            }
        }

        throw new ContextCompressionException(problem == FilteredProblem
            ? T($"While compressing the earlier conversation, the service did not return a complete summary ({problem}).", $"压缩较早的对话时服务没有返回完整摘要（{problem}）。")
            : T(
                $"While compressing the earlier conversation, the service did not return a complete summary ({problem}), after {Plural(SummaryAttempts, "attempt", "attempts")}.",
                $"压缩较早的对话时服务没有返回完整摘要（{problem}），已尝试 {SummaryAttempts} 次。"));
    }

    /// <summary>
    /// The output a chunk summary asks for (health check 2026-11-12, item 8): the usual allowance, or less when the chunk
    /// itself leaves less room in the model's window; a chunk is closed only at a question, so one long pasted message
    /// makes a large one. Never below <see cref="MinimumSummaryOutputTokens"/>.
    /// </summary>
    internal static int SummaryOutputCap(ModelSnapshot model, int inputEstimate)
    {
        var budget = ContextBudget.For(model);
        var room = budget.Capacity - budget.Margin - budget.Predict(inputEstimate);
        return Math.Clamp(room, MinimumSummaryOutputTokens, SummaryOutputTokens);
    }

    /// <summary>One summary request; returns the summary, or null with the reason it cannot be used.</summary>
    private async Task<(string? Summary, string Problem)> RequestSummaryAsync(
        GenerationTaskRecord task, ChatRequest request, string apiKey, Func<AiRequestRecord, Task> record, CancellationToken stop)
    {
        var trace = new RequestTrace(task.Id, clock, request, RequestPurpose.Compression);
        var text = new StringBuilder();
        ChatStreamEvent.Completed? completed = null;
        try
        {
            await foreach (var item in client.StreamAsync(request, apiKey, stop).ConfigureAwait(false))
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
            await record(trace.Finish(stopped: true, null, null)).ConfigureAwait(false);
            throw;
        }
        catch (ChatClientException ex)
        {
            trace.Fail(ex);
            await record(trace.Finish(stopped: false, ex.Kind, ex.Message)).ConfigureAwait(false);
            throw new ContextCompressionException(T($"While compressing the earlier conversation, the request failed: {ex.Message}", $"压缩较早的对话时请求失败：{ex.Message}"), ex);
        }

        var summary = ConversationRules.WithoutInlineReasoning(text.ToString());
        var problem = completed is null ? T("the answer stream did not end normally", "回答流未正常结束")
            : completed.Filtered ? FilteredProblem
            : completed.Truncated ? T("cut off at the output limit", "达到输出上限被截断")
            : summary.Length == 0 ? T("it was empty", "内容为空")
            : null;
        await record(trace.Finish(stopped: false, problem is null ? null : GenerationErrorKind.InvalidResponse, problem is null ? null : $"Summary unusable: {problem}."))
            .ConfigureAwait(false);
        return problem is null ? (summary, string.Empty) : (null, problem);
    }
}
