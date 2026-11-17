using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Diagnostics;
using Mindweave.Application.Runs;
using Mindweave.Application.Storage;
using Mindweave.Application.Tools;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;

namespace Mindweave.Application.Conversations;

/// <summary>
/// The run loop (requirements §10.4.4, architecture §15.5 and §15.12): model request, tool calls, their results, the next model
/// request, until a reply calls no tool or a limit is reached; after a limit, one last request without tools. Every step is
/// recorded as it starts and as it ends, with the answer's blocks so far, in the run's own slot. A stop ends it at any step,
/// keeping what arrived. Nothing here is ever resumed: a restart marks the answer interrupted.
/// </summary>
internal sealed partial class AnswerRunner
{
    /// <summary>A tool result handed to the model whole only up to this many estimated tokens; a longer one goes as its start and a handle.</summary>
    internal const int ToolResultTokens = 2_000;

    /// <summary>After this many failures in a row, a tool is not offered again in the run.</summary>
    internal const int MaxConsecutiveFailures = 3;

    /// <summary>Characters of a tool result's preview in its block (step details).</summary>
    private const int BlockPreview = 200;

    /// <summary>What the last request of a run that reached a limit asks of the model (decision R8), added to the system turn.</summary>
    internal const string FinalInstruction = "【本次运行已达到上限】请根据上面已经取得的工具结果直接回答用户的问题，不要再调用工具；信息不足时说明还缺什么。";

    internal const string SkippedResult = "未执行：本次运行已达到工具调用次数上限。";

    internal const string PreSearchCallId = WebMaterial.PreSearchCallId;

    internal const string PreSearchLead = WebMaterial.PreSearchLead;

    internal const string DeniedResult = WebMaterial.DeniedResult;

    private readonly record struct RunEnding(MessageState State, GenerationErrorKind? Error, string? Detail);

    /// <summary>One tool turn as sent, and the line that stands for it once room runs short.</summary>
    private sealed record SentResult(int TurnIndex, string Line);

    private async Task<RunEnding> RunAsync(AnswerWork work, RunPlan run, RunServices services, ChatRequest request, string key, CancellationToken stop)
    {
        var live = work.Live;
        var task = work.Task;
        var limits = run.Limits;
        var turns = new List<ChatTurn>(request.Turns);
        var results = new List<SentResult>();
        var shortened = new HashSet<int>();
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        var withdrawn = new HashSet<string>(StringComparer.Ordinal);
        var budget = work.Budget ?? await _requests.Budgets.BudgetForAsync(task.Model, CancellationToken.None).ConfigureAwait(false);
        var elapsed = Stopwatch.StartNew();
        int requests = 0, calls = 0, tokens = 0, ordinal = 0;
        RunLimit? reached = null;

        if (run.Scope.Web is { Mode: WebSearchMode.On } web && run.Tools.FirstOrDefault(t => t.Name == WebSearchTool.ToolName) is not null
            && turns.FindLastIndex(t => t.Role == ChatRole.User) is var question and >= 0)
        {
            // 开 (decision W1): the app searches the question once before the first request; the results go with the question as
            // material, and the model decides from there.
            var query = string.IsNullOrWhiteSpace(web.Question) ? turns[question].Content : web.Question;
            var arguments = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["query"] = query.Length > WebSearchTool.MaxQuery ? query[..WebSearchTool.MaxQuery] : query });
            calls++;
            var first = await RunToolAsync(work, run, services, new ChatToolCall(PreSearchCallId, WebSearchTool.ToolName, arguments), ordinal++, failures, withdrawn, elapsed, stop).ConfigureAwait(false);
            if (first.Ending is { } ending)
            {
                return ending;
            }

            turns[question] = turns[question] with { Content = PreSearchLead + "\n" + first.Content + "\n\n" + turns[question].Content };
        }

        while (true)
        {
            reached ??= requests >= limits.ModelRequests - 1 ? RunLimit.ModelRequests
                : calls >= limits.ToolCalls ? RunLimit.ToolCalls
                : elapsed.Elapsed >= limits.Duration ? RunLimit.Duration
                : tokens >= limits.Tokens ? RunLimit.Tokens
                : null;
            if (reached is { } limit && live.Snapshot().LimitReached is null)
            {
                live.SetLimitReached(limit);
                await _writes.SaveAsync(() => Done(services.Store.SetLimitReachedAsync(task.Id, limit, CancellationToken.None))).ConfigureAwait(false);
            }

            var final = reached is not null;
            var offered = final ? [] : run.Tools.Where(t => !withdrawn.Contains(t.Name)).ToList();
            var next = Compose(request, turns, results, shortened, offered, final);
            while (ChatTurns.Estimate(next) > budget.Available && Shorten(results, shortened))
            {
                next = Compose(request, turns, results, shortened, offered, final);
            }

            if (!final && ChatTurns.Estimate(next) > budget.Available)
            {
                // No room for another round with tools: what was found is answered from.
                reached = RunLimit.Tokens;
                continue;
            }

            if (!_health.TryAdmit())
            {
                return new RunEnding(MessageState.Failed, GenerationErrorKind.StorageUnavailable, StorageAdmissionRefusedException.Detail);
            }

            // One model request.
            requests++;
            var step = new RunStepRecord(task.Id, ordinal++, RunStepKind.Model, RunStepState.Running, _clock.UtcNow);
            await _writes.SaveAsync(() => Done(services.Store.AddStepAsync(step, CancellationToken.None))).ConfigureAwait(false);
            live.BeginTurn();

            var allowed = (final ? limits.Duration + RunLimits.FinalRequestGrace : limits.Duration) - elapsed.Elapsed;
            using var deadline = new CancellationTokenSource(allowed > TimeSpan.Zero ? allowed : TimeSpan.FromMilliseconds(1));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, deadline.Token);
            var trace = new RequestTrace(task.Id, _clock, next, RequestPurpose.Answer);
            var text = new StringBuilder();
            ChatStreamEvent.ToolCalls? asked = null;
            ChatStreamEvent.Completed? end = null;
            bool stopped = false, timedOut = false;
            GenerationErrorKind? error = null;
            string? detail = null;
            try
            {
                await foreach (var item in _requests.StreamAsync(next, key, linked.Token).ConfigureAwait(false))
                {
                    trace.Observe(item);
                    switch (item)
                    {
                        case ChatStreamEvent.TextDelta delta:
                            live.Append(delta.Text);
                            text.Append(delta.Text);
                            break;
                        case ChatStreamEvent.ReasoningDelta:
                            live.MarkThinking();
                            break;
                        case ChatStreamEvent.ToolCalls toolCalls:
                            asked = toolCalls;
                            break;
                        case ChatStreamEvent.Completed done:
                            end = done;
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                stopped = true;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                timedOut = true;
            }
            catch (ChatClientException ex)
            {
                error = ex.Kind;
                detail = ex.Message;
                trace.Fail(ex);
            }
#pragma warning disable CA1031 // Anything else still ends the run and records its request (as for a single answer).
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogUnexpectedFailure(_logger, ex, task.Id);
                error = GenerationErrorKind.InvalidResponse;
                detail = UnexpectedDetail(ex);
            }

            var record = trace.Finish(stopped || timedOut, error, detail);
            await _requests.RecordAsync(record).ConfigureAwait(false);
            tokens += (record.EstimatedTokens ?? 0) + (record.CompletionTokens ?? TokenEstimator.Estimate(text.ToString()));
            var stepState = stopped || timedOut ? RunStepState.Stopped : error is not null ? RunStepState.Failed : RunStepState.Completed;
            var ended = step with { State = stepState, FinishedAt = _clock.UtcNow, AiRequestId = record.Id, ErrorKind = error, ErrorDetail = detail };
            await _writes.SaveAsync(() => services.Store.FinishStepAsync(ended, live.MessageId, null, 0, CancellationToken.None)).ConfigureAwait(false);

            if (stopped)
            {
                return new RunEnding(MessageState.Stopped, null, null);
            }

            if (timedOut)
            {
                if (!final)
                {
                    reached = RunLimit.Duration;
                    continue;
                }

                return new RunEnding(MessageState.Stopped, GenerationErrorKind.Timeout, "达到运行的时长上限，最后一次回答没有完成。");
            }

            if (error is not null)
            {
                return new RunEnding(MessageState.Failed, error, detail);
            }

            var empty = string.IsNullOrWhiteSpace(live.Snapshot().Text);
            if (end is null)
            {
                // Tool calls of a reply that never finished may be cut off: they are not run.
                return new RunEnding(MessageState.Failed, GenerationErrorKind.InvalidResponse, "The service closed the stream without finishing the answer.");
            }

            if (end.Truncated)
            {
                return new RunEnding(empty ? MessageState.Failed : MessageState.Stopped, end.Filtered ? GenerationErrorKind.ContentFiltered : GenerationErrorKind.OutputLimit, null);
            }

            if (asked is not { Calls.Count: > 0 } || final)
            {
                // The answer: a reply that calls no tool, or the last one (any calls it still made are not run).
                return !empty ? new RunEnding(MessageState.Complete, null, null)
                    : final ? new RunEnding(MessageState.Stopped, GenerationErrorKind.InvalidResponse, "达到运行的上限后，模型没有给出回答。")
                    : new RunEnding(MessageState.Failed, GenerationErrorKind.InvalidResponse, "The service finished without any text.");
            }

            turns.Add(new ChatTurn(ChatRole.Assistant, text.ToString()) { ToolCalls = asked.Calls, Reasoning = asked.Reasoning });
            foreach (var call in asked.Calls)
            {
                if (calls >= limits.ToolCalls)
                {
                    // Every call needs a result in the protocol; one beyond the limit is not run (decision R6).
                    var tool = run.Tools.FirstOrDefault(t => t.Name == call.Name);
                    live.AddToolCall(new ToolCallBlock(call.Id, call.Name, call.Arguments, tool?.Risk ?? ToolRisk.LocalRead, Summary(tool, call)));
                    live.AddToolResult(new ToolResultBlock(call.Id, ToolResultStatus.Skipped, SkippedResult, null, "未执行"));
                    turns.Add(new ChatTurn(ChatRole.Tool, SkippedResult) { ToolCallId = call.Id });
                    reached ??= RunLimit.ToolCalls;
                    continue;
                }

                calls++;
                var outcome = await RunToolAsync(work, run, services, call, ordinal++, failures, withdrawn, elapsed, stop).ConfigureAwait(false);
                if (outcome.Ending is { } ending)
                {
                    return ending;
                }

                results.Add(new SentResult(turns.Count, outcome.Line));
                turns.Add(new ChatTurn(ChatRole.Tool, outcome.Content) { ToolCallId = call.Id });
            }
        }
    }

    /// <summary>The request for the next step: the turns so far (older results shortened as needed), the tools, and for the last one the instruction to answer.</summary>
    private static ChatRequest Compose(ChatRequest request, List<ChatTurn> turns, List<SentResult> results, HashSet<int> shortened, List<ITool> offered, bool final)
    {
        var sent = new List<ChatTurn>(turns);
        foreach (var result in results.Where(r => shortened.Contains(r.TurnIndex)))
        {
            sent[result.TurnIndex] = sent[result.TurnIndex] with { Content = result.Line };
        }

        if (final && sent.Count > 0 && sent[0].Role == ChatRole.System)
        {
            // In the system turn: some services refuse a system turn anywhere but first.
            sent[0] = sent[0] with { Content = sent[0].Content + "\n\n" + FinalInstruction };
        }
        else if (final)
        {
            sent.Insert(0, new ChatTurn(ChatRole.System, FinalInstruction));
        }

        return request with { Turns = sent, Tools = offered.Count == 0 ? null : [.. offered.Select(ToolRegistry.Definition)] };
    }

    /// <summary>Shortens the oldest result still sent whole to its one line. False when none is left.</summary>
    private static bool Shorten(List<SentResult> results, HashSet<int> shortened)
    {
        var oldest = results.FirstOrDefault(r => !shortened.Contains(r.TurnIndex));
        return oldest is not null && shortened.Add(oldest.TurnIndex);
    }

    private readonly record struct ToolOutcome(string Content, string Line, RunEnding? Ending);

    /// <summary>
    /// Runs one tool call as a step: an unknown tool, invalid arguments, a withdrawn tool, a timeout or a tool's failure become an
    /// error handed back to the model; a result is stored as a resource and sent whole or as its start and handle.
    /// </summary>
    private async Task<ToolOutcome> RunToolAsync(
        AnswerWork work, RunPlan run, RunServices services, ChatToolCall call, int ordinal, Dictionary<string, int> failures, HashSet<string> withdrawn, Stopwatch elapsed, CancellationToken stop)
    {
        var live = work.Live;
        var tool = run.Tools.FirstOrDefault(t => string.Equals(t.Name, call.Name, StringComparison.Ordinal));
        JsonElement arguments = default;
        var parsed = false;
        if (call.ArgumentsValid)
        {
            try
            {
                using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
                arguments = document.RootElement.Clone();
                parsed = arguments.ValueKind == JsonValueKind.Object;
            }
            catch (JsonException)
            {
                parsed = false;
            }
        }

        var input = tool is not null && parsed ? tool.Describe(arguments) : null;
        var callBlock = new ToolCallBlock(call.Id, call.Name, call.Arguments, tool?.Risk ?? ToolRisk.LocalRead, Summary(tool, call, input));
        var step = new RunStepRecord(
            work.Task.Id, ordinal, RunStepKind.Tool, RunStepState.Running, _clock.UtcNow,
            CallId: call.Id, Tool: call.Name.Length > 0 ? call.Name : "?", Risk: tool?.Risk ?? ToolRisk.LocalRead, Input: input)
        {
            Target = tool is not null && parsed ? tool.TargetOf(arguments) : null,
        };
        await _writes.SaveAsync(() => Done(services.Store.AddStepAsync(step, CancellationToken.None))).ConfigureAwait(false);
        live.StartTool(callBlock, call.Name);

        // Q10 (decision W10): a call the user must allow waits for the card, without its slot and without counting time.
        var asks = tool is not null && parsed && !withdrawn.Contains(tool.Name) ? tool.ApprovalFor(run.Scope, arguments) : null;
        if (asks is not null)
        {
            var decided = await AwaitApprovalAsync(work, services, step, call, asks, elapsed, stop).ConfigureAwait(false);
            if (decided is null)
            {
                return new ToolOutcome(string.Empty, string.Empty, new RunEnding(MessageState.Stopped, null, null));
            }

            step = step with { Approval = decided.Value ? Approval.Approved : Approval.Denied };
            if (!decided.Value)
            {
                var refused = new ToolResultBlock(call.Id, ToolResultStatus.Denied, DeniedResult, null, "你拒绝了读取");
                live.AddToolResult(refused);
                var shot = live.Snapshot();
                var deniedStep = step with { State = RunStepState.Denied, FinishedAt = _clock.UtcNow };
                await _writes.SaveAsync(() => services.Store.FinishStepAsync(deniedStep, live.MessageId, shot.Blocks, shot.Revision, CancellationToken.None)).ConfigureAwait(false);
                return new ToolOutcome(DeniedResult, $"（较早的工具调用：{call.Name} {input}，用户拒绝。）", null);
            }
        }

        string? failure = null;
        var failureKind = GenerationErrorKind.InvalidResponse;
        ToolOutput? output = null;
        if (tool is null)
        {
            failure = $"没有名为“{call.Name}”的工具。可用的工具：{string.Join("、", run.Tools.Where(t => !withdrawn.Contains(t.Name)).Select(t => t.Name))}。";
        }
        else if (withdrawn.Contains(tool.Name))
        {
            failure = "这个工具在本次运行中连续出错，已不再提供。";
        }
        else if (!parsed)
        {
            failure = "参数不是合法的 JSON 对象，请按工具的输入格式重新给出。";
        }
        else
        {
            using var timeout = new CancellationTokenSource(tool.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, timeout.Token);
            try
            {
                output = await tool.ExecuteAsync(run.Scope, arguments, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                live.EndTool();
                var stoppedStep = step with { State = RunStepState.Stopped, FinishedAt = _clock.UtcNow };
                await _writes.SaveAsync(() => services.Store.FinishStepAsync(stoppedStep, live.MessageId, null, 0, CancellationToken.None)).ConfigureAwait(false);
                return new ToolOutcome(string.Empty, string.Empty, new RunEnding(MessageState.Stopped, null, null));
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                failure = string.Create(CultureInfo.InvariantCulture, $"超时（{tool.Timeout.TotalSeconds:0} 秒内没有完成）。");
                failureKind = GenerationErrorKind.Timeout;
            }
            catch (ToolInputException ex)
            {
                failure = ex.Message;
            }
            catch (ToolFailureException ex)
            {
                failure = ex.Message;
            }
            catch (StorageWriteException ex)
            {
                failure = "读取本地数据失败：" + ex.Message;
            }
#pragma warning disable CA1031 // A tool's failure is handed back to the model; it never ends the run (architecture §15.10).
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogToolFailed(_logger, ex, call.Name);
                failure = "工具出错：" + ex.Message;
            }
        }

        ResourceId? stored = null;
        if (output is not null)
        {
            try
            {
                stored = await services.StoreResultAsync(run.Scope, $"{tool!.DisplayName}：{input}".TrimEnd('：'), output, CancellationToken.None).ConfigureAwait(false);
            }
            catch (StorageWriteException ex)
            {
                live.EndTool();
                var failedStep = step with { State = RunStepState.Failed, FinishedAt = _clock.UtcNow, ErrorKind = GenerationErrorKind.StorageUnavailable, ErrorDetail = ex.Message };
                await _writes.SaveAsync(() => services.Store.FinishStepAsync(failedStep, live.MessageId, null, 0, CancellationToken.None)).ConfigureAwait(false);
                return new ToolOutcome(string.Empty, string.Empty, new RunEnding(MessageState.Failed, GenerationErrorKind.StorageUnavailable, ex.Message));
            }
        }

        string content;
        ToolResultBlock block;
        if (output is not null && stored is { } id)
        {
            failures[call.Name] = 0;
            content = TokenEstimator.Estimate(output.Text) <= ToolResultTokens
                ? output.Text
                : Start(output.Text, ToolResultTokens) + $"\n\n（结果较长，以上只是开头。完整结果是资源 {id}，可用 {SearchAttachmentsTool.ToolName} 的 resource 与 part 读取。）";
            if (output.Provenance == ResourceProvenance.External)
            {
                // Outside material is never an instruction (architecture §15.8, decision W11).
                content = WebMaterial.Wrap(content);
            }
            block = new ToolResultBlock(call.Id, ToolResultStatus.Succeeded, Preview(output.Text), id, output.Detail, output.Findings);
        }
        else
        {
            failure ??= "工具没有返回结果。";
            var count = failures[call.Name] = failures.GetValueOrDefault(call.Name) + 1;
            if (tool is not null && count >= MaxConsecutiveFailures)
            {
                withdrawn.Add(tool.Name);
            }

            content = "出错：" + failure;
            block = new ToolResultBlock(call.Id, ToolResultStatus.Failed, failure, null, failureKind == GenerationErrorKind.Timeout ? "超时" : "出错");
        }

        live.AddToolResult(block);
        var snapshot = live.Snapshot();
        var finished = step with
        {
            State = output is not null ? RunStepState.Completed : RunStepState.Failed,
            FinishedAt = _clock.UtcNow,
            Result = stored,
            ErrorKind = output is not null ? null : failureKind,
            ErrorDetail = output is not null ? null : failure,
        };
        await _writes.SaveAsync(() => services.Store.FinishStepAsync(finished, live.MessageId, snapshot.Blocks, snapshot.Revision, CancellationToken.None)).ConfigureAwait(false);
        var line = $"（较早的工具结果已省略：{call.Name} {input}{(stored is { } r ? $"，完整结果是资源 {r}" : string.Empty)}；需要时可再读取。）";
        return new ToolOutcome(content, line, null);
    }

    /// <summary>
    /// Shows the approval card and waits for the user (decision W10): the step is recorded as awaiting approval, the run gives
    /// its slot up, and the wait does not count towards its duration. Returns the decision, or null when the run was stopped
    /// meanwhile (the step is then recorded as stopped and the card withdrawn).
    /// </summary>
    private async Task<bool?> AwaitApprovalAsync(AnswerWork work, RunServices services, RunStepRecord step, ChatToolCall call, string asks, Stopwatch elapsed, CancellationToken stop)
    {
        var live = work.Live;
        await _writes.SaveAsync(() => Done(services.Store.SetApprovalAsync(step.Task, step.Ordinal, RunStepState.AwaitingApproval, Approval.Pending, CancellationToken.None))).ConfigureAwait(false);
        var decision = live.RequestApproval(new PendingApproval(call.Id, call.Name, asks));
        elapsed.Stop();
        Slots?.Release(work.Task.Id);
        try
        {
            var allowed = await decision.WaitAsync(stop).ConfigureAwait(false);
            if (Slots is { } slots)
            {
                await slots.ReacquireAsync(work.Task.Id, stop).ConfigureAwait(false);
            }

            await _writes.SaveAsync(() => Done(services.Store.SetApprovalAsync(
                step.Task, step.Ordinal, allowed ? RunStepState.Running : RunStepState.Denied, allowed ? Approval.Approved : Approval.Denied, CancellationToken.None))).ConfigureAwait(false);
            return allowed;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            live.WithdrawApproval();
            live.EndTool();
            var stopped = step with { State = RunStepState.Stopped, FinishedAt = _clock.UtcNow };
            await _writes.SaveAsync(() => services.Store.FinishStepAsync(stopped, live.MessageId, null, 0, CancellationToken.None)).ConfigureAwait(false);
            return null;
        }
        finally
        {
            elapsed.Start();
        }
    }

    private static string Summary(ITool? tool, ChatToolCall call, string? input = null) =>
        tool is null ? $"{(call.Name.Length > 0 ? call.Name : "?")}（不存在的工具）"
        : string.IsNullOrEmpty(input) ? tool.DisplayName
        : $"{tool.DisplayName}：{input}";

    private static string Preview(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= BlockPreview ? flat : flat[..BlockPreview] + "…";
    }

    /// <summary>The start of <paramref name="text"/> within about <paramref name="tokens"/> estimated tokens.</summary>
    internal static string Start(string text, int tokens)
    {
        var length = Math.Min(text.Length, (int)((long)text.Length * tokens / Math.Max(1, TokenEstimator.Estimate(text))));
        while (length > 0 && TokenEstimator.Estimate(text[..length]) > tokens)
        {
            length = length * 9 / 10;
        }

        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }

    private static async Task<bool> Done(Task write)
    {
        await write.ConfigureAwait(false);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool {Tool} failed; the error went back to the model")]
    private static partial void LogToolFailed(ILogger logger, Exception exception, string tool);
}
