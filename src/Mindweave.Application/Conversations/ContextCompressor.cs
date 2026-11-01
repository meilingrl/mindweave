using System.Text;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Diagnostics;
using Mindweave.Application.Storage;
using Mindweave.Core.Branches;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;

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
    /// with no text and one cut off after 179 characters (TRACK.md findings, 2026-10-27).
    /// </summary>
    public const int SummaryOutputTokens = 16_384;

    /// <summary>A summary that comes back cut off or empty is requested again once, within the same answer task.</summary>
    public const int SummaryAttempts = 2;

    private const string Instructions = """
        你在为一段较长的对话写背景摘要，供之后继续这段对话时使用。这不是给读者看的总结。
        请用对话所用的语言，按下面四部分写，每部分几条要点，总共不超过 600 字：
        1. 背景与已确认的事实（含具体名称、数字、年代）；
        2. 用户提出的要求、偏好与纠正——有纠正时只写纠正后的内容，并注明“已更正”；
        3. 尚未解决的问题；
        4. 指代：对话里“它”“这个”“那台车”等说法分别指什么。
        只写对话中出现过的内容，不要补充、推测或评价。
        """;

    public static PreparedContext Unsummarized(ContextPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new PreparedContext([.. plan.RawItems.Select(Turn)], []);
    }

    /// <param name="admit">Asked before each summarizing request; when it refuses, nothing more is sent.</param>
    internal async Task<PreparedContext> PrepareAsync(
        GenerationTaskRecord task, ContextPlan plan, ContextBudget budget, string apiKey, Func<AiRequestRecord, Task> record, CancellationToken stop, Func<bool>? admit = null)
    {
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

        var turns = plan.Parts.Select(part => part switch
        {
            RawPart raw => Turn(raw.Item),
            SummaryPart s => new ChatTurn(ChatRole.System, ContextPlanner.SummaryMessage(s.Chunk.Key, used[s.Chunk.Key].Text)),
            _ => throw new InvalidOperationException("Unknown context part."),
        }).ToArray();

        // Summaries were budgeted at an assumed size; a much longer one may still not fit. The margin absorbs small
        // overruns, but the output reserve is never given up.
        var estimate = TokenEstimator.PerRequest + turns.Sum(t => TokenEstimator.Message(t.Content));
        if (estimate > budget.Capacity - budget.OutputReserve)
        {
            throw new ContextCompressionException($"压缩后的上下文约 {estimate} token，仍超出模型可用容量。");
        }

        return new PreparedContext(turns, [.. used.Values.Select(s => s.Id).Distinct()]);
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

    private async Task<string> SummarizeAsync(GenerationTaskRecord task, ContextChunk chunk, string apiKey, Func<AiRequestRecord, Task> record, CancellationToken stop)
    {
        var transcript = new StringBuilder();
        foreach (var item in chunk.Items)
        {
            transcript.Append(item.Message.Role == MessageRole.User ? "用户：" : "助手：").Append(item.Text).Append("\n\n");
        }

        var request = new ChatRequest(task.Model with { MaxOutputTokens = SummaryOutputTokens },
        [
            new ChatTurn(ChatRole.System, Instructions),
            new ChatTurn(ChatRole.User, transcript.ToString()),
        ]);

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
        }

        throw new ContextCompressionException($"压缩较早的对话时服务没有返回完整摘要（{problem}），已尝试 {SummaryAttempts} 次。");
    }

    /// <summary>One summary request; returns the summary, or null with the reason it cannot be used.</summary>
    private async Task<(string? Summary, string Problem)> RequestSummaryAsync(
        GenerationTaskRecord task, ChatRequest request, string apiKey, Func<AiRequestRecord, Task> record, CancellationToken stop)
    {
        var trace = new RequestTrace(task.Id, clock);
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
            throw new ContextCompressionException($"压缩较早的对话时请求失败：{ex.Message}", ex);
        }

        var summary = text.ToString().Trim();
        var problem = completed is null ? "回答流未正常结束"
            : completed.Truncated ? "达到输出上限被截断"
            : summary.Length == 0 ? "内容为空"
            : null;
        await record(trace.Finish(stopped: false, problem is null ? null : GenerationErrorKind.InvalidResponse, problem is null ? null : $"Summary unusable: {problem}."))
            .ConfigureAwait(false);
        return problem is null ? (summary, string.Empty) : (null, problem);
    }
}
