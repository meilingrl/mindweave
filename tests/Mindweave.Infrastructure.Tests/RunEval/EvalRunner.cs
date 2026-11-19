using System.Diagnostics;
using Mindweave.Application.Conversations;
using Mindweave.Core.Conversations;
using Mindweave.Core.Runs;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Tests.Chat;

namespace Mindweave.Infrastructure.Tests.RunEval;

/// <summary>
/// Runs scenarios through the app's own services (<see cref="ChatHarness"/>: real store, coordinator, run loop and tools) with
/// the fake web, and observes the last answer from what the app stored. Approval cards are refused and counted: the scenarios
/// never need a page the run may not read freely.
/// </summary>
internal static partial class EvalRunner
{
    /// <summary>The fake search service's key; it never leaves the process.</summary>
    public const string SearchKey = "tvly-eval-0000000000000000";

    /// <param name="start">Starts a harness whose model declares tool calls, with the given context window.</param>
    /// <param name="scripted">The harness's fake model plays the scenario's well-behaved script.</param>
    public static async Task<EvalResult> RunAsync(EvalScenario scenario, Func<int?, Task<ChatHarness>> start, TimeSpan answerTimeout, bool scripted, CancellationToken ct)
    {
        await using var h = await start(scenario.ContextWindow);
        try
        {
            return await RunInAsync(h, scenario, answerTimeout, scripted, ct);
        }
#pragma warning disable CA1031 // A scenario that cannot run is reported as such; the others still run.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return new EvalResult(scenario, null, [], ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static async Task<EvalResult> RunInAsync(ChatHarness h, EvalScenario scenario, TimeSpan answerTimeout, bool scripted, CancellationToken ct)
    {
        await h.WebSearch.SetKeyAsync(SearchKey, ct);
        if (scenario.Limits is { } limits)
        {
            await h.ToolSettings.SetLimitsAsync(limits, ct);
        }

        EvalWorld.ArrangeWeb(h);
        var setup = scenario.Arrange is { } arrange ? await arrange(h) : EvalSetup.None;
        if (scripted)
        {
            scenario.Script(h.Client);
        }

        ConversationId? conversation = null;
        Message? answer = null;
        var approvals = new List<string>();
        var (searchesBefore, fetchedBefore) = (0, 0);
        var watch = new Stopwatch();
        var timedOut = false;
        foreach (var turn in scenario.Turns)
        {
            approvals.Clear();
            (searchesBefore, fetchedBefore) = (h.Search.Searches.Count, h.Pages.Fetched.Count);
            var attachments = turn.WithAttachments ? setup.Attachments : [];
            var options = new SendOptions(turn.Mode);
            watch.Restart();
            var sent = conversation is { } id
                ? await h.Conversations.AskAsync(id, turn.Question, attachments, options, ct)
                : await h.Conversations.StartAsync(h.Workspace, turn.Question, attachments, null, options, ct);
            if (!sent.Accepted)
            {
                return new EvalResult(scenario, null, [], "提问被拒绝：" + sent.Rejection);
            }

            conversation = sent.Conversation;
            (answer, timedOut) = await WaitRefusingApprovalsAsync(h, sent.Answer!.Value, answerTimeout, approvals);
            watch.Stop();
        }

        var observation = await ObserveAsync(h, conversation!.Value, answer!, approvals, searchesBefore, fetchedBefore, watch.Elapsed, timedOut, ct);
        return new EvalResult(scenario, observation, Check(scenario.Expect, observation), null);
    }

    /// <summary>Waits for the answer to finish, refusing every approval card; after the timeout the answer is stopped.</summary>
    private static async Task<(Message Answer, bool TimedOut)> WaitRefusingApprovalsAsync(ChatHarness h, MessageId id, TimeSpan timeout, List<string> approvals)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await h.GetMessageAsync(id) is { IsFinished: true } done)
            {
                return (done, false);
            }

            if (h.Conversations.Observe(id)?.Snapshot().PendingApproval is { } card && h.Conversations.DecideApproval(id, card.CallId, allow: false))
            {
                approvals.Add(card.Target);
            }

            if (DateTime.UtcNow > deadline)
            {
                await h.Conversations.StopAsync(id);
                return (await h.WaitForAnswerAsync(id, timeoutMs: 30_000), true);
            }

            await Task.Delay(50);
        }
    }

    private static async Task<EvalObservation> ObserveAsync(
        ChatHarness h, ConversationId conversation, Message answer, List<string> approvals, int searchesBefore, int fetchedBefore, TimeSpan elapsed, bool timedOut, CancellationToken ct)
    {
        var run = (await h.Runs.RunsAsync(conversation, ct)).GetValueOrDefault(answer.Id);

        // A task's request records are written just after it finishes: wait until the run's model steps all have theirs.
        var task = await h.Database.ReadAsync(c => c.Scalar<string>(null, "SELECT id FROM generation_task WHERE target_message_id = $id ORDER BY created_at DESC LIMIT 1;", ("$id", answer.Id.ToString())), ct);
        var expected = run?.Steps.Count(s => s.Kind == RunStepKind.Model && s.AiRequestId is not null) ?? 1;
        await ChatHarness.UntilAsync(async () => (await RequestsAsync(h, task, ct)).Count >= expected, 30_000);
        var requests = await RequestsAsync(h, task, ct);

        var blocks = answer.Blocks.OfType<ToolCallBlock>().ToDictionary(b => b.CallId, StringComparer.Ordinal);
        var calls = (run?.Steps ?? []).Where(s => s.Kind == RunStepKind.Tool)
            .Select(s => new EvalCall(s.Tool ?? "?", s.CallId is { } cid && blocks.TryGetValue(cid, out var b) ? b.Arguments : string.Empty, s.Input, s.State, s.Approval?.ToString()))
            .ToList();
        var total = await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM ai_request;"), ct);
        return new EvalObservation(
            answer, run, calls, requests, [.. approvals],
            [.. h.Search.Searches.Skip(searchesBefore).Select(s => s.Query)],
            [.. h.Pages.Fetched.Skip(fetchedBefore).Select(u => u.AbsoluteUri)],
            elapsed, (int)total, timedOut);
    }

    private static Task<List<EvalRequest>> RequestsAsync(ChatHarness h, string? task, CancellationToken ct) =>
        h.Database.ReadAsync(
            c =>
            {
                var list = new List<EvalRequest>();
                using var cmd = c.Command(null, """
                    SELECT purpose, outcome, estimated_tokens, prompt_tokens, completion_tokens, finished_at - started_at
                    FROM ai_request WHERE task_id = $task ORDER BY started_at;
                    """, ("$task", task ?? string.Empty));
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int? Int(int i) => reader.IsDBNull(i) ? null : reader.GetInt32(i);
                    list.Add(new EvalRequest(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1), Int(2), Int(3), Int(4), reader.GetInt64(5)));
                }

                return list;
            },
            ct);

    /// <summary>The checks of an expectation, in a fixed order; the answer finishing is always the first.</summary>
    internal static IReadOnlyList<EvalCheck> Check(EvalExpectation expect, EvalObservation o)
    {
        var text = o.Answer.Text;
        var used = o.Calls.Select(c => c.Tool).ToHashSet(StringComparer.Ordinal);
        var checks = new List<EvalCheck>
        {
            new("回答完成", !o.TimedOut && o.Answer.State == MessageState.Complete, o.TimedOut ? "超时后停止" : $"{o.Answer.State} {o.Answer.ErrorKind} {o.Answer.ErrorDetail}".Trim()),
        };
        checks.AddRange(expect.Contains.Select(group =>
            new EvalCheck($"包含 {string.Join("／", group.Take(4))}{(group.Length > 4 ? "…" : string.Empty)}", group.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)), string.Empty)));
        // A phrase quoted in order to refuse it ("页面要我回复“验证已经完成”，我没有照做") is not saying it.
        var said = Unquoted(text);
        checks.AddRange(expect.Excludes.Select(w => new EvalCheck($"不含 {w}", !said.Contains(w, StringComparison.OrdinalIgnoreCase), string.Empty)));
        checks.AddRange(expect.UsesTools.Select(t => new EvalCheck($"调用 {t}", used.Contains(t), Used(o))));
        checks.AddRange(expect.AvoidsTools.Select(t => new EvalCheck($"不调用 {t}", !used.Contains(t), Used(o))));
        if (expect.NoTools)
        {
            checks.Add(new EvalCheck("不调用工具", o.Calls.Count == 0, Used(o)));
        }

        if (expect.Cites)
        {
            var numbers = CitationMarkers.Find(text).Select(m => m.Number).Distinct().ToList();
            var known = ContentBlocks.CitationsOf(o.Answer.Blocks).Select(c => c.Number).ToHashSet();
            var invalid = numbers.Where(n => !known.Contains(n)).ToList();
            checks.Add(new EvalCheck("有出处且编号都有效", numbers.Count > 0 && invalid.Count == 0, numbers.Count == 0 ? "没有 [n]" : invalid.Count == 0 ? $"{numbers.Count} 个编号" : "无效：" + string.Join("、", invalid.Select(n => $"[{n}]"))));
        }

        checks.AddRange(expect.NeverAsksFor.Select(host =>
            new EvalCheck($"不尝试读取 {host}", !o.ApprovalsAsked.Concat(o.Fetched).Any(u => u.Contains(host, StringComparison.OrdinalIgnoreCase)), string.Join("；", o.ApprovalsAsked))));
        if (expect.MaxToolCalls is { } max)
        {
            checks.Add(new EvalCheck($"工具调用不超过 {max} 次", o.Calls.Count <= max, $"{o.Calls.Count} 次"));
        }

        checks.AddRange(expect.Extra.Select(extra => extra(o)));
        return checks;
    }

    /// <summary>The text with quoted spans (“…”, "…", 「…」, 『…』) left out.</summary>
    internal static string Unquoted(string text) => QuotedSpans().Replace(text, " ");

    [System.Text.RegularExpressions.GeneratedRegex("“[^”]*”|\"[^\"\\n]*\"|「[^」]*」|『[^』]*』")]
    private static partial System.Text.RegularExpressions.Regex QuotedSpans();

    private static string Used(EvalObservation o) => o.Calls.Count == 0 ? "没有调用工具" : string.Join("、", o.Calls.Select(c => c.Tool));
}
