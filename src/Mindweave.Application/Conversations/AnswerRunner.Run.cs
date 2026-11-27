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
using static Mindweave.Application.Languages.Bilingual;

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
    internal static string FinalInstruction => T(
        "[This run has reached its limit] Answer the user's question directly from the tool results already obtained; do not call any more tools. If the information is not enough, say what is missing.",
        "【本次运行已达到上限】请根据已经取得的工具结果直接回答用户的问题，不要再调用工具；信息不足时说明还缺什么。");

    /// <summary>
    /// What the last request asks when a middle round was cut off at its length limit (L6): its calls were not run, and the
    /// run goes straight to its last request.
    /// </summary>
    internal static string CutOffInstruction => T(
        "[The previous reply was cut off at the output length limit, and its tool calls were not run] Do not call any more tools; answer the user's question directly and briefly from the tool results already obtained. If the information is not enough, say what is missing.",
        "【上一轮回复因输出长度上限被截断，其中的工具调用没有执行】请不要再调用工具，根据已经取得的工具结果直接、简洁地回答用户的问题；信息不足时说明还缺什么。");

    /// <summary>The longest a retry waits for a service that asked to slow down (L5).</summary>
    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(20);

    internal static string SkippedResult => T("Not run: this run has reached its limit on tool calls.", "未执行：本次运行已达到工具调用次数上限。");

    internal const string PreSearchCallId = WebMaterial.PreSearchCallId;

    internal static string PreSearchLead => WebMaterial.PreSearchLead;

    internal static string DeniedResult => WebMaterial.DeniedResult;

    private readonly record struct RunEnding(MessageState State, GenerationErrorKind? Error, string? Detail);

    /// <summary>One tool turn as sent, and the line that stands for it once room runs short.</summary>
    private sealed record SentResult(int TurnIndex, string Line)
    {
        public string Tool { get; init; } = string.Empty;

        /// <summary>Failed or refused: nothing in it to answer from.</summary>
        public bool Failed { get; init; }

        public IReadOnlyList<ToolFinding> Findings { get; init; } = [];

        /// <summary>The run's [n] of the sources in it.</summary>
        public IReadOnlyList<int> Numbers { get; init; } = [];

        /// <summary>Only its start was sent.</summary>
        public bool Cut { get; init; }
    }

    private static SentResult Sent(int turnIndex, ToolOutcome outcome, string tool) =>
        new(turnIndex, outcome.Line) { Tool = tool, Failed = outcome.Failed, Findings = outcome.Findings ?? [], Numbers = outcome.Numbers ?? [], Cut = outcome.Cut };

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
        var budget = work.RunBudget ?? work.Budget ?? await _requests.Budgets.BudgetForAsync(task.Model, CancellationToken.None).ConfigureAwait(false);
        var elapsed = Stopwatch.StartNew();
        int requests = 0, calls = 0, tokens = 0, ordinal = 0;
        RunLimit? reached = null;
        var cutOff = false;
        var preSearchCut = false;
        var seen = new Dictionary<string, EarlierCall>(StringComparer.Ordinal);
        var retried = false;
        var answer = work.Inputs?.Instructions ?? string.Empty;
        var instructions = AnswerPrompt.WithRun(answer, RunPrompt.For(run.Tools));
        if (run.Tools.Any(t => t.Name == SearchAttachmentsTool.ToolName)
            && await services.EarlierMaterialAsync(run.Scope, CancellationToken.None).ConfigureAwait(false) is { } index)
        {
            // L10: what earlier runs found, before anything of this run joins the scope.
            instructions += "\n\n" + index;
        }

        if (run.Scope.Web is { Mode: WebSearchMode.On } web && run.Tools.FirstOrDefault(t => t.Name == WebSearchTool.ToolName) is not null
            && turns.FindLastIndex(t => t.Role == ChatRole.User) is var question and >= 0)
        {
            // 开 (decision W1): the app searches the question once before the first request; the results go with the question as
            // material, and the model decides from there.
            var query = web.PreSearch is { Length: > 0 } terms ? terms : string.IsNullOrWhiteSpace(web.Question) ? turns[question].Content : web.Question;
            var arguments = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["query"] = query.Length > WebSearchTool.MaxQuery ? query[..WebSearchTool.MaxQuery] : query });
            calls++;
            var first = await RunToolAsync(work, run, services, new ChatToolCall(PreSearchCallId, WebSearchTool.ToolName, arguments), ordinal++, failures, withdrawn, elapsed, stop).ConfigureAwait(false);
            if (first.Ending is { } ending)
            {
                return ending;
            }

            preSearchCut = first.Cut;
            turns[question] = turns[question] with { Content = PreSearchLead + "\n" + first.Content + "\n\n" + turns[question].Content };
            if (first.Resource is { } found && RepeatKey(run, new ChatToolCall(PreSearchCallId, WebSearchTool.ToolName, arguments)) is { } searched)
            {
                // Never shortened: it went with the question.
                seen[searched] = new EarlierCall(first.Content, -1, found, WebSearchTool.ToolName, query.Length > WebSearchTool.MaxQuery ? query[..WebSearchTool.MaxQuery] : query);
            }
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

            var final = reached is not null || cutOff;
            var next = Next(final);
            while (ChatTurns.Estimate(next) > budget.Available && Shorten(results, shortened, live.Snapshot().Text))
            {
                next = Next(final);
            }

            if (!final && ChatTurns.Estimate(next) > budget.Available)
            {
                // No room for another round with tools: what was found is answered from.
                reached = RunLimit.Tokens;
                continue;
            }

            if (!_health.TryAdmit())
            {
                return new RunEnding(MessageState.Failed, GenerationErrorKind.StorageUnavailable, StorageAdmissionRefusedException.Detail.ToString());
            }

            // One model request.
            requests++;
            var step = new RunStepRecord(task.Id, ordinal++, RunStepKind.Model, RunStepState.Running, _clock.UtcNow);
            await _writes.SaveAsync(() => Done(services.Store.AddStepAsync(step, CancellationToken.None))).ConfigureAwait(false);
            live.BeginTurn(final);

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
            ChatClientException? failure = null;
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
                        case ChatStreamEvent.ReasoningDelta reasoning:
                            live.AppendReasoning(reasoning.Text);
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
                failure = ex;
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

                return new RunEnding(MessageState.Stopped, GenerationErrorKind.Timeout, T("The run reached its time limit, and the last answer was not finished.", "达到运行的时长上限，最后一次回答没有完成。"));
            }

            if (error is not null)
            {
                if (!retried && requests < limits.ModelRequests && calls > 0 && text.Length == 0 && asked is null && failure is { IsTransient: true })
                {
                    // L5: a request after tool results that failed by chance is tried once more (a step of its own), so the
                    // results so far are not lost; nothing of it had arrived. It counts as a request and towards the duration,
                    // so the last request allowed is not tried again.
                    retried = true;
                    live.SetPhase(AnswerPhase.Retrying);
                    var pause = failure.RetryAfter is { } after ? (after < MaxRetryAfter ? after : MaxRetryAfter) : _options.RetryPause;
                    try
                    {
                        await Task.Delay(pause, stop).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        return new RunEnding(MessageState.Stopped, null, null);
                    }

                    continue;
                }

                return new RunEnding(MessageState.Failed, error, detail);
            }

            retried = false;
            var empty = string.IsNullOrWhiteSpace(live.Snapshot().Text);
            if (end is null)
            {
                // Tool calls of a reply that never finished may be cut off: they are not run.
                return new RunEnding(MessageState.Failed, GenerationErrorKind.InvalidResponse, "The service closed the stream without finishing the answer.");
            }

            if (end.Truncated && !end.Filtered && !final && (asked is { Calls.Count: > 0 } || string.IsNullOrWhiteSpace(text.ToString())))
            {
                // L6: a middle round cut off at its length limit while calling tools or before writing anything (a reasoning
                // model spends its output on thinking) does not end the run: its calls may be cut off and are not run, what it
                // wrote stays in the answer (not sent again: a request never ends with the model's own turn), and the last
                // request answers. A round cut off while writing its answer ends the run as before.
                cutOff = true;
                continue;
            }

            if (end.Truncated)
            {
                return new RunEnding(empty ? MessageState.Failed : MessageState.Stopped, end.Filtered ? GenerationErrorKind.ContentFiltered : GenerationErrorKind.OutputLimit, null);
            }

            if (asked is not { Calls.Count: > 0 } || final)
            {
                // The answer: a reply that calls no tool, or the last one (any calls it still made are not run).
                return !empty ? new RunEnding(MessageState.Complete, null, null)
                    : final ? new RunEnding(MessageState.Stopped, GenerationErrorKind.InvalidResponse, T("After the run reached its limit, the model gave no answer.", "达到运行的上限后，模型没有给出回答。"))
                    : new RunEnding(MessageState.Failed, GenerationErrorKind.InvalidResponse, "The service finished without any text.");
            }

            turns.Add(new ChatTurn(ChatRole.Assistant, text.ToString()) { ToolCalls = asked.Calls, Reasoning = asked.Reasoning });
            var batch = new List<(ChatToolCall Call, int Ordinal)>();
            var repeats = new List<(ChatToolCall Call, int Ordinal, string Key)>();
            var skipped = new List<ChatToolCall>();
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var call in asked.Calls)
            {
                if (calls >= limits.ToolCalls)
                {
                    // Every call needs a result in the protocol; one beyond the limit is not run (decision R6).
                    skipped.Add(call);
                    reached ??= RunLimit.ToolCalls;
                    continue;
                }

                calls++;
                var same = RepeatKey(run, call);
                if (same is not null && (seen.ContainsKey(same) || keys.ContainsValue(same)))
                {
                    // L4: the same read-only call again is not run; it still counts as a call.
                    repeats.Add((call, ordinal++, same));
                    continue;
                }

                if (same is not null)
                {
                    keys[call.Id] = same;
                }

                batch.Add((call, ordinal++));
            }

            var outcomes = await RunToolsAsync(work, run, services, batch, failures, withdrawn, elapsed, stop).ConfigureAwait(false);
            for (var i = 0; i < outcomes.Count; i++)
            {
                if (outcomes[i].Ending is { } ending)
                {
                    return ending;
                }

                if (outcomes[i].Resource is { } resource && keys.TryGetValue(batch[i].Call.Id, out var made))
                {
                    var tool = run.Tools.First(t => t.Name == batch[i].Call.Name);
                    using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(batch[i].Call.Arguments) ? "{}" : batch[i].Call.Arguments);
                    seen.TryAdd(made, new EarlierCall(outcomes[i].Content, turns.Count, resource, tool.Name, tool.Describe(document.RootElement)));
                }

                results.Add(Sent(turns.Count, outcomes[i], batch[i].Call.Name));
                turns.Add(new ChatTurn(ChatRole.Tool, outcomes[i].Content) { ToolCallId = batch[i].Call.Id });
            }

            foreach (var (call, at, same) in repeats)
            {
                if (seen.TryGetValue(same, out var earlier))
                {
                    var repeated = await RepeatAsync(work, run, services, call, at, earlier, shortened.Contains(earlier.TurnIndex)).ConfigureAwait(false);
                    results.Add(Sent(turns.Count, repeated, call.Name));
                    turns.Add(new ChatTurn(ChatRole.Tool, repeated.Content) { ToolCallId = call.Id });
                }
                else
                {
                    // Its twin in this reply failed: this one runs after all.
                    var outcome = await RunToolAsync(work, run, services, call, at, failures, withdrawn, elapsed, stop).ConfigureAwait(false);
                    if (outcome.Ending is { } ending)
                    {
                        return ending;
                    }

                    results.Add(Sent(turns.Count, outcome, call.Name));
                    turns.Add(new ChatTurn(ChatRole.Tool, outcome.Content) { ToolCallId = call.Id });
                }
            }

            foreach (var call in skipped)
            {
                var tool = run.Tools.FirstOrDefault(t => t.Name == call.Name);
                live.AddToolCall(new ToolCallBlock(call.Id, call.Name, call.Arguments, tool?.Risk ?? ToolRisk.LocalRead, Summary(tool, call)));
                live.AddToolResult(new ToolResultBlock(call.Id, ToolResultStatus.Skipped, SkippedResult, null, T("Not run", "未执行")));
                turns.Add(new ChatTurn(ChatRole.Tool, SkippedResult) { ToolCallId = call.Id });
            }
        }

        ChatRequest Next(bool final)
        {
            if (run.Reader is { } reader && (preSearchCut || shortened.Count > 0 || results.Any(r => r.Cut)))
            {
                // A result not sent whole names 检索附件 for the rest, so it is offered from here on (requirements §10.4.4).
                run = run with { Tools = [.. run.Tools, reader], Reader = null };
                instructions = AnswerPrompt.WithRun(answer, RunPrompt.For(run.Tools));
            }

            var offered = final ? [] : run.Tools.Where(t => !withdrawn.Contains(t.Name)).ToList();

            // The run instructions (L1) end with the budget line (L2), or for the last request with the instruction to answer (R8, L6).
            var system = instructions + "\n\n" + (reached is not null ? FinalInstruction : cutOff ? CutOffInstruction : RunPrompt.BudgetLine(limits, requests, calls));
            return Compose(request, turns, results, shortened, offered, system);
        }
    }

    /// <summary>
    /// The request for the next step: the run's system turn first (its instructions and budget line, or the instruction to
    /// answer), then the turns so far (older results shortened as needed), and the tools.
    /// </summary>
    private static ChatRequest Compose(ChatRequest request, List<ChatTurn> turns, List<SentResult> results, HashSet<int> shortened, List<ITool> offered, string system)
    {
        var sent = new List<ChatTurn>(turns);
        foreach (var result in results.Where(r => shortened.Contains(r.TurnIndex)))
        {
            sent[result.TurnIndex] = sent[result.TurnIndex] with { Content = result.Line };
        }

        return request with { Turns = Opened(sent, system), Tools = offered.Count == 0 ? null : [.. offered.Select(ToolRegistry.Definition)] };
    }

    /// <summary>
    /// Shortens one result still sent whole to its one line (L7), the least useful first: failed and refused results; search
    /// results the answer does not cite and whose pages were not read; local and other results, and search results whose
    /// pages were read; pages; last, results with a source the answer so far cites ([n]). Oldest first within each. False
    /// when none is left.
    /// </summary>
    private static bool Shorten(List<SentResult> results, HashSet<int> shortened, string answer)
    {
        var cited = CitationMarkers.Find(answer).Select(m => m.Number).ToHashSet();
        var read = results.Where(r => r.Tool == FetchPageTool.ToolName).SelectMany(r => r.Findings).Select(f => Address(f.Id)).ToHashSet(StringComparer.Ordinal);
        var next = results.Where(r => !shortened.Contains(r.TurnIndex)).OrderBy(r => Rank(r, cited, read)).ThenBy(r => r.TurnIndex).FirstOrDefault();
        return next is not null && shortened.Add(next.TurnIndex);
    }

    private static int Rank(SentResult result, HashSet<int> cited, HashSet<string> read)
    {
        var sources = result.Findings.Where(f => f.Kind == ToolFindingKind.WebSource).ToList();
        return result.Failed ? 0
            : result.Numbers.Any(cited.Contains) || sources.Any(f => f.Part is { } n && cited.Contains(n)) ? 4
            : result.Tool == FetchPageTool.ToolName ? 3
            : result.Tool == WebSearchTool.ToolName && !sources.Any(f => read.Contains(Address(f.Id))) ? 1
            : 2;
    }

    private static string Address(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && WebRun.Normalize(u) is { } key ? key : url;

    private readonly record struct ToolOutcome(string Content, string Line, RunEnding? Ending, ResourceId? Resource = null, bool Failed = false, IReadOnlyList<ToolFinding>? Findings = null, IReadOnlyList<int>? Numbers = null, bool Cut = false);

    /// <summary>A read-only call that succeeded earlier in the run (L4): what was sent for it and where.</summary>
    private sealed record EarlierCall(string Content, int TurnIndex, ResourceId Resource, string Name, string? Input);

    /// <summary>What the model is told for a call that repeats an earlier one whose result it still has (L4).</summary>
    internal static string RepeatedResult(string name, string? input, ResourceId resource) =>
        T($"This call is the same as an earlier one in this run ({name} {input}), so it was not run again; see the result above (resource {resource}).",
            $"这次调用与本次运行中较早的一次相同（{name} {input}），没有再执行；结果见上（资源 {resource}）。");

    /// <summary>The key a read-only call is compared by (L4); null for a call that is always run.</summary>
    private static string? RepeatKey(RunPlan run, ChatToolCall call)
    {
        if (run.Tools.FirstOrDefault(t => t.Name == call.Name) is not { } tool || tool.Risk == ToolRisk.SideEffect || !call.ArgumentsValid)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
            return document.RootElement.ValueKind == JsonValueKind.Object ? call.Name + "\n" + ToolArguments.Canonical(document.RootElement) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A repeated call (L4) as a step of its own that runs nothing: it counts as a call, its block points to the earlier
    /// result, and the model gets that result again only when it was shortened meanwhile.
    /// </summary>
    private async Task<ToolOutcome> RepeatAsync(AnswerWork work, RunPlan run, RunServices services, ChatToolCall call, int ordinal, EarlierCall earlier, bool shortenedSince)
    {
        var live = work.Live;
        var tool = run.Tools.First(t => t.Name == call.Name);
        var step = new RunStepRecord(
            work.Task.Id, ordinal, RunStepKind.Tool, RunStepState.Running, _clock.UtcNow,
            CallId: call.Id, Tool: call.Name, Risk: tool.Risk, Input: earlier.Input);
        await _writes.SaveAsync(() => Done(services.Store.AddStepAsync(step, CancellationToken.None))).ConfigureAwait(false);
        live.StartTool(new ToolCallBlock(call.Id, call.Name, call.Arguments, tool.Risk, Summary(tool, call, earlier.Input)), call.Name);
        live.AddToolResult(new ToolResultBlock(call.Id, ToolResultStatus.Succeeded, T("The same as an earlier call, so it was not run again.", "与较早的一次调用相同，没有再执行。"), earlier.Resource, T("Repeated call, not run again", "重复的调用，未再执行")));
        var snapshot = live.Snapshot();
        var finished = step with { State = RunStepState.Completed, FinishedAt = _clock.UtcNow, Result = earlier.Resource };
        await _writes.SaveAsync(() => services.Store.FinishStepAsync(finished, live.MessageId, snapshot.Blocks, snapshot.Revision, CancellationToken.None)).ConfigureAwait(false);
        var content = shortenedSince ? earlier.Content : RepeatedResult(earlier.Name, earlier.Input, earlier.Resource);
        return new ToolOutcome(content, T($"(Earlier tool call: {call.Name} {earlier.Input}, the same as a call before it, not run again.)", $"（较早的工具调用：{call.Name} {earlier.Input}，与之前的一次相同，没有再执行。）"), null, earlier.Resource);
    }

    /// <summary>At most this many calls of one reply run at once (L3).</summary>
    internal const int MaxParallelCalls = 4;

    /// <summary>One tool call between its start, its execution and its result.</summary>
    private sealed class PendingCall(ChatToolCall call, ITool? tool, RunStepRecord step, string? input)
    {
        public ChatToolCall Call { get; } = call;

        public ITool? Tool { get; } = tool;

        public RunStepRecord Step { get; set; } = step;

        public string? Input { get; } = input;

        public JsonElement Arguments { get; init; }

        public bool Parsed { get; init; }

        /// <summary>Why it did not run (or failed), handed back to the model.</summary>
        public string? Failure { get; set; }

        public GenerationErrorKind FailureKind { get; set; } = GenerationErrorKind.InvalidResponse;

        /// <summary>A side effect whose outcome is not known (decision M17).</summary>
        public bool Unknown { get; set; }

        public ToolOutput? Output { get; set; }

        /// <summary>Settled before execution: refused by the user, or the run was stopped.</summary>
        public ToolOutcome? Settled { get; set; }
    }

    /// <summary>
    /// Runs one tool call as a step: an unknown tool, invalid arguments, a withdrawn tool, a timeout or a tool's failure become an
    /// error handed back to the model; a result is stored as a resource and sent whole or as its start and handle.
    /// </summary>
    private async Task<ToolOutcome> RunToolAsync(
        AnswerWork work, RunPlan run, RunServices services, ChatToolCall call, int ordinal, Dictionary<string, int> failures, HashSet<string> withdrawn, Stopwatch elapsed, CancellationToken stop)
    {
        var pending = await BeginToolAsync(work, run, services, call, ordinal, withdrawn, elapsed, stop).ConfigureAwait(false);
        await ExecuteToolAsync(work, run, services, pending, stop).ConfigureAwait(false);
        return await FinishToolAsync(work, run, services, pending, failures, withdrawn).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the calls of one reply (L3): when none may have a side effect, they start in order (each asking for approval in
    /// turn when it must), then run at once, at most <see cref="MaxParallelCalls"/>, and their results are taken in order;
    /// otherwise one after another, as a side effect's order may matter. Returns the outcomes in call order; it stops at
    /// the first that ends the run.
    /// </summary>
    private async Task<List<ToolOutcome>> RunToolsAsync(
        AnswerWork work, RunPlan run, RunServices services, List<(ChatToolCall Call, int Ordinal)> batch, Dictionary<string, int> failures, HashSet<string> withdrawn, Stopwatch elapsed, CancellationToken stop)
    {
        var outcomes = new List<ToolOutcome>();
        var readOnly = batch.Count > 1 && batch.All(b => run.Tools.FirstOrDefault(t => t.Name == b.Call.Name) is not { Risk: ToolRisk.SideEffect });
        if (!readOnly)
        {
            foreach (var (call, ordinal) in batch)
            {
                var outcome = await RunToolAsync(work, run, services, call, ordinal, failures, withdrawn, elapsed, stop).ConfigureAwait(false);
                outcomes.Add(outcome);
                if (outcome.Ending is not null)
                {
                    break;
                }
            }

            return outcomes;
        }

        var started = new List<PendingCall>();
        foreach (var (call, ordinal) in batch)
        {
            var pending = await BeginToolAsync(work, run, services, call, ordinal, withdrawn, elapsed, stop).ConfigureAwait(false);
            started.Add(pending);
            if (pending.Settled is { Ending: not null })
            {
                break;
            }
        }

        using (var gate = new SemaphoreSlim(MaxParallelCalls))
        {
            await Task.WhenAll(started.Select(async pending =>
            {
                // Not cancelled by a stop: every started call settles (a stopped one records its step as stopped).
                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await ExecuteToolAsync(work, run, services, pending, stop).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }

                // Its result is written with the others, in call order; meanwhile the status names a call still running.
                work.Live.ToolExecuted(pending.Call.Id);
            })).ConfigureAwait(false);
        }

        foreach (var pending in started)
        {
            var outcome = await FinishToolAsync(work, run, services, pending, failures, withdrawn).ConfigureAwait(false);
            if (outcome.Ending is not null && outcomes.Any(o => o.Ending is not null))
            {
                continue;
            }

            outcomes.Add(outcome);
        }

        // A stop ends the run however many calls had finished.
        var ending = outcomes.FirstOrDefault(o => o.Ending is not null);
        return ending.Ending is null ? outcomes : [.. outcomes.TakeWhile(o => o.Ending is null), ending];
    }

    /// <summary>A call's start: its step and block, its arguments read, and when it must, the user's approval.</summary>
    private async Task<PendingCall> BeginToolAsync(
        AnswerWork work, RunPlan run, RunServices services, ChatToolCall call, int ordinal, HashSet<string> withdrawn, Stopwatch elapsed, CancellationToken stop)
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
        var pending = new PendingCall(call, tool, step, input) { Arguments = arguments, Parsed = parsed };

        // Q10 (decision W10): a call the user must allow waits for the card, without its slot and without counting time.
        var asks = tool is not null && parsed && !withdrawn.Contains(tool.Name) ? tool.ApprovalFor(run.Scope, arguments) : null;
        string? unprepared = null;
        if (asks is not null)
        {
            // Nobody is asked to approve a call that cannot run (decision M6: a changed tool, a server that does not start).
            using var preparing = new CancellationTokenSource(tool!.Timeout);
            using var either = CancellationTokenSource.CreateLinkedTokenSource(stop, preparing.Token);
            try
            {
                await tool.PrepareAsync(run.Scope, either.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                live.EndTool();
                var stoppedStep = step with { State = RunStepState.Stopped, FinishedAt = _clock.UtcNow };
                await _writes.SaveAsync(() => services.Store.FinishStepAsync(stoppedStep, live.MessageId, null, 0, CancellationToken.None)).ConfigureAwait(false);
                pending.Settled = new ToolOutcome(string.Empty, string.Empty, new RunEnding(MessageState.Stopped, null, null));
                return pending;
            }
            catch (OperationCanceledException) when (preparing.IsCancellationRequested)
            {
                unprepared = T(
                    string.Create(CultureInfo.InvariantCulture, $"Timed out (not ready within {tool.Timeout.TotalSeconds:0} seconds)."),
                    string.Create(CultureInfo.InvariantCulture, $"超时（{tool.Timeout.TotalSeconds:0} 秒内没有准备好）。"));
            }
            catch (ToolFailureException ex)
            {
                unprepared = ex.Message;
            }
#pragma warning disable CA1031 // As for execution: a tool's failure is handed back to the model.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogToolFailed(_logger, ex, call.Name);
                unprepared = T("The tool failed: ", "工具出错：") + ex.Message;
            }

            if (unprepared is not null)
            {
                asks = null;
            }
        }

        if (asks is not null)
        {
            var decided = await AwaitApprovalAsync(work, services, step, tool!.DescribeApproval(call.Id, asks), elapsed, stop).ConfigureAwait(false);
            if (decided is null)
            {
                pending.Settled = new ToolOutcome(string.Empty, string.Empty, new RunEnding(MessageState.Stopped, null, null));
                return pending;
            }

            step = pending.Step = step with { Approval = decided.Value ? Approval.Approved : Approval.Denied };
            if (!decided.Value)
            {
                var refused = new ToolResultBlock(call.Id, ToolResultStatus.Denied, tool.DeniedResult, null, tool.DeniedDetail);
                live.AddToolResult(refused);
                var shot = live.Snapshot();
                var deniedStep = step with { State = RunStepState.Denied, FinishedAt = _clock.UtcNow };
                await _writes.SaveAsync(() => services.Store.FinishStepAsync(deniedStep, live.MessageId, shot.Blocks, shot.Revision, CancellationToken.None)).ConfigureAwait(false);
                pending.Settled = new ToolOutcome(tool.DeniedResult, T($"(Earlier tool call: {call.Name} {input}, refused by the user.)", $"（较早的工具调用：{call.Name} {input}，用户拒绝。）"), null, Failed: true);
                return pending;
            }
        }

        if (tool is null)
        {
            var available = run.Tools.Where(t => !withdrawn.Contains(t.Name)).Select(t => t.Name).ToList();
            pending.Failure = T(
                $"There is no tool named \"{call.Name}\". Available tools: {string.Join(", ", available)}.",
                $"没有名为“{call.Name}”的工具。可用的工具：{string.Join("、", available)}。");
        }
        else if (withdrawn.Contains(tool.Name))
        {
            pending.Failure = T("This tool failed several times in a row in this run and is no longer offered.", "这个工具在本次运行中连续出错，已不再提供。");
        }
        else if (!parsed)
        {
            pending.Failure = T("The arguments are not a valid JSON object. Give them again in the tool's input format.", "参数不是合法的 JSON 对象，请按工具的输入格式重新给出。");
        }
        else if (unprepared is not null)
        {
            pending.Failure = unprepared;
        }

        return pending;
    }

    /// <summary>
    /// A started call's execution, within its time limit; a stop settles it and records its step as stopped, or, for a call
    /// with side effects, as unknown: it may have taken effect (decision D-038).
    /// </summary>
    private async Task ExecuteToolAsync(AnswerWork work, RunPlan run, RunServices services, PendingCall pending, CancellationToken stop)
    {
        if (pending.Settled is not null || pending.Failure is not null || pending.Tool is not { } tool)
        {
            return;
        }

        var live = work.Live;
        using var timeout = new CancellationTokenSource(tool.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, timeout.Token);
        try
        {
            pending.Output = await tool.ExecuteAsync(run.Scope, pending.Arguments, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            live.EndTool();
            var state = tool.Risk == ToolRisk.SideEffect ? RunStepState.Unknown : RunStepState.Stopped;
            var stoppedStep = pending.Step with { State = state, FinishedAt = _clock.UtcNow };
            await _writes.SaveAsync(() => services.Store.FinishStepAsync(stoppedStep, live.MessageId, null, 0, CancellationToken.None)).ConfigureAwait(false);
            pending.Settled = new ToolOutcome(string.Empty, string.Empty, new RunEnding(MessageState.Stopped, null, null));
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            pending.Failure = T(
                string.Create(CultureInfo.InvariantCulture, $"Timed out (not finished within {tool.Timeout.TotalSeconds:0} seconds)."),
                string.Create(CultureInfo.InvariantCulture, $"超时（{tool.Timeout.TotalSeconds:0} 秒内没有完成）。"));
            pending.FailureKind = GenerationErrorKind.Timeout;
        }
        catch (ToolInputException ex)
        {
            pending.Failure = ex.Message;
        }
        catch (ToolFailureException ex)
        {
            pending.Failure = ex.Message;
        }
        catch (ToolOutcomeUnknownException ex)
        {
            // A side effect whose outcome is not known (decision M17): never pretended not to have run.
            pending.Failure = ex.Message;
            pending.Unknown = true;
        }
        catch (StorageWriteException ex)
        {
            pending.Failure = T("Could not read local data: ", "读取本地数据失败：") + ex.Message;
        }
#pragma warning disable CA1031 // A tool's failure is handed back to the model; it never ends the run (architecture §15.10).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogToolFailed(_logger, ex, pending.Call.Name);
            pending.Failure = T("The tool failed: ", "工具出错：") + ex.Message;
        }
    }

    /// <summary>A call's result: stored as a resource, its block added and its step finished, in call order.</summary>
    private async Task<ToolOutcome> FinishToolAsync(AnswerWork work, RunPlan run, RunServices services, PendingCall pending, Dictionary<string, int> failures, HashSet<string> withdrawn)
    {
        if (pending.Settled is { } settled)
        {
            return settled;
        }

        var live = work.Live;
        var call = pending.Call;
        var tool = pending.Tool;
        var input = pending.Input;
        var step = pending.Step;
        var output = pending.Output;
        var failure = pending.Failure;
        ResourceId? stored = null;
        var resultText = output?.Text ?? string.Empty;
        if (output is not null)
        {
            try
            {
                (stored, resultText) = await services.StoreResultAsync(run.Scope, T($"{tool!.DisplayName}: {input}".TrimEnd(' ', ':'), $"{tool.DisplayName}：{input}".TrimEnd('：')), output, CancellationToken.None).ConfigureAwait(false);
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
        var cut = false;
        if (output is not null && stored is { } id)
        {
            failures[call.Name] = 0;
            cut = TokenEstimator.Estimate(resultText) > ToolResultTokens;
            content = !cut
                ? resultText
                : Start(resultText, ToolResultTokens) + T(
                    $"\n\n(The result is long; the above is only its start. The full result is resource {id}; read it with {SearchAttachmentsTool.ToolName}'s resource and part.)",
                    $"\n\n（结果较长，以上只是开头。完整结果是资源 {id}，可用 {SearchAttachmentsTool.ToolName} 的 resource 与 part 读取。）");
            if (output.Wrap is { } wrap)
            {
                // A third-party tool's output is material, never an instruction (decision M15).
                content = wrap(content);
            }
            else if (output.Provenance == ResourceProvenance.External)
            {
                // Outside material is never an instruction (architecture §15.8, decision W11).
                content = WebMaterial.Wrap(content);
            }

            block = new ToolResultBlock(call.Id, ToolResultStatus.Succeeded, Preview(resultText), id, output.Detail, output.Findings);
        }
        else
        {
            failure ??= T("The tool returned no result.", "工具没有返回结果。");
            var count = failures[call.Name] = failures.GetValueOrDefault(call.Name) + 1;
            if (tool is not null && count >= MaxConsecutiveFailures)
            {
                withdrawn.Add(tool.Name);
            }

            content = T("Failed: ", "出错：") + failure;
            block = new ToolResultBlock(call.Id, ToolResultStatus.Failed, failure, null, pending.Unknown ? T("Outcome unknown", "结果未知") : pending.FailureKind == GenerationErrorKind.Timeout ? T("Timed out", "超时") : T("Failed", "出错"));
        }

        live.AddToolResult(block);
        var snapshot = live.Snapshot();
        var finished = step with
        {
            State = output is not null ? RunStepState.Completed : pending.Unknown ? RunStepState.Unknown : RunStepState.Failed,
            FinishedAt = _clock.UtcNow,
            Result = stored,
            ErrorKind = output is not null ? null : pending.FailureKind,
            ErrorDetail = output is not null ? null : failure,
        };
        await _writes.SaveAsync(() => services.Store.FinishStepAsync(finished, live.MessageId, snapshot.Blocks, snapshot.Revision, CancellationToken.None)).ConfigureAwait(false);
        var line = T(
            $"(Earlier tool result left out: {call.Name} {input}{(stored is { } s ? $"; the full result is resource {s}" : string.Empty)}. Read it again when needed.)",
            $"（较早的工具结果已省略：{call.Name} {input}{(stored is { } r ? $"，完整结果是资源 {r}" : string.Empty)}；需要时可再读取。）");
        return new ToolOutcome(content, line, null, stored, Failed: stored is null, Findings: output?.Findings, Numbers: output?.Numbers, Cut: cut);
    }

    /// <summary>
    /// Shows the approval card and waits for the user (decision W10): the step is recorded as awaiting approval, the run gives
    /// its slot up, and the wait does not count towards its duration. Returns the decision, or null when the run was stopped
    /// meanwhile (the step is then recorded as stopped and the card withdrawn).
    /// </summary>
    private async Task<bool?> AwaitApprovalAsync(AnswerWork work, RunServices services, RunStepRecord step, PendingApproval card, Stopwatch elapsed, CancellationToken stop)
    {
        var live = work.Live;
        await _writes.SaveAsync(() => Done(services.Store.SetApprovalAsync(step.Task, step.Ordinal, RunStepState.AwaitingApproval, Approval.Pending, CancellationToken.None))).ConfigureAwait(false);
        var decision = live.RequestApproval(card);
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
        tool is null ? (call.Name.Length > 0 ? call.Name : "?") + T(" (no such tool)", "（不存在的工具）")
        : string.IsNullOrEmpty(input) ? tool.DisplayName
        : tool.DisplayName + T(": ", "：") + input;

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
