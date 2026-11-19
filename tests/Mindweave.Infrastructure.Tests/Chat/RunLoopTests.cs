using System.Text.Json;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Storage;
using Mindweave.Application.Tools;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// The run loop improvements (docs/design/agent-loop.md L1–L11) through the real store, coordinator, tools and a fake provider:
/// the run instructions and budget line, retrying a transient error, a cut-off middle turn, room for the run, read-only calls
/// in parallel, repeated calls, eviction by citation, the 开 search terms, the index of earlier material and numbered local results.
/// </summary>
public class RunLoopTests
{
    private static readonly ModelSettingsInput ToolModel = new(
        "fake", "https://fake.example/v1", "fake-model", "sk-test",
        Capabilities: ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = true });

    private const string Material = "第一段讲铁路的修建。\n\n第二段讲蒸汽机的发明：瓦特改良了蒸汽机。\n\n第三段讲信号灯。";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Query(string q) => JsonSerializer.Serialize(new { query = q });

    // L1: run instructions.

    [Fact]
    public async Task Every_request_of_a_run_opens_with_the_run_instructions_and_an_answer_without_tools_has_none()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        h.Client.CallTool(SearchAttachmentsTool.ToolName, Query("蒸汽机"));
        h.Client.Reply("瓦特改良了蒸汽机。");

        var (_, answer) = await AskWithAttachmentAsync(h, "蒸汽机是谁改良的？");

        Assert.Equal(MessageState.Complete, answer.State);
        var requests = h.Client.Requests.Where(FakeChatClient.IsAnswer).ToList();
        Assert.Equal(2, requests.Count);
        Assert.All(requests, r =>
        {
            Assert.Equal(ChatRole.System, r.Turns[0].Role);
            Assert.StartsWith("你是 Mindweave 中回答用户提问的助手", r.Turns[0].Content, StringComparison.Ordinal);
            Assert.Contains("search_attachments（检索附件）", r.Turns[0].Content, StringComparison.Ordinal);
            Assert.DoesNotContain("web_search", r.Turns[0].Content, StringComparison.Ordinal);
            Assert.Single(r.Turns, t => t.Role == ChatRole.System);
        });

        // Without a model that calls tools, the request is exactly as before: no system turn of its own.
        await using var plain = await ChatHarness.StartAsync();
        var (_, single) = await AskWithAttachmentAsync(plain, "问");
        Assert.Equal(MessageState.Complete, single.State);
        Assert.DoesNotContain(plain.Client.Requests.Single(FakeChatClient.IsAnswer).Turns, t => t.Role == ChatRole.System);
    }

    [Fact]
    public void The_run_instructions_name_each_offered_tool_and_stay_short()
    {
        var all = new ITool[]
        {
            new SearchAttachmentsTool(null!), new SearchSummariesTool(null!), new WebSearchTool(null!), new FetchPageTool(null!), new NamedTool("notes_write"),
        };

        var text = RunPrompt.For(all);

        foreach (var name in new[] { "search_attachments（检索附件）", "search_summaries（检索总结网络）", "web_search（网络搜索）", "fetch_page（取网页）" })
        {
            Assert.Contains(name, text, StringComparison.Ordinal);
        }

        Assert.Contains("notes_write：用户添加的第三方工具", text, StringComparison.Ordinal);
        Assert.InRange(TokenEstimator.Estimate(text), 1, 650);
        Assert.InRange(TokenEstimator.Estimate(RunPrompt.For([new SearchAttachmentsTool(null!)])), 1, 400);

        // The fake provider tells internal requests apart by their system turn; the instructions must read as an answer's.
        var request = new ChatRequest(ModelSnapshot(), [new ChatTurn(ChatRole.System, text), new ChatTurn(ChatRole.User, "问")]);
        Assert.True(FakeChatClient.IsAnswer(request));
    }

    // L2: budget line.

    [Fact]
    public async Task The_budget_line_counts_down_and_the_last_request_asks_for_the_answer_instead()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        await h.ToolSettings.SetLimitsAsync(RunLimits.Default with { ModelRequests = 3, ToolCalls = 5 }, Ct);
        h.Client.KeepCalling = (SearchAttachmentsTool.ToolName, Query("蒸汽机"));

        var (_, answer) = await AskWithAttachmentAsync(h, "蒸汽机？");

        Assert.Equal(MessageState.Complete, answer.State);
        var systems = h.Client.Requests.Where(FakeChatClient.IsAnswer).Select(r => r.Turns[0].Content).ToList();
        Assert.Equal(3, systems.Count);
        Assert.EndsWith("【额度】包括这一轮，本次回答还可以调用工具 2 轮、共 5 次；用完之后必须直接回答。", systems[0], StringComparison.Ordinal);
        Assert.EndsWith("【额度】包括这一轮，本次回答还可以调用工具 1 轮、共 4 次；用完之后必须直接回答。", systems[1], StringComparison.Ordinal);
        Assert.DoesNotContain("【额度】", systems[2], StringComparison.Ordinal);
        Assert.EndsWith(AnswerRunnerFinal, systems[2], StringComparison.Ordinal);
    }

    // L5: one retry of a transient error.

    [Theory]
    [InlineData(GenerationErrorKind.ServerError, 502)]
    [InlineData(GenerationErrorKind.QuotaOrRateLimited, 429)]
    [InlineData(GenerationErrorKind.Network, null)]
    public async Task A_request_after_tool_results_that_fails_by_chance_is_tried_once_more_as_a_step_of_its_own(GenerationErrorKind kind, int? status)
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        h.Client.CallTool(SearchAttachmentsTool.ToolName, Query("蒸汽机"));
        h.Client.FailWithStatus(kind, status, status == 429 ? TimeSpan.FromMilliseconds(30) : null);
        h.Client.Reply("瓦特改良了蒸汽机。");

        var (conversation, answer) = await AskWithAttachmentAsync(h, "蒸汽机是谁改良的？");

        Assert.Equal((MessageState.Complete, "瓦特改良了蒸汽机。"), (answer.State, answer.Text));
        var run = (await h.Runs.RunsAsync(conversation, Ct))[answer.Id];
        Assert.Equal([RunStepKind.Model, RunStepKind.Tool, RunStepKind.Model, RunStepKind.Model], run.Steps.Select(s => s.Kind));
        Assert.Equal((RunStepState.Failed, kind), (run.Steps[2].State, run.Steps[2].ErrorKind!.Value));
        Assert.Equal(RunStepState.Completed, run.Steps[3].State);
        Assert.Equal(3, h.Client.Requests.Count(FakeChatClient.IsAnswer));
    }

    [Fact]
    public async Task A_second_failure_in_a_row_a_refused_key_or_a_failed_first_request_ends_the_answer_as_before()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);

        // Two transient failures in a row: one retry only.
        h.Client.CallTool(SearchAttachmentsTool.ToolName, Query("蒸汽机"));
        h.Client.FailWithStatus(GenerationErrorKind.ServerError, 500);
        h.Client.FailWithStatus(GenerationErrorKind.ServerError, 500);
        var (_, twice) = await AskWithAttachmentAsync(h, "一");
        Assert.Equal((MessageState.Failed, GenerationErrorKind.ServerError), (twice.State, twice.ErrorKind));

        // Not by chance: a refused key, a used-up quota.
        h.Client.CallTool(SearchAttachmentsTool.ToolName, Query("蒸汽机"));
        h.Client.FailWithStatus(GenerationErrorKind.Unauthorized, 401);
        var (_, refused) = await AskWithAttachmentAsync(h, "二");
        Assert.Equal((MessageState.Failed, GenerationErrorKind.Unauthorized), (refused.State, refused.ErrorKind));
        h.Client.CallTool(SearchAttachmentsTool.ToolName, Query("蒸汽机"));
        h.Client.FailWithStatus(GenerationErrorKind.QuotaOrRateLimited, 402);
        var (_, broke) = await AskWithAttachmentAsync(h, "三");
        Assert.Equal((MessageState.Failed, GenerationErrorKind.QuotaOrRateLimited), (broke.State, broke.ErrorKind));

        // The first request, before any tool result, fails as a single answer does.
        h.Client.FailWithStatus(GenerationErrorKind.ServerError, 503);
        var (conversation, first) = await AskWithAttachmentAsync(h, "四");
        Assert.Equal((MessageState.Failed, GenerationErrorKind.ServerError), (first.State, first.ErrorKind));
        Assert.Single((await h.Runs.RunsAsync(conversation, Ct))[first.Id].Steps);
    }

    // L6: a cut-off middle round.

    [Fact]
    public async Task A_middle_round_cut_off_while_calling_a_tool_goes_to_the_last_request_without_running_the_call()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        h.Client.CallTool(SearchAttachmentsTool.ToolName, Query("蒸汽机"));
        h.Client.CutOffCalling(SearchAttachmentsTool.ToolName, Query("信号"));
        h.Client.Reply("瓦特改良了蒸汽机。");

        var (conversation, answer) = await AskWithAttachmentAsync(h, "蒸汽机是谁改良的？");

        Assert.Equal((MessageState.Complete, "瓦特改良了蒸汽机。"), (answer.State, answer.Text));
        var run = (await h.Runs.RunsAsync(conversation, Ct))[answer.Id];
        Assert.Equal([RunStepKind.Model, RunStepKind.Tool, RunStepKind.Model, RunStepKind.Model], run.Steps.Select(s => s.Kind));
        Assert.Single(answer.Blocks.OfType<ToolCallBlock>());
        Assert.Null(run.LimitReached);
        var last = h.Client.Requests.Where(FakeChatClient.IsAnswer).Last();
        Assert.False(last.HasTools);
        Assert.Contains("【上一轮回复因输出长度上限被截断，其中的工具调用没有执行】", last.Turns[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_round_cut_off_while_writing_its_answer_or_by_the_filter_ends_the_run_as_before()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        h.Client.CallTool(SearchAttachmentsTool.ToolName, Query("蒸汽机"));
        h.Client.CutOff("瓦特改良了");
        var (_, cut) = await AskWithAttachmentAsync(h, "一");
        Assert.Equal((MessageState.Stopped, GenerationErrorKind.OutputLimit, "瓦特改良了"), (cut.State, cut.ErrorKind, cut.Text));

        h.Client.CallTool(SearchAttachmentsTool.ToolName, Query("蒸汽机"));
        h.Client.Filtered();
        var (_, filtered) = await AskWithAttachmentAsync(h, "二");
        Assert.Equal((MessageState.Failed, GenerationErrorKind.ContentFiltered), (filtered.State, filtered.ErrorKind));
    }

    // L8: room for the run.

    [Fact]
    public async Task A_run_plans_its_context_with_a_quarter_of_the_space_left_and_an_answer_without_tools_uses_all_of_it()
    {
        // About 9,000 estimated tokens of attachment in a window whose input space is about 10,300: whole without tools,
        // reduced for a run, so the first request still has room for tools.
        var material = string.Concat(Enumerable.Range(1, 60).Select(i => $"第 {i} 节：" + new string('河', 140) + "\n\n"));
        var window = ToolModel with { ContextWindowTokens = 16_000 };

        await using var plain = await ChatHarness.StartAsync(model: window with { Capabilities = ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) });
        var (_, single) = await AskWithAttachmentAsync(plain, "这些节讲了什么？", material);
        Assert.Equal(MessageState.Complete, single.State);
        Assert.Contains("第 60 节", plain.Client.Requests.Single(FakeChatClient.IsAnswer).Turns[^1].Content, StringComparison.Ordinal);

        await using var h = await ChatHarness.StartAsync(model: window);
        h.Client.CallTool(SearchAttachmentsTool.ToolName, Query("第 30 节"));
        h.Client.Reply("讲的是河。");
        var (conversation, answer) = await AskWithAttachmentAsync(h, "这些节讲了什么？", material);

        Assert.Equal(MessageState.Complete, answer.State);
        var first = h.Client.Requests.First(FakeChatClient.IsAnswer);
        Assert.True(first.HasTools);
        var budget = Core.Context.ContextBudget.For(Snapshot(window));
        Assert.InRange(ChatTurns.Estimate(first.Turns) - TokenEstimator.Estimate(first.Turns[0].Content), 0, budget.LeavingRoom(ConversationServiceRunRoom).Available + 20);
        var run = (await h.Runs.RunsAsync(conversation, Ct))[answer.Id];
        Assert.Null(run.LimitReached);
    }

    private const int ConversationServiceRunRoom = 25;

    // L3: read-only calls in parallel.

    [Fact]
    public async Task Read_only_calls_of_one_reply_run_at_once_and_their_results_come_in_call_order()
    {
        var tool = new CountingTool("count_tool", ToolRisk.LocalRead);
        await using var h = await ChatHarness.StartAsync(model: ToolModel, tools: [tool]);
        h.Client.CallTools("", [.. Enumerable.Range(1, 3).Select(i => new ChatToolCall($"c{i}", "count_tool", $"{{\"n\":{i}}}"))]);
        h.Client.Reply("好。");

        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await ChatHarness.UntilAsync(() => tool.Running == 3);
        tool.Open();
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal((MessageState.Complete, 3), (answer.State, tool.Max));
        Assert.Equal(["c1", "c2", "c3"], answer.Blocks.OfType<ToolResultBlock>().Select(b => b.CallId));
        Assert.Equal(["完成 1", "完成 2", "完成 3"], answer.Blocks.OfType<ToolResultBlock>().Select(b => b.Preview));
        var steps = (await h.Runs.RunsAsync(sent.Conversation, Ct))[answer.Id].Steps.Where(s => s.Kind == RunStepKind.Tool).ToList();
        Assert.Equal(["c1", "c2", "c3"], steps.OrderBy(s => s.Ordinal).Select(s => s.CallId));
        Assert.All(steps, s => Assert.Equal(RunStepState.Completed, s.State));
        var next = h.Client.Requests.Where(FakeChatClient.IsAnswer).Last().Turns.Where(t => t.Role == ChatRole.Tool).ToList();
        Assert.Equal(["c1", "c2", "c3"], next.Select(t => t.ToolCallId));
        Assert.Equal(["完成 1", "完成 2", "完成 3"], next.Select(t => t.Content));
    }

    [Fact]
    public async Task Stopping_while_calls_run_at_once_stops_them_all()
    {
        var tool = new CountingTool("count_tool", ToolRisk.LocalRead);
        await using var h = await ChatHarness.StartAsync(model: ToolModel, tools: [tool]);
        h.Client.CallTools("等一下。", [.. Enumerable.Range(1, 3).Select(i => new ChatToolCall($"c{i}", "count_tool", $"{{\"n\":{i}}}"))]);

        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await ChatHarness.UntilAsync(() => tool.Running == 3);
        await h.Conversations.StopAsync(sent.Answer!.Value);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal((MessageState.Stopped, "等一下。"), (answer.State, answer.Text));
        Assert.Empty(answer.Blocks.OfType<ToolResultBlock>());
        var steps = (await h.Runs.RunsAsync(sent.Conversation, Ct))[answer.Id].Steps.Where(s => s.Kind == RunStepKind.Tool).ToList();
        Assert.Equal(3, steps.Count);
        Assert.All(steps, s => Assert.Equal(RunStepState.Stopped, s.State));
        Assert.Single(h.Client.Requests, FakeChatClient.IsAnswer);
    }

    [Fact]
    public async Task At_most_four_calls_run_at_once()
    {
        var tool = new CountingTool("count_tool", ToolRisk.LocalRead);
        await using var h = await ChatHarness.StartAsync(model: ToolModel, tools: [tool]);
        h.Client.CallTools("", [.. Enumerable.Range(1, 6).Select(i => new ChatToolCall($"c{i}", "count_tool", $"{{\"n\":{i}}}"))]);
        h.Client.Reply("好。");

        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await ChatHarness.UntilAsync(() => tool.Running == 4);
        await Task.Delay(100, Ct);
        Assert.Equal(4, tool.Running);
        tool.Open();
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal((MessageState.Complete, 4), (answer.State, tool.Max));
        Assert.Equal(6, answer.Blocks.OfType<ToolResultBlock>().Count(b => b.Status == ToolResultStatus.Succeeded));
    }

    [Fact]
    public async Task A_reply_with_a_call_that_may_have_a_side_effect_runs_all_its_calls_one_after_another()
    {
        var read = new CountingTool("read_tool", ToolRisk.NetworkRead, opened: true);
        var write = new CountingTool("write_tool", ToolRisk.SideEffect, opened: true, shared: read);
        await using var h = await ChatHarness.StartAsync(model: ToolModel, tools: [read, write]);
        h.Client.CallTools("", [new ChatToolCall("c1", "read_tool", "{\"n\":1}"), new ChatToolCall("c2", "write_tool", "{\"n\":2}"), new ChatToolCall("c3", "read_tool", "{\"n\":3}")]);
        h.Client.Reply("好。");

        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal((MessageState.Complete, 1), (answer.State, read.Max));
        Assert.Equal(["c1", "c2", "c3"], answer.Blocks.OfType<ToolResultBlock>().Select(b => b.CallId));
    }

    /// <summary>
    /// A tool that counts the calls running at once (shared with another when given) and waits until opened; when opened from
    /// the start, each call still takes a moment, so calls that could overlap do.
    /// </summary>
    private sealed class CountingTool(string name, ToolRisk risk, bool opened = false, CountingTool? shared = null) : ITool
    {
        private readonly TaskCompletionSource _open = NewOpen(opened);
        private int _running;
        private int _max;

        private CountingTool Counter => shared ?? this;

        public int Running => Volatile.Read(ref Counter._running);

        public int Max => Volatile.Read(ref Counter._max);

        public string Name => name;

        public string DisplayName => name;

        public string Description => "测试用的工具";

        public string ParametersSchema => "{\"type\":\"object\"}";

        public ToolRisk Risk => risk;

        public TimeSpan Timeout => TimeSpan.FromSeconds(20);

        public void Open() => _open.TrySetResult();

        public Task<bool> IsAvailableAsync(ToolScope scope, CancellationToken cancellationToken) => Task.FromResult(true);

        public string Describe(JsonElement arguments) => arguments.TryGetProperty("n", out var n) ? n.ToString() : string.Empty;

        public async Task<ToolOutput> ExecuteAsync(ToolScope scope, JsonElement arguments, CancellationToken cancellationToken)
        {
            var counter = Counter;
            var now = Interlocked.Increment(ref counter._running);
            int seen;
            while ((seen = Volatile.Read(ref counter._max)) < now && Interlocked.CompareExchange(ref counter._max, now, seen) != seen)
            {
            }

            try
            {
                await _open.Task.WaitAsync(cancellationToken);
                await Task.Delay(30, cancellationToken);
                return new ToolOutput("完成 " + Describe(arguments), "完成", ResourceProvenance.Workspace, []);
            }
            finally
            {
                Interlocked.Decrement(ref counter._running);
            }
        }

        private static TaskCompletionSource NewOpen(bool opened)
        {
            var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (opened)
            {
                open.TrySetResult();
            }

            return open;
        }
    }

    private static ModelSnapshot Snapshot(ModelSettingsInput input) =>
        new("fake", input.BaseUrl, ChatProtocol.ChatCompletions, input.Model, "ref", null, input.Capabilities! with { ContextWindowTokens = input.ContextWindowTokens });

    private const string AnswerRunnerFinal = "【本次运行已达到上限】请根据已经取得的工具结果直接回答用户的问题，不要再调用工具；信息不足时说明还缺什么。";

    private static ModelSnapshot ModelSnapshot() =>
        new("fake", "https://fake.example/v1", ChatProtocol.ChatCompletions, "fake-model", "ref", null, ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions));

    private static async Task<ResourceId> PasteAsync(ChatHarness h, string text) =>
        (await h.ResourceService.AttachPasteAsync(DraftSlot.NewExploration(h.Workspace), text, Ct)).Resource!.Resource.Id;

    private static async Task<(ConversationId Conversation, Message Answer)> AskWithAttachmentAsync(ChatHarness h, string question, string material = Material)
    {
        var resource = await PasteAsync(h, material);
        var sent = await h.Conversations.StartAsync(h.Workspace, question, [resource], null, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        return (sent.Conversation, await h.WaitForAnswerAsync(sent.Answer!.Value));
    }

    /// <summary>A third-party-looking tool that is never run.</summary>
    private sealed class NamedTool(string name, ToolRisk risk = ToolRisk.SideEffect) : ITool
    {
        public string Name => name;

        public string DisplayName => name;

        public string Description => "测试用的工具";

        public string ParametersSchema => "{\"type\":\"object\"}";

        public ToolRisk Risk => risk;

        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public Task<bool> IsAvailableAsync(ToolScope scope, CancellationToken cancellationToken) => Task.FromResult(true);

        public string Describe(JsonElement arguments) => string.Empty;

        public Task<ToolOutput> ExecuteAsync(ToolScope scope, JsonElement arguments, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolOutput("完成", "完成", ResourceProvenance.Workspace, []));
    }
}
