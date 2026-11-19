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
