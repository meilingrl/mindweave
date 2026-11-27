using System.Diagnostics;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Core.Branches;
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
    /// <param name="edition">The prompts the scenario runs with; the prompts as built when null.</param>
    public static async Task<EvalResult> RunAsync(
        EvalScenario scenario, Func<int?, Task<ChatHarness>> start, TimeSpan answerTimeout, bool scripted, CancellationToken ct, EvalEdition? edition = null)
    {
        // The edition's prompts hold for everything this harness starts: its worker, its answers and its summaries.
        using var prompts = (edition ?? EvalEdition.Current).Use();
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
        string? parent = null;
        string? quote = null;
        var approvals = new List<string>();
        var (searchesBefore, fetchedBefore, requestsBefore) = (0, 0, 0);
        var watch = new Stopwatch();
        TimeSpan? firstText = null;
        var timedOut = false;
        foreach (var turn in scenario.Turns)
        {
            approvals.Clear();
            (searchesBefore, fetchedBefore, requestsBefore) = (h.Search.Searches.Count, h.Pages.Fetched.Count, SentRequests(h).Count);
            var attachments = turn.WithAttachments ? setup.Attachments : [];
            var options = new SendOptions(turn.Mode);
            if (turn.BranchOn is { } pick)
            {
                // A branch from a passage the previous answer actually has (a real model's text is not known beforehand).
                parent = answer?.Text ?? string.Empty;
                quote = pick(parent);
                if (quote is null || conversation is not { } from)
                {
                    return new EvalResult(scenario, null, [], "上一个回答里没有可选的段落：" + Excerpt(parent));
                }

                var branch = await h.Conversations.CreateBranchAsync(from, new BranchSelection(answer!.Id, 0, quote, "{\"v\":1}", 1, "eval"), ct);
                if (!branch.Accepted)
                {
                    return new EvalResult(scenario, null, [], "分支被拒绝：" + branch.Rejection);
                }

                conversation = branch.Child;
            }

            watch.Restart();
            var sent = conversation is { } id
                ? await h.Conversations.AskAsync(id, turn.Question, attachments, options, ct)
                : await h.Conversations.StartAsync(h.Workspace, turn.Question, attachments, null, options, ct);
            if (!sent.Accepted)
            {
                return new EvalResult(scenario, null, [], "提问被拒绝：" + sent.Rejection);
            }

            conversation = sent.Conversation;
            (answer, timedOut, firstText) = await WaitRefusingApprovalsAsync(h, sent.Answer!.Value, answerTimeout, approvals, watch);
            watch.Stop();
        }

        var observation = await ObserveAsync(h, conversation!.Value, answer!, approvals, searchesBefore, fetchedBefore, watch.Elapsed, timedOut, ct) with
        {
            FirstText = firstText,
            ParentAnswer = parent,
            Quote = quote,
            Sent = FirstResults(SentRequests(h).Skip(requestsBefore)),
        };
        if (scenario.Summarize)
        {
            observation = await SummarizeAsync(h, observation, conversation.Value, answerTimeout, ct);
        }

        return new EvalResult(scenario, observation, Check(scenario.Expect, observation), null);
    }

    private static string Excerpt(string text) => text.Length > 80 ? text[..80] + "…" : text;

    /// <summary>The requests sent so far: the real service's, else the fake model's.</summary>
    private static System.Collections.Concurrent.ConcurrentQueue<ChatRequest> SentRequests(ChatHarness h) =>
        h.ActiveClient is EvalBudgetedClient real ? real.Requests : h.Client.Requests;

    /// <summary>Each tool result in the requests, as it was first sent, with the tool that was called for it.</summary>
    internal static IReadOnlyList<EvalSent> FirstResults(IEnumerable<ChatRequest> requests)
    {
        var sent = new List<EvalSent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            var tools = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var call in request.Turns.Where(t => t.ToolCalls is not null).SelectMany(t => t.ToolCalls!))
            {
                tools.TryAdd(call.Id, call.Name);
            }

            foreach (var turn in request.Turns.Where(t => t.Role == ChatRole.Tool && t.ToolCallId is not null))
            {
                if (seen.Add(turn.ToolCallId!))
                {
                    sent.Add(new EvalSent(tools.GetValueOrDefault(turn.ToolCallId!, "?"), turn.Content));
                }
            }
        }

        return sent;
    }

    /// <summary>
    /// Summarizes the conversation and measures that instead of the last answer: the summary request's numbers, its time to
    /// the first text, and what was stored.
    /// </summary>
    private static async Task<EvalObservation> SummarizeAsync(ChatHarness h, EvalObservation answered, ConversationId conversation, TimeSpan timeout, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var outcome = await h.SummaryService.SummarizeAsync(conversation, ct);
        if (outcome.Task is not { } task)
        {
            return answered with { Summary = new EvalSummary(string.Empty, null, string.Empty, "Rejected " + outcome.Rejection), Requests = [] };
        }

        var deadline = DateTime.UtcNow + timeout;
        GenerationTaskState? state = null;
        while (DateTime.UtcNow < deadline)
        {
            state = await h.SummaryService.LatestTaskAsync(conversation, ct) is { } latest && latest.Id == task ? latest.State : null;
            if (state is not (null or GenerationTaskState.Queued or GenerationTaskState.Running))
            {
                break;
            }

            await Task.Delay(50, ct);
        }

        watch.Stop();
        await ChatHarness.UntilAsync(async () => (await RequestsAsync(h, task.ToString(), ct)).Count > 0, 30_000);
        var requests = await RequestsAsync(h, task.ToString(), ct);
        var stored = await h.Database.ReadAsync(
            c =>
            {
                using var cmd = c.Command(null, """
                    SELECT v.title, v.short_title, v.body FROM summary_node n JOIN summary_version v ON v.id = n.current_version_id
                    WHERE n.conversation_id = $c;
                    """, ("$c", conversation.ToString()));
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)) : ((string, string?, string)?)null;
            },
            ct);
        var (title, shortTitle, body) = stored ?? (string.Empty, null, string.Empty);
        var (total, prompt, completion, cached) = await TotalsAsync(h, ct);
        var first = requests.FirstOrDefault(r => r.FirstTextMilliseconds is not null);
        return answered with
        {
            Summary = new EvalSummary(title, shortTitle, body, state is null ? "TimedOut" : state.Value == GenerationTaskState.Completed ? "Complete" : state.Value.ToString()),
            Requests = requests,
            Calls = [],
            Elapsed = watch.Elapsed,
            FirstText = first is null ? null : TimeSpan.FromMilliseconds(requests.TakeWhile(r => r != first).Sum(r => r.Milliseconds) + first.FirstTextMilliseconds!.Value),
            ScenarioRequests = total,
            ScenarioPromptTokens = prompt,
            ScenarioCompletionTokens = completion,
            ScenarioCacheReadTokens = cached,
            TimedOut = state is null,
            Cited = [],
        };
    }

    /// <summary>
    /// Requests of the whole scenario, the tokens the service reported for them, and the input it read from its cache
    /// (requirements §10.11.4; null when it reported none).
    /// </summary>
    private static Task<(int Requests, int? Prompt, int? Completion, int? CacheRead)> TotalsAsync(ChatHarness h, CancellationToken ct) =>
        h.Database.ReadAsync(
            c =>
            {
                using var cmd = c.Command(null, "SELECT count(*), sum(prompt_tokens), sum(completion_tokens), sum(cache_read_tokens) FROM ai_request;");
                using var reader = cmd.ExecuteReader();
                reader.Read();
                return (reader.GetInt32(0), reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1), reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2), reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3));
            },
            ct);

    /// <summary>
    /// Waits for the answer to finish, refusing every approval card; after the timeout the answer is stopped. Notes when the
    /// first text of the answer showed, on <paramref name="watch"/>.
    /// </summary>
    private static async Task<(Message Answer, bool TimedOut, TimeSpan? FirstText)> WaitRefusingApprovalsAsync(ChatHarness h, MessageId id, TimeSpan timeout, List<string> approvals, Stopwatch watch)
    {
        var deadline = DateTime.UtcNow + timeout;
        TimeSpan? firstText = null;
        while (true)
        {
            if (firstText is null && h.Conversations.Observe(id)?.Snapshot().Text is { Length: > 0 })
            {
                firstText = watch.Elapsed;
            }

            if (await h.GetMessageAsync(id) is { IsFinished: true } done)
            {
                return (done, false, firstText ?? (done.Text.Length > 0 ? watch.Elapsed : null));
            }

            if (h.Conversations.Observe(id)?.Snapshot().PendingApproval is { } card && h.Conversations.DecideApproval(id, card.CallId, allow: false))
            {
                approvals.Add(card.Target);
            }

            if (DateTime.UtcNow > deadline)
            {
                await h.Conversations.StopAsync(id);
                return (await h.WaitForAnswerAsync(id, timeoutMs: 30_000), true, firstText);
            }

            await Task.Delay(20);
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
        var (total, prompt, completion, cached) = await TotalsAsync(h, ct);
        return new EvalObservation(
            answer, run, calls, requests, [.. approvals],
            [.. h.Search.Searches.Skip(searchesBefore).Select(s => s.Query)],
            [.. h.Pages.Fetched.Skip(fetchedBefore).Select(u => u.AbsoluteUri)],
            elapsed, total, timedOut)
        {
            ScenarioPromptTokens = prompt,
            ScenarioCompletionTokens = completion,
            ScenarioCacheReadTokens = cached,
            Cited = await CitedAsync(h, answer, ct),
        };
    }

    /// <summary>Longest passage of a cited source the judge is shown.</summary>
    private const int PassageLimit = 4_000;

    /// <summary>Each sentence of the answer that writes [n], with the text of the source n names (its located part when known).</summary>
    private static async Task<IReadOnlyList<EvalCited>> CitedAsync(ChatHarness h, Message answer, CancellationToken ct)
    {
        var citations = ContentBlocks.CitationsOf(answer.Blocks).GroupBy(c => c.Number).ToDictionary(g => g.Key, g => g.First());
        var cited = new List<EvalCited>();
        foreach (var sentence in EvalText.Sentences(answer.Text))
        {
            foreach (var number in CitationMarkers.Find(sentence).Select(m => m.Number).Distinct())
            {
                var passage = string.Empty;
                if (citations.TryGetValue(number, out var citation))
                {
                    var text = citation.Resource is { } resource ? (await h.ResourceService.GetAsync(resource, ct))?.Text : null;
                    passage = text is null ? citation.Title
                        : citation.End > citation.Start && citation.End <= text.Length ? text[citation.Start..citation.End]
                        : text;
                }

                cited.Add(new EvalCited(sentence, number, passage.Length > PassageLimit ? passage[..PassageLimit] : passage));
            }
        }

        return cited;
    }

    private static Task<List<EvalRequest>> RequestsAsync(ChatHarness h, string? task, CancellationToken ct) =>
        h.Database.ReadAsync(
            c =>
            {
                var list = new List<EvalRequest>();
                using var cmd = c.Command(null, """
                    SELECT purpose, outcome, estimated_tokens, prompt_tokens, completion_tokens, finished_at - started_at, first_text_ms, cache_read_tokens
                    FROM ai_request WHERE task_id = $task ORDER BY started_at;
                    """, ("$task", task ?? string.Empty));
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int? Int(int i) => reader.IsDBNull(i) ? null : reader.GetInt32(i);
                    list.Add(new EvalRequest(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1), Int(2), Int(3), Int(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6), Int(7)));
                }

                return list;
            },
            ct);

    /// <summary>The checks of an expectation, in a fixed order; the answer finishing is always the first.</summary>
    internal static IReadOnlyList<EvalCheck> Check(EvalExpectation expect, EvalObservation o)
    {
        var text = o.Text;
        var used = o.Calls.Select(c => c.Tool).ToHashSet(StringComparer.Ordinal);
        var checks = new List<EvalCheck>
        {
            o.Summary is { } summary
                ? new("总结完成", !o.TimedOut && summary.State == "Complete" && summary.Body.Length > 0, summary.State)
                : new("回答完成", !o.TimedOut && o.Answer.State == MessageState.Complete, o.TimedOut ? "超时后停止" : $"{o.Answer.State} {o.Answer.ErrorKind} {o.Answer.ErrorDetail}".Trim()),
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

        if (expect.NoStrayNumbers)
        {
            // Q6: an attachment that came with the question has no number; only a run's sources do.
            var known = ContentBlocks.CitationsOf(o.Answer.Blocks).Select(c => c.Number).ToHashSet();
            var stray = CitationMarkers.Find(text).Select(m => m.Number).Distinct().Where(n => !known.Contains(n)).ToList();
            checks.Add(new EvalCheck("不为没有编号的材料写编号", stray.Count == 0, stray.Count == 0 ? string.Empty : "没有来源的编号：" + string.Join("、", stray.Select(n => $"[{n}]"))));
        }

        if (expect.NotRestating)
        {
            var share = EvalText.RestatedShare(o.ParentAnswer ?? string.Empty, o.Quote ?? string.Empty, text);
            checks.Add(new EvalCheck("不重复上一级回答", share < EvalText.RestatedLimit, $"与上一级回答重合 {share:P0}；所选：{Excerpt(o.Quote ?? string.Empty)}"));
        }

        if (expect.Gist)
        {
            var gist = EvalText.Gist(text);
            checks.Add(new EvalCheck("开头是一两句要点", gist.Sentences is >= 1 and <= 2, gist.Sentences == 0 ? "开头不是一段话：" + Excerpt(gist.Block) : $"{gist.Sentences} 句：{Excerpt(gist.Block)}"));
        }

        if (expect.Terms)
        {
            var terms = EvalText.KeyTerms(text);
            checks.Add(new EvalCheck("列出关键术语", terms > 0, terms == 0 ? "没有术语列表" : $"{terms} 个术语"));
        }

        if (expect.FirstResultHas is { } has)
        {
            var first = o.Sent.FirstOrDefault(r => r.Tool == has.Tool);
            checks.Add(new EvalCheck(
                $"第一次 {has.Tool} 的结果含有 {string.Join("／", has.Words)}",
                first is not null && has.Words.Any(w => first.Content.Contains(w, StringComparison.OrdinalIgnoreCase)),
                first is null ? $"没有调用 {has.Tool}" : $"结果约 {first.Content.Length} 字"));
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
