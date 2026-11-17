using System.Text.Json;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Storage;
using Mindweave.Application.Tools;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Phase 2 acceptance (requirements §11, agent platform phase 2; architecture §15.5, §15.10, §15.12) through the real store,
/// coordinator, tools and a fake provider that asks for tool calls: when an answer is a run, the loop and its steps, the two
/// local tools, limits, malformed and unknown calls, timeouts, stop at any step, a restart during a run, retry, later context,
/// two runs at once and frozen limits.
/// </summary>
public class RunFlowTests
{
    private static readonly ModelSettingsInput ToolModel = new(
        "fake", "https://fake.example/v1", "fake-model", "sk-test",
        Capabilities: ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = true });

    private const string Material = "第一段讲铁路的修建。\n\n第二段讲蒸汽机的发明：瓦特改良了蒸汽机。\n\n第三段讲信号灯。";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Query(string q) => JsonSerializer.Serialize(new { query = q });

    [Fact]
    public async Task A_run_searches_the_attachment_records_each_step_and_ends_with_one_answer()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        h.Client.CallTool(SearchAttachmentsTool.ToolName, Query("蒸汽机"), "我先查一下附件。");
        h.Client.Reply("根据附件，瓦特改良了蒸汽机。");

        var (conversation, answer) = await AskWithAttachmentAsync(h, "蒸汽机是谁改良的？");

        Assert.Equal(MessageState.Complete, answer.State);
        Assert.Equal("我先查一下附件。\n\n根据附件，瓦特改良了蒸汽机。", answer.Text);

        // The text is block 0, the tool blocks follow (decision R1).
        Assert.IsType<TextBlock>(answer.Blocks[0]);
        var call = Assert.IsType<ToolCallBlock>(answer.Blocks[1]);
        var result = Assert.IsType<ToolResultBlock>(answer.Blocks[2]);
        Assert.Equal((SearchAttachmentsTool.ToolName, "检索附件：蒸汽机", ToolRisk.LocalRead), (call.Tool, call.Summary, call.Risk));
        Assert.Equal((call.CallId, ToolResultStatus.Succeeded, "找到 1 段"), (result.CallId, result.Status, result.Detail));

        // Only the attachment tool was offered (no summaries in the workspace); the second request carries the call and its result.
        var requests = h.Client.Requests.Where(FakeChatClient.IsAnswer).ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal([SearchAttachmentsTool.ToolName], requests[0].Tools!.Select(t => t.Name));
        var assistant = requests[1].Turns.Single(t => t.ToolCalls is not null);
        Assert.Equal("我先查一下附件。", assistant.Content);
        var tool = requests[1].Turns[^1];
        Assert.Equal((ChatRole.Tool, call.CallId), (tool.Role, tool.ToolCallId));
        Assert.Contains("瓦特改良了蒸汽机", tool.Content, StringComparison.Ordinal);

        // Steps: model, tool, model, each finished and the model ones tied to their recorded request.
        var run = (await h.Runs.RunsAsync(conversation, Ct))[answer.Id];
        Assert.Equal([RunStepKind.Model, RunStepKind.Tool, RunStepKind.Model], run.Steps.Select(s => s.Kind));
        Assert.All(run.Steps, s => Assert.Equal(RunStepState.Completed, s.State));
        Assert.All(run.Steps.Where(s => s.Kind == RunStepKind.Model), s => Assert.NotNull(s.AiRequestId));
        Assert.Equal(("蒸汽机", result.Resource), (run.Steps[1].Input, run.Steps[1].Result));
        Assert.Null(run.LimitReached);

        // The whole result is a resource of the conversation, made by that step.
        var stored = (await h.ResourceService.GetAsync(result.Resource!.Value, Ct))!;
        Assert.Equal((ResourceOrigin.Tool, ResourceProvenance.User, conversation), (stored.Resource.Origin, stored.Resource.Provenance, stored.Resource.ConversationId));
        Assert.Contains("瓦特改良了蒸汽机", stored.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_part_of_an_attachment_is_read_by_its_handle()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        var resource = await PasteAsync(h, Material);
        h.Client.CallTool(SearchAttachmentsTool.ToolName, JsonSerializer.Serialize(new { resource = resource.ToString(), part = 1 }));
        h.Client.Reply("读到了。");

        var sent = await h.Conversations.StartAsync(h.Workspace, "读一下", [resource], null, Ct);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        var result = answer.Blocks.OfType<ToolResultBlock>().Single();
        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(new ToolFinding(ToolFindingKind.ResourcePart, resource.ToString(), ContentBlocks.PastedTextName, 1), Assert.Single(result.Findings!));
        Assert.Contains("瓦特改良了蒸汽机", h.Client.Requests.Where(FakeChatClient.IsAnswer).Last().Turns[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Searching_the_summary_network_finds_saved_summaries_and_names_them()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        var earlier = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        await h.WaitForAnswerAsync(earlier.Answer!.Value);
        Assert.True((await h.SummaryService.SummarizeAsync(earlier.Conversation, Ct)).Accepted);
        await ChatHarness.UntilAsync(async () => await h.SummaryService.ForConversationAsync(earlier.Conversation, Ct) is not null);
        var node = (await h.SummaryService.ForConversationAsync(earlier.Conversation, Ct))!.Node;

        h.Client.CallTool(SearchSummariesTool.ToolName, Query(FakeChatClient.DefaultSummaryTitle));
        h.Client.Reply("你以前探索过蒸汽机车。");
        var sent = await h.Conversations.StartAsync(h.Workspace, "我以前看过什么？", Ct);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal(MessageState.Complete, answer.State);
        Assert.Equal([SearchSummariesTool.ToolName], h.Client.Requests.Where(FakeChatClient.IsAnswer).Last(r => r.HasTools).Tools!.Select(t => t.Name));
        var result = answer.Blocks.OfType<ToolResultBlock>().Single();
        Assert.Equal(new ToolFinding(ToolFindingKind.Summary, node.Id.Value.ToString("D"), node.DisplayName), Assert.Single(result.Findings!));
        Assert.Equal(ResourceProvenance.Workspace, (await h.ResourceService.GetAsync(result.Resource!.Value, Ct))!.Resource.Provenance);
    }

    [Fact]
    public async Task Without_declared_tool_calls_or_with_tools_turned_off_an_answer_is_a_single_request_as_before()
    {
        await using var h = await ChatHarness.StartAsync();
        var (conversation, answer) = await AskWithAttachmentAsync(h, "问");
        Assert.Equal(MessageState.Complete, answer.State);
        Assert.False(Assert.Single(h.Client.Requests, FakeChatClient.IsAnswer).HasTools);
        Assert.Empty(await h.Runs.RunsAsync(conversation, Ct));
        Assert.Single(answer.Blocks);

        await h.Models.SaveAsync(ToolModel, Ct);
        await h.ToolSettings.SetEnabledAsync(false, Ct);
        var (second, _) = await AskWithAttachmentAsync(h, "再问");
        Assert.All(h.Client.Requests.Where(FakeChatClient.IsAnswer), r => Assert.False(r.HasTools));
        Assert.Empty(await h.Runs.RunsAsync(second, Ct));
    }

    [Fact]
    public async Task With_nothing_to_search_no_tool_is_offered()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        var sent = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal("答：火车是怎么发明的？", answer.Text);
        Assert.False(Assert.Single(h.Client.Requests, FakeChatClient.IsAnswer).HasTools);
        Assert.Empty(await h.Runs.RunsAsync(sent.Conversation, Ct));
    }

    [Fact]
    public async Task A_model_that_keeps_calling_tools_answers_without_them_at_the_request_limit()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        h.Client.KeepCalling = (SearchAttachmentsTool.ToolName, Query("蒸汽机"));

        var (conversation, answer) = await AskWithAttachmentAsync(h, "蒸汽机？");

        var requests = h.Client.Requests.Where(FakeChatClient.IsAnswer).ToList();
        Assert.Equal(RunLimits.Default.ModelRequests, requests.Count);
        Assert.All(requests.SkipLast(1), r => Assert.True(r.HasTools));
        Assert.False(requests[^1].HasTools);
        Assert.Contains(AnswerRunnerFinalInstruction, requests[^1].Turns[0].Content, StringComparison.Ordinal);
        Assert.Equal(MessageState.Complete, answer.State);

        var run = (await h.Runs.RunsAsync(conversation, Ct))[answer.Id];
        Assert.Equal(RunLimit.ModelRequests, run.LimitReached);
        Assert.Equal(RunLimits.Default.ModelRequests - 1, run.Steps.Count(s => s.Kind == RunStepKind.Tool));
    }

    [Fact]
    public async Task Calls_beyond_the_tool_call_limit_are_not_run_and_the_next_request_has_no_tools()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        await h.ToolSettings.SetLimitsAsync(RunLimits.Default with { ToolCalls = 3 }, Ct);
        h.Client.CallTools("", [.. Enumerable.Range(1, 5).Select(i => new ChatToolCall($"c{i}", SearchAttachmentsTool.ToolName, Query("蒸汽机")))]);
        h.Client.Reply("够了。");

        var (conversation, answer) = await AskWithAttachmentAsync(h, "蒸汽机？");

        Assert.Equal("够了。", answer.Text);
        var results = answer.Blocks.OfType<ToolResultBlock>().ToList();
        Assert.Equal([.. Enumerable.Repeat(ToolResultStatus.Succeeded, 3), ToolResultStatus.Skipped, ToolResultStatus.Skipped], results.Select(r => r.Status));
        var requests = h.Client.Requests.Where(FakeChatClient.IsAnswer).ToList();
        Assert.Equal(2, requests.Count);
        Assert.False(requests[1].HasTools);
        Assert.Equal(5, requests[1].Turns.Count(t => t.Role == ChatRole.Tool));
        var run = (await h.Runs.RunsAsync(conversation, Ct))[answer.Id];
        Assert.Equal((RunLimit.ToolCalls, 3), (run.LimitReached, run.Steps.Count(s => s.Kind == RunStepKind.Tool)));
    }

    [Fact]
    public async Task Malformed_arguments_and_an_unknown_tool_go_back_to_the_model_and_the_run_goes_on()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        h.Client.CallTool(SearchAttachmentsTool.ToolName, "{\"query\": 蒸汽机");
        h.Client.CallTool("delete_everything", "{}");
        h.Client.Reply("我直接回答。");

        var (conversation, answer) = await AskWithAttachmentAsync(h, "问");

        Assert.Equal((MessageState.Complete, "我直接回答。"), (answer.State, answer.Text));
        var results = answer.Blocks.OfType<ToolResultBlock>().ToList();
        Assert.All(results, r => Assert.Equal(ToolResultStatus.Failed, r.Status));
        Assert.Contains("JSON", results[0].Preview, StringComparison.Ordinal);
        Assert.Contains("delete_everything", results[1].Preview, StringComparison.Ordinal);
        Assert.Contains("（不存在的工具）", answer.Blocks.OfType<ToolCallBlock>().Last().Summary, StringComparison.Ordinal);
        var requests = h.Client.Requests.Where(FakeChatClient.IsAnswer).ToList();
        Assert.StartsWith("出错：", requests[1].Turns[^1].Content, StringComparison.Ordinal);
        var run = (await h.Runs.RunsAsync(conversation, Ct))[answer.Id];
        Assert.Equal([RunStepState.Failed, RunStepState.Failed], run.Steps.Where(s => s.Kind == RunStepKind.Tool).Select(s => s.State));
    }

    [Fact]
    public async Task A_tool_that_fails_three_times_in_a_row_is_no_longer_offered()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        for (var i = 0; i < 3; i++)
        {
            h.Client.CallTool(SearchAttachmentsTool.ToolName, "{\"part\": 1}");
        }

        h.Client.Reply("算了。");
        var (_, answer) = await AskWithAttachmentAsync(h, "问");

        Assert.Equal(MessageState.Complete, answer.State);
        var requests = h.Client.Requests.Where(FakeChatClient.IsAnswer).ToList();
        Assert.Equal(4, requests.Count);
        Assert.All(requests.Take(3), r => Assert.True(r.HasTools));
        Assert.False(requests[3].HasTools);
    }

    [Fact]
    public async Task A_tool_that_times_out_fails_its_step_and_the_run_goes_on()
    {
        var slow = new GatedTool("slow_tool", TimeSpan.FromMilliseconds(100));
        await using var h = await ChatHarness.StartAsync(model: ToolModel, tools: [slow]);
        h.Client.CallTool("slow_tool", "{}");
        h.Client.Reply("工具太慢了。");

        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal((MessageState.Complete, "工具太慢了。"), (answer.State, answer.Text));
        var result = answer.Blocks.OfType<ToolResultBlock>().Single();
        Assert.Equal((ToolResultStatus.Failed, "超时"), (result.Status, result.Detail));
        var step = (await h.Runs.RunsAsync(sent.Conversation, Ct))[answer.Id].Steps.Single(s => s.Kind == RunStepKind.Tool);
        Assert.Equal((RunStepState.Failed, GenerationErrorKind.Timeout), (step.State, step.ErrorKind));
    }

    [Fact]
    public async Task Stopping_during_a_model_request_keeps_the_text_and_the_finished_steps()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        h.Client.CallTool(SearchAttachmentsTool.ToolName, Query("蒸汽机"), "先查。");
        var held = h.Client.Hold("查到了");

        var resource = await PasteAsync(h, Material);
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", [resource], null, Ct);
        await held.Started.Task.WaitAsync(Ct);
        await ChatHarness.UntilAsync(() => h.Conversations.Observe(sent.Answer!.Value)?.Snapshot().Text.EndsWith("查到了", StringComparison.Ordinal) == true);
        await h.Conversations.StopAsync(sent.Answer!.Value);

        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);
        Assert.Equal((MessageState.Stopped, "先查。\n\n查到了"), (answer.State, answer.Text));
        Assert.Single(answer.Blocks.OfType<ToolResultBlock>());
        var run = (await h.Runs.RunsAsync(sent.Conversation, Ct))[answer.Id];
        Assert.Equal([RunStepState.Completed, RunStepState.Completed, RunStepState.Stopped], run.Steps.Select(s => s.State));
    }

    [Fact]
    public async Task Stopping_while_a_tool_runs_stops_it_and_keeps_its_call()
    {
        var blocking = new GatedTool("blocking_tool", TimeSpan.FromMinutes(1));
        await using var h = await ChatHarness.StartAsync(model: ToolModel, tools: [blocking]);
        h.Client.CallTool("blocking_tool", "{}", "等一下。");

        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await blocking.Started.Task.WaitAsync(Ct);
        Assert.Equal("blocking_tool", h.Conversations.Observe(sent.Answer!.Value)!.Snapshot().ActiveTool);
        await h.Conversations.StopAsync(sent.Answer!.Value);

        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);
        Assert.Equal((MessageState.Stopped, "等一下。"), (answer.State, answer.Text));
        Assert.Single(answer.Blocks.OfType<ToolCallBlock>());
        Assert.Empty(answer.Blocks.OfType<ToolResultBlock>());
        var step = (await h.Runs.RunsAsync(sent.Conversation, Ct))[answer.Id].Steps.Single(s => s.Kind == RunStepKind.Tool);
        Assert.Equal(RunStepState.Stopped, step.State);
        Assert.Single(h.Client.Requests, FakeChatClient.IsAnswer);
    }

    [Fact]
    public async Task A_restart_during_a_run_marks_the_answer_interrupted_stops_its_steps_and_sends_nothing()
    {
        using var dir = new TempDataDirectory();
        var blocking = new GatedTool("blocking_tool", TimeSpan.FromMinutes(1));
        MessageId answer;
        ConversationId conversation;
        await using (var before = await ChatHarness.StartAsync(directory: dir, model: ToolModel, tools: [blocking]))
        {
            before.Client.CallTool("blocking_tool", "{}", "等一下。");
            var sent = await before.Conversations.StartAsync(before.Workspace, "问", Ct);
            (answer, conversation) = (sent.Answer!.Value, sent.Conversation);
            await blocking.Started.Task.WaitAsync(Ct);
            await ChatHarness.UntilAsync(async () => (await before.Runs.RunsAsync(conversation, Ct)).GetValueOrDefault(answer)?.Steps.Any(s => s is { Kind: RunStepKind.Tool, State: RunStepState.Running }) == true);
            await before.WaitForAnswerAsync(answer, m => m.Text == "等一下。");

            // The process dies: no shutdown, the database connection is simply gone.
            await before.Database.CloseAsync();
        }

        await using var after = await ChatHarness.StartAsync(directory: dir, configure: false);
        await after.Store.RecoverUnfinishedAsync(DateTimeOffset.UtcNow, Ct);

        var recovered = (await after.GetMessageAsync(answer))!;
        Assert.Equal((MessageState.Interrupted, "等一下。"), (recovered.State, recovered.Text));
        var steps = (await after.Runs.RunsAsync(conversation, Ct))[answer].Steps;
        Assert.DoesNotContain(steps, s => s.State is RunStepState.Running or RunStepState.AwaitingApproval);
        Assert.Equal(RunStepState.Stopped, steps.Single(s => s.Kind == RunStepKind.Tool).State);
        Assert.Empty(after.Client.Requests);
    }

    [Fact]
    public async Task Retrying_a_run_starts_a_new_one_and_keeps_the_old_answer_and_its_steps()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        h.Client.CallTool(SearchAttachmentsTool.ToolName, Query("蒸汽机"));
        h.Client.Reply("第一次。");
        var (conversation, first) = await AskWithAttachmentAsync(h, "蒸汽机？");

        h.Client.CallTool(SearchAttachmentsTool.ToolName, Query("蒸汽机"));
        h.Client.Reply("第二次。");
        var retry = await h.Conversations.RetryAsync(conversation, first.Id, Ct);
        Assert.True(retry.Accepted, retry.Rejection?.ToString());
        var second = await h.WaitForAnswerAsync(retry.Answer!.Value);

        Assert.Equal("第二次。", second.Text);
        Assert.Equal("第一次。", (await h.GetMessageAsync(first.Id))!.Text);
        var runs = await h.Runs.RunsAsync(conversation, Ct);
        Assert.Equal(2, runs.Count);
        Assert.NotEqual(
            first.Blocks.OfType<ToolResultBlock>().Single().Resource,
            second.Blocks.OfType<ToolResultBlock>().Single().Resource);
    }

    [Fact]
    public async Task A_later_question_sees_the_earlier_answer_text_but_not_its_tool_calls()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        h.Client.CallTool(SearchAttachmentsTool.ToolName, Query("蒸汽机"), "先查。");
        h.Client.Reply("瓦特。");
        var (conversation, first) = await AskWithAttachmentAsync(h, "谁改良了蒸汽机？");

        h.Client.Reply("1769 年。");
        var count = h.Client.Requests.Count(FakeChatClient.IsAnswer);
        await h.AskAsync(conversation, "哪一年？");

        var request = h.Client.Requests.Where(FakeChatClient.IsAnswer).Skip(count).First();
        Assert.DoesNotContain(request.Turns, t => t.Role == ChatRole.Tool || t.ToolCalls is not null);
        Assert.Contains(request.Turns, t => t.Role == ChatRole.Assistant && t.Content == first.Text);
    }

    [Fact]
    public async Task Two_runs_go_on_at_once_and_a_third_waits_for_a_slot()
    {
        var gate = new GatedTool("gate_tool", TimeSpan.FromMinutes(1));
        await using var h = await ChatHarness.StartAsync(model: ToolModel, tools: [gate]);
        h.Client.KeepCalling = ("gate_tool", "{}");

        var sent = new List<SendOutcome>();
        for (var i = 0; i < 3; i++)
        {
            sent.Add(await h.Conversations.StartAsync(h.Workspace, $"问题{i}", Ct));
        }

        await ChatHarness.UntilAsync(() => gate.Running == 2);
        await ChatHarness.StaysAsync(() => h.Client.Requests.Count(FakeChatClient.IsAnswer) == 2, 200);
        Assert.Equal(1, sent.Count(s => h.Conversations.Observe(s.Answer!.Value)?.Snapshot().State == MessageState.Pending));

        h.Client.KeepCalling = null;
        gate.Open();
        foreach (var s in sent)
        {
            Assert.Equal(MessageState.Complete, (await h.WaitForAnswerAsync(s.Answer!.Value)).State);
        }
    }

    [Fact]
    public async Task A_run_keeps_the_limits_it_was_accepted_with()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        await h.ToolSettings.SetLimitsAsync(RunLimits.Default with { ModelRequests = 3 }, Ct);
        h.Client.KeepCalling = (SearchAttachmentsTool.ToolName, Query("蒸汽机"));
        var resource = await PasteAsync(h, Material);
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", [resource], null, Ct);
        await h.ToolSettings.SetLimitsAsync(RunLimits.Default, Ct);

        await h.WaitForAnswerAsync(sent.Answer!.Value);
        Assert.Equal(3, h.Client.Requests.Count(FakeChatClient.IsAnswer));
    }

    [Fact]
    public async Task Statistics_count_runs_their_model_requests_and_each_tools_calls()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        h.Client.CallTool(SearchAttachmentsTool.ToolName, Query("蒸汽机"));
        h.Client.CallTool(SearchAttachmentsTool.ToolName, Query("信号灯"));
        h.Client.Reply("好。");
        await AskWithAttachmentAsync(h, "问");
        var plain = await h.Conversations.StartAsync(h.Workspace, "不带附件的问题", Ct);
        await h.WaitForAnswerAsync(plain.Answer!.Value);

        var usage = await h.StatisticsStore.ToolUsageAsync(h.Workspace, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), Ct);
        Assert.Equal((1, 3), (usage.Runs, usage.ModelRequests));
        Assert.Equal([new ToolCallCount(SearchAttachmentsTool.ToolName, 2)], usage.Calls);
        Assert.Equal(ToolUsage.None.Runs, (await h.StatisticsStore.ToolUsageAsync(Core.Workspaces.WorkspaceId.New(), DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), Ct)).Runs);
    }

    private static string AnswerRunnerFinalInstruction => "【本次运行已达到上限】";

    private static async Task<ResourceId> PasteAsync(ChatHarness h, string text) =>
        (await h.ResourceService.AttachPasteAsync(DraftSlot.NewExploration(h.Workspace), text, Ct)).Resource!.Resource.Id;

    private static async Task<(ConversationId Conversation, Message Answer)> AskWithAttachmentAsync(ChatHarness h, string question)
    {
        var resource = await PasteAsync(h, Material);
        var sent = await h.Conversations.StartAsync(h.Workspace, question, [resource], null, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        return (sent.Conversation, await h.WaitForAnswerAsync(sent.Answer!.Value));
    }

    /// <summary>A tool that waits until opened (or until its time limit, or a stop); counts how many calls are running.</summary>
    private sealed class GatedTool(string name, TimeSpan timeout) : ITool
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Running => Volatile.Read(ref _running);

        public string Name => name;

        public string DisplayName => name;

        public string Description => "测试用的工具";

        public string ParametersSchema => "{\"type\":\"object\"}";

        public ToolRisk Risk => ToolRisk.LocalRead;

        public TimeSpan Timeout => timeout;

        public void Open() => _open.TrySetResult();

        public Task<bool> IsAvailableAsync(ToolScope scope, CancellationToken cancellationToken) => Task.FromResult(true);

        public string Describe(JsonElement arguments) => string.Empty;

        public async Task<ToolOutput> ExecuteAsync(ToolScope scope, JsonElement arguments, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _running);
            Started.TrySetResult();
            try
            {
                await _open.Task.WaitAsync(cancellationToken);
                return new ToolOutput("完成", "完成", ResourceProvenance.Workspace, []);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }
}
