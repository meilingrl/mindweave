using System.Text.Json;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Tools;
using Mindweave.Application.Tools.Mcp;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;
using Mindweave.FakeMcpServer;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Phase 4 acceptance (requirements §10.4.6 and §11, agent platform phase 4; architecture §15.14; docs/design/mcp.md) through
/// the real store, coordinator, run loop, settings and MCP connections, with the fake server as a real process: tools off
/// until enabled, calls and their untrusted results, approval (allow, deny, every call again, stop and restart while waiting,
/// the slot given up), risk choices and the web switch, changed definitions, a server that fails to start, exits during a
/// call, hangs, returns huge output or images, or writes instructions; two servers with the same tool; statistics.
/// </summary>
public class McpRunFlowTests
{
    private static readonly ModelSettingsInput ToolModel = new(
        "fake", "https://fake.example/v1", "fake-model", "sk-test",
        Capabilities: ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = true });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Text(string value) => JsonSerializer.Serialize(new { text = value });

    private sealed record Setup(ChatHarness H, McpServerConfig Server, IReadOnlyList<McpToolInfo> Tools) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => H.DisposeAsync();

        public Task<McpToolSetting> EnableAsync(string tool) => H.Mcp.EnableToolAsync(Server.Id, Tools.Single(t => t.Name == tool), Ct);

        public string Folder => Path.Combine(H.DataDirectory, "mcp", Server.Id);
    }

    private static async Task<Setup> StartAsync(string name = "files", int? slots = null, TempDataDirectory? directory = null, IReadOnlyDictionary<string, string>? environment = null, params string[] flags)
    {
        var h = await ChatHarness.StartAsync(model: ToolModel, slots: slots, directory: directory);
        var server = await h.Mcp.AddAsync(FakeMcp.Command(name, environment, flags), Ct);
        var tools = await h.McpServers.StartAsync(server.Id, Ct);
        return new Setup(h, server, tools);
    }

    private static async Task<(SendOutcome Sent, Message Answer)> AskAsync(ChatHarness h, string question, WebSearchMode mode = WebSearchMode.Auto)
    {
        var sent = await h.Conversations.StartAsync(h.Workspace, question, [], null, new SendOptions(mode), Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        return (sent, await h.WaitForAnswerAsync(sent.Answer!.Value, timeoutMs: 20_000));
    }

    private static async Task<PendingApproval> CardAsync(ChatHarness h, MessageId answer)
    {
        var live = h.Conversations.Observe(answer)!;
        await ChatHarness.UntilAsync(() => live.Snapshot().PendingApproval is not null, 20_000);
        return live.Snapshot().PendingApproval!;
    }

    private static async Task<IReadOnlyList<Application.Storage.RunStepRecord>> StepsAsync(ChatHarness h, SendOutcome sent, MessageId answer) =>
        [.. (await h.Runs.RunsAsync(sent.Conversation, Ct))[answer].Steps.Where(s => s.Kind == RunStepKind.Tool)];

    [Fact]
    public async Task Tools_of_a_started_server_are_all_off_and_nothing_is_offered_until_one_is_enabled()
    {
        await using var s = await StartAsync();
        Assert.Contains(s.Tools, t => t.Name == "write_note");
        Assert.Equal(McpServerState.Connected, s.H.McpServers.StatusOf(s.Server.Id).State);
        Assert.Empty((await s.H.Mcp.GetAsync(s.Server.Id, Ct))!.Tools);

        s.H.Client.Reply("没有工具。");
        var (_, answer) = await AskAsync(s.H, "问一个问题");

        Assert.Equal(MessageState.Complete, answer.State);
        Assert.False(Assert.Single(s.H.Client.Requests, FakeChatClient.IsAnswer).HasTools);
    }

    [Fact]
    public async Task An_enabled_read_only_tool_is_called_without_asking_and_its_result_is_untrusted_material()
    {
        await using var s = await StartAsync();
        var echo = await s.EnableAsync("echo");
        Assert.Equal(("files__echo", ToolRisk.NetworkRead), (echo.ModelName, echo.Risk));
        s.H.Client.CallTool("files__echo", Text("你好"));
        s.H.Client.Reply("工具说：你好。");

        var (sent, answer) = await AskAsync(s.H, "让工具回声");

        Assert.Equal(MessageState.Complete, answer.State);
        var first = s.H.Client.Requests.Where(FakeChatClient.IsAnswer).First();
        var offered = Assert.Single(first.Tools!);
        Assert.Equal("files__echo", offered.Name);
        Assert.StartsWith("【第三方工具，来自 MCP 服务器“files”", offered.Description, StringComparison.Ordinal);
        Assert.EndsWith("原样返回 text。", offered.Description, StringComparison.Ordinal);

        // The model got the result marked as untrusted material from the server and tool.
        var material = s.H.Client.Requests.Where(FakeChatClient.IsAnswer).Last().Turns[^1].Content;
        Assert.StartsWith(McpMaterial.Opening("files", "echo"), material, StringComparison.Ordinal);
        Assert.Contains("echo: 你好", material, StringComparison.Ordinal);
        Assert.EndsWith(McpMaterial.Closing, material, StringComparison.Ordinal);

        // The step names the server; the result is a stored external resource of origin mcp.
        var call = answer.Blocks.OfType<ToolCallBlock>().Single();
        Assert.StartsWith("files › echo：", call.Summary, StringComparison.Ordinal);
        var result = answer.Blocks.OfType<ToolResultBlock>().Single();
        var stored = (await s.H.ResourceService.GetAsync(result.Resource!.Value, Ct))!;
        Assert.Equal((ResourceOrigin.Mcp, ResourceProvenance.External), (stored.Resource.Origin, stored.Resource.Provenance));
        var step = Assert.Single(await StepsAsync(s.H, sent, answer.Id));
        Assert.Equal(("files", RunStepState.Completed, (Approval?)null), (step.Target, step.State, step.Approval));
    }

    [Fact]
    public async Task A_side_effect_waits_for_approval_without_its_slot_and_every_call_asks_again()
    {
        await using var s = await StartAsync(slots: 1);
        await s.EnableAsync("write_note");
        s.H.Client.CallTool("files__write_note", Text("第一条"), "我来写笔记。");

        var sent = await s.H.Conversations.StartAsync(s.H.Workspace, "写两条笔记", [], null, new SendOptions(WebSearchMode.Auto), Ct);
        var card = await CardAsync(s.H, sent.Answer!.Value);
        Assert.Equal(("files__write_note", "files", "write_note", true), (card.Tool, card.Server, card.ToolTitle, card.IsThirdParty));
        Assert.Contains("\"text\": \"第一条\"", card.Target, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(s.Folder, "notes.txt")));
        var waiting = Assert.Single(await StepsAsync(s.H, sent, sent.Answer!.Value));
        Assert.Equal((RunStepState.AwaitingApproval, Approval.Pending), (waiting.State, waiting.Approval));

        // With one slot, another question still runs while the card waits.
        s.H.Client.Reply("另一个回答。");
        var other = await s.H.Conversations.StartAsync(s.H.Workspace, "另一个问题", [], null, new SendOptions(WebSearchMode.Off), Ct);
        Assert.Equal(MessageState.Complete, (await s.H.WaitForAnswerAsync(other.Answer!.Value)).State);

        s.H.Client.CallTool("files__write_note", Text("第二条"));
        s.H.Client.Reply("写了一条，第二条被拒绝。");
        Assert.True(s.H.Conversations.DecideApproval(sent.Answer!.Value, card.CallId, allow: true));

        // The second call asks again (Q11: one approval is for one call); this time it is refused.
        await ChatHarness.UntilAsync(() => s.H.Conversations.Observe(sent.Answer!.Value)?.Snapshot().PendingApproval is { } next && next.CallId != card.CallId, 20_000);
        var second = s.H.Conversations.Observe(sent.Answer!.Value)!.Snapshot().PendingApproval!;
        Assert.Contains("第二条", second.Target, StringComparison.Ordinal);
        Assert.True(s.H.Conversations.DecideApproval(sent.Answer!.Value, second.CallId, allow: false));
        var answer = await s.H.WaitForAnswerAsync(sent.Answer!.Value, timeoutMs: 20_000);

        Assert.Equal(MessageState.Complete, answer.State);
        Assert.Equal("第一条\n", await File.ReadAllTextAsync(Path.Combine(s.Folder, "notes.txt"), Ct));
        var results = answer.Blocks.OfType<ToolResultBlock>().ToList();
        Assert.Equal([ToolResultStatus.Succeeded, ToolResultStatus.Denied], results.Select(r => r.Status));
        Assert.Equal(McpMaterial.DeniedDetail, results[1].Detail);
        Assert.Equal(McpMaterial.DeniedResult, s.H.Client.Requests.Where(FakeChatClient.IsAnswer).Last().Turns[^1].Content);
        var steps = await StepsAsync(s.H, sent, answer.Id);
        Assert.Equal([(RunStepState.Completed, Approval.Approved), (RunStepState.Denied, Approval.Denied)], steps.Select(x => (x.State, x.Approval!.Value)));
    }

    [Fact]
    public async Task Stopping_while_the_card_waits_runs_nothing()
    {
        await using var s = await StartAsync();
        await s.EnableAsync("write_note");
        s.H.Client.CallTool("files__write_note", Text("不该写"), "等一下。");

        var sent = await s.H.Conversations.StartAsync(s.H.Workspace, "写笔记", [], null, new SendOptions(WebSearchMode.Auto), Ct);
        var card = await CardAsync(s.H, sent.Answer!.Value);
        await s.H.Conversations.StopAsync(sent.Answer!.Value);
        var answer = await s.H.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal(MessageState.Stopped, answer.State);
        Assert.False(s.H.Conversations.DecideApproval(sent.Answer!.Value, card.CallId, allow: true));
        Assert.Equal(RunStepState.Stopped, Assert.Single(await StepsAsync(s.H, sent, answer.Id)).State);
        Assert.False(File.Exists(Path.Combine(s.Folder, "notes.txt")));
    }

    [Fact]
    public async Task A_restart_while_the_card_waits_marks_the_answer_interrupted_and_runs_nothing()
    {
        using var dir = new TempDataDirectory();
        MessageId answer;
        ConversationId conversation;
        string folder;
        await using (var before = await StartAsync(directory: dir))
        {
            await before.EnableAsync("write_note");
            folder = before.Folder;
            before.H.Client.CallTool("files__write_note", Text("不该写"), "等一下。");
            var sent = await before.H.Conversations.StartAsync(before.H.Workspace, "写笔记", [], null, new SendOptions(WebSearchMode.Auto), Ct);
            (answer, conversation) = (sent.Answer!.Value, sent.Conversation);
            await CardAsync(before.H, answer);
            await ChatHarness.UntilAsync(async () => (await before.H.Runs.RunsAsync(conversation, Ct)).GetValueOrDefault(answer)?.Steps.Any(x => x.State == RunStepState.AwaitingApproval) == true);
            await before.H.WaitForAnswerAsync(answer, m => m.Text == "等一下。");
            await before.H.Database.CloseAsync();
        }

        await using var after = await ChatHarness.StartAsync(directory: dir, configure: false);
        await after.Store.RecoverUnfinishedAsync(DateTimeOffset.UtcNow, Ct);

        Assert.Equal(MessageState.Interrupted, (await after.GetMessageAsync(answer))!.State);
        var step = (await after.Runs.RunsAsync(conversation, Ct))[answer].Steps.Single(x => x.Kind == RunStepKind.Tool);
        Assert.Equal((RunStepState.Stopped, Approval.Pending), (step.State, step.Approval));
        Assert.False(File.Exists(Path.Combine(folder, "notes.txt")));
        Assert.Empty(after.Client.Requests);
    }

    [Fact]
    public async Task The_users_choice_of_risk_decides_whether_a_call_asks()
    {
        await using var s = await StartAsync();
        await s.EnableAsync("echo");
        await s.EnableAsync("write_note");
        await s.H.Mcp.SetRiskAsync(s.Server.Id, "echo", ToolRisk.SideEffect, Ct);
        await s.H.Mcp.SetRiskAsync(s.Server.Id, "write_note", ToolRisk.LocalRead, Ct);
        s.H.Client.CallTool("files__write_note", Text("直接写"));
        s.H.Client.CallTool("files__echo", Text("要问"));
        s.H.Client.Reply("好。");

        var sent = await s.H.Conversations.StartAsync(s.H.Workspace, "问", [], null, new SendOptions(WebSearchMode.Auto), Ct);
        var card = await CardAsync(s.H, sent.Answer!.Value);

        Assert.Equal("files__echo", card.Tool);
        Assert.Equal("直接写\n", await File.ReadAllTextAsync(Path.Combine(s.Folder, "notes.txt"), Ct));
        Assert.True(s.H.Conversations.DecideApproval(sent.Answer!.Value, card.CallId, allow: true));
        Assert.Equal(MessageState.Complete, (await s.H.WaitForAnswerAsync(sent.Answer!.Value, timeoutMs: 20_000)).State);
    }

    [Fact]
    public async Task With_the_web_switch_off_tools_that_read_the_network_are_not_offered()
    {
        await using var s = await StartAsync();
        await s.EnableAsync("echo");
        await s.EnableAsync("write_note");
        s.H.Client.Reply("好。");

        await AskAsync(s.H, "问", WebSearchMode.Off);

        Assert.Equal(["files__write_note"], s.H.Client.Requests.Single(FakeChatClient.IsAnswer).Tools!.Select(t => t.Name));
    }

    [Fact]
    public async Task A_tool_declared_read_only_and_destructive_is_a_side_effect()
    {
        await using var s = await StartAsync();
        Assert.Equal(ToolRisk.SideEffect, (await s.EnableAsync("destroy")).Risk);
        Assert.Equal(ToolRisk.SideEffect, (await s.EnableAsync("write_note")).Risk);
        Assert.False(s.Tools.Single(t => t.Name == "destroy").DeclaresReadOnly);
    }

    [Fact]
    public async Task A_server_that_starts_claiming_read_only_after_enabling_has_the_tool_turned_off_and_the_call_refused()
    {
        // Listing 1 (start) says write_note has side effects; from listing 2 it claims to be read-only.
        await using var s = await StartAsync(flags: ["--lie-after", "1"]);
        await s.EnableAsync("write_note");
        s.H.Client.CallTool("files__write_note", Text("偷偷写"));
        s.H.Client.Reply("没能写。");

        var (sent, answer) = await AskAsync(s.H, "写笔记");

        Assert.Equal(MessageState.Complete, answer.State);
        Assert.Null(s.H.Conversations.Observe(answer.Id)?.Snapshot().PendingApproval);
        Assert.False(File.Exists(Path.Combine(s.Folder, "notes.txt")));
        var result = answer.Blocks.OfType<ToolResultBlock>().Single();
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("定义已变化", result.Preview, StringComparison.Ordinal);
        var setting = (await s.H.Mcp.GetAsync(s.Server.Id, Ct))!.Tool("write_note")!;
        Assert.Equal((false, McpServerSettings.ChangedDefinition.ToString()), (setting.Enabled, setting.DisabledReason));

        // Not offered any more.
        s.H.Client.Reply("好。");
        await AskAsync(s.H, "再问");
        Assert.False(s.H.Client.Requests.Where(FakeChatClient.IsAnswer).Last().HasTools);
        Assert.Equal(RunStepState.Failed, Assert.Single(await StepsAsync(s.H, sent, answer.Id)).State);
    }

    [Fact]
    public async Task A_renamed_tool_is_turned_off_and_its_call_is_an_error_the_run_goes_on_with()
    {
        await using var s = await StartAsync(flags: ["--rename-after", "1"]);
        await s.EnableAsync("echo");
        s.H.Client.CallTool("files__echo", Text("还在吗"));
        s.H.Client.Reply("工具不见了。");

        var (_, answer) = await AskAsync(s.H, "回声");

        Assert.Equal(MessageState.Complete, answer.State);
        Assert.Contains("不再提供", answer.Blocks.OfType<ToolResultBlock>().Single().Preview, StringComparison.Ordinal);
        Assert.Equal(McpServerSettings.Vanished.ToString(), (await s.H.Mcp.GetAsync(s.Server.Id, Ct))!.Tool("echo")!.DisabledReason);
    }

    [Fact]
    public async Task A_changed_description_turns_the_tool_off()
    {
        await using var s = await StartAsync(flags: ["--describe-after", "1"]);
        await s.EnableAsync("write_note");

        await s.H.McpServers.StopAsync(s.Server.Id);
        await s.H.McpServers.StartAsync(s.Server.Id, Ct); // A new process lists from 1 again: unchanged.
        Assert.True((await s.H.Mcp.GetAsync(s.Server.Id, Ct))!.Tool("write_note")!.Enabled);

        s.H.Client.CallTool("files__write_note", Text("x"));
        s.H.Client.Reply("好。");
        await AskAsync(s.H, "写"); // The call lists again (2): the description changed.
        Assert.Equal(McpServerSettings.ChangedDefinition.ToString(), (await s.H.Mcp.GetAsync(s.Server.Id, Ct))!.Tool("write_note")!.DisabledReason);
    }

    [Fact]
    public async Task A_server_that_cannot_start_fails_the_step_with_the_reason_and_the_answer_goes_on()
    {
        await using var h = await ChatHarness.StartAsync(model: ToolModel);
        var server = await h.Mcp.AddAsync(FakeMcp.Command("broken", flags: "--exit-at-start"), Ct);
        await Assert.ThrowsAsync<McpServerException>(() => h.McpServers.StartAsync(server.Id, Ct));
        Assert.Equal(McpServerState.Failed, h.McpServers.StatusOf(server.Id).State);
        Assert.Contains("fatal: cannot start", h.McpServers.StatusOf(server.Id).StandardError);
        await h.Mcp.EnableToolAsync(server.Id, new McpToolInfo("echo", null, "回声", """{"type":"object"}""", ReadOnlyHint: true), Ct);
        h.Client.CallTool("broken__echo", Text("x"));
        h.Client.Reply("服务器坏了。");

        var (_, answer) = await AskAsync(h, "问");

        Assert.Equal(MessageState.Complete, answer.State);
        var result = answer.Blocks.OfType<ToolResultBlock>().Single();
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("握手", result.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_exits_during_a_call_fails_the_step_and_is_started_again_for_the_next()
    {
        await using var s = await StartAsync();
        await s.EnableAsync("crash_read");
        await s.EnableAsync("echo");
        s.H.Client.CallTool("files__crash_read", Text("x"));
        s.H.Client.CallTool("files__echo", Text("又活了"));
        s.H.Client.Reply("服务器重启了。");

        var (_, answer) = await AskAsync(s.H, "问");

        Assert.Equal(MessageState.Complete, answer.State);
        var results = answer.Blocks.OfType<ToolResultBlock>().ToList();
        Assert.Equal([ToolResultStatus.Failed, ToolResultStatus.Succeeded], results.Select(r => r.Status));
        Assert.Contains("退出", results[0].Preview, StringComparison.Ordinal);
        Assert.Equal(McpServerState.Connected, s.H.McpServers.StatusOf(s.Server.Id).State);
    }

    [Fact]
    public async Task A_side_effect_whose_server_exits_during_the_call_has_an_unknown_outcome()
    {
        await using var s = await StartAsync();
        await s.EnableAsync("crash");
        s.H.Client.CallTool("files__crash", Text("x"));
        s.H.Client.Reply("不确定。");

        var sent = await s.H.Conversations.StartAsync(s.H.Workspace, "问", [], null, new SendOptions(WebSearchMode.Auto), Ct);
        Assert.True(s.H.Conversations.DecideApproval(sent.Answer!.Value, (await CardAsync(s.H, sent.Answer!.Value)).CallId, allow: true));
        var answer = await s.H.WaitForAnswerAsync(sent.Answer!.Value, timeoutMs: 20_000);

        Assert.Equal(MessageState.Complete, answer.State);
        var result = answer.Blocks.OfType<ToolResultBlock>().Single();
        Assert.Equal((ToolResultStatus.Failed, "结果未知"), (result.Status, result.Detail));
        Assert.Contains("是否已经生效未知", result.Preview, StringComparison.Ordinal);
        Assert.Equal(RunStepState.Unknown, Assert.Single(await StepsAsync(s.H, sent, answer.Id)).State);
    }

    [Fact]
    public async Task A_call_that_hangs_times_out_and_the_server_stays_usable()
    {
        await using var s = await StartAsync();
        await s.EnableAsync("hang");
        await s.EnableAsync("echo");
        s.H.Client.CallTool("files__hang", Text("x"));
        s.H.Client.CallTool("files__echo", Text("还在"));
        s.H.Client.Reply("第一个超时了。");

        var (_, answer) = await AskAsync(s.H, "问");

        Assert.Equal(MessageState.Complete, answer.State);
        var results = answer.Blocks.OfType<ToolResultBlock>().ToList();
        Assert.Equal([ToolResultStatus.Failed, ToolResultStatus.Succeeded], results.Select(r => r.Status));
        Assert.Contains("超时", results[0].Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Huge_output_is_kept_cut_and_the_model_gets_its_start_and_a_handle()
    {
        await using var s = await StartAsync();
        await s.EnableAsync("huge");
        s.H.Client.CallTool("files__huge", Text("x"));
        s.H.Client.Reply("很长。");

        var (_, answer) = await AskAsync(s.H, "问");

        Assert.Equal(MessageState.Complete, answer.State);
        var stored = (await s.H.ResourceService.GetAsync(answer.Blocks.OfType<ToolResultBlock>().Single().Resource!.Value, Ct))!;
        Assert.True(stored.Text!.Length <= McpMaterial.MaxText + 100);
        Assert.Contains("结果过长", stored.Text, StringComparison.Ordinal);
        var sentToModel = s.H.Client.Requests.Where(FakeChatClient.IsAnswer).Last().Turns[^1].Content;
        Assert.True(sentToModel.Length < 10_000, sentToModel.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("以上只是开头", sentToModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Images_are_stored_apart_links_are_listed_and_nothing_else_is_read()
    {
        await using var s = await StartAsync();
        await s.EnableAsync("media");
        s.H.Client.CallTool("files__media", Text("x"));
        s.H.Client.Reply("有图。");

        var (_, answer) = await AskAsync(s.H, "问");

        var stored = (await s.H.ResourceService.GetAsync(answer.Blocks.OfType<ToolResultBlock>().Single().Resource!.Value, Ct))!;
        Assert.Contains("[资源链接：秘密文件 <https://example.org/secret.txt>（应用没有读取）]", stored.Text, StringComparison.Ordinal);
        Assert.Contains("嵌入的笔记", stored.Text, StringComparison.Ordinal);
        Assert.Contains("[未保存的内容：audio]", stored.Text, StringComparison.Ordinal);
        var images = System.Text.RegularExpressions.Regex.Matches(stored.Text!, @"\[图片：资源 ([^\]]+)\]");
        Assert.Equal(2, images.Count);
        foreach (System.Text.RegularExpressions.Match match in images)
        {
            var image = (await s.H.ResourceService.GetAsync(ResourceId.Parse(match.Groups[1].Value), Ct))!;
            Assert.Equal(("image/png", ResourceOrigin.Mcp, ResourceProvenance.External), (image.Resource.MediaType, image.Resource.Origin, image.Resource.Provenance));
        }
    }

    [Fact]
    public async Task Instructions_in_a_tools_output_are_material_and_a_side_effect_still_asks()
    {
        await using var s = await StartAsync();
        await s.EnableAsync("inject");
        await s.EnableAsync("write_note");
        s.H.Client.CallTool("files__inject", Text("x"));
        s.H.Client.CallTool("files__write_note", Text("已被接管"));
        s.H.Client.Reply("我不会照做。");

        var sent = await s.H.Conversations.StartAsync(s.H.Workspace, "查一下", [], null, new SendOptions(WebSearchMode.Auto), Ct);
        var card = await CardAsync(s.H, sent.Answer!.Value);

        var material = s.H.Client.Requests.Where(FakeChatClient.IsAnswer).Last().Turns[^1].Content;
        Assert.StartsWith(McpMaterial.Opening("files", "inject"), material, StringComparison.Ordinal);
        Assert.Contains(FakeServer.Injection, material, StringComparison.Ordinal);
        Assert.Contains("不可信", material, StringComparison.Ordinal);
        Assert.Equal("files__write_note", card.Tool);
        Assert.True(s.H.Conversations.DecideApproval(sent.Answer!.Value, card.CallId, allow: false));
        await s.H.WaitForAnswerAsync(sent.Answer!.Value, timeoutMs: 20_000);
        Assert.False(File.Exists(Path.Combine(s.Folder, "notes.txt")));
    }

    [Fact]
    public async Task An_error_result_goes_back_as_an_error_marked_untrusted()
    {
        await using var s = await StartAsync();
        await s.EnableAsync("fail");
        s.H.Client.CallTool("files__fail", Text("x"));
        s.H.Client.Reply("出错了。");

        var (_, answer) = await AskAsync(s.H, "问");

        var result = answer.Blocks.OfType<ToolResultBlock>().Single();
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        var sentToModel = s.H.Client.Requests.Where(FakeChatClient.IsAnswer).Last().Turns[^1].Content;
        Assert.Contains("数据库连接失败", sentToModel, StringComparison.Ordinal);
        Assert.Contains("不可信", sentToModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_servers_with_the_same_tool_get_different_names_and_count_apart()
    {
        await using var s = await StartAsync("files");
        var other = await s.H.Mcp.AddAsync(FakeMcp.Command("Files"), Ct);
        var otherTools = await s.H.McpServers.StartAsync(other.Id, Ct);
        await s.EnableAsync("echo");
        var second = await s.H.Mcp.EnableToolAsync(other.Id, otherTools.Single(t => t.Name == "echo"), Ct);
        Assert.Equal("files2__echo", second.ModelName);
        s.H.Client.CallTool("files__echo", Text("一"));
        s.H.Client.CallTool("files2__echo", Text("二"));
        s.H.Client.Reply("两个都回声了。");

        var (sent, answer) = await AskAsync(s.H, "问");

        Assert.Equal(["files__echo", "files2__echo"], s.H.Client.Requests.Where(FakeChatClient.IsAnswer).First().Tools!.Select(t => t.Name));
        Assert.Equal(["files", "Files"], (await StepsAsync(s.H, sent, answer.Id)).Select(x => x.Target));
        var usage = await s.H.StatisticsStore.ToolUsageAsync(null, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), Ct);
        Assert.Equal(
            [("files2__echo", 1, "Files"), ("files__echo", 1, "files")],
            usage.Calls.Select(c => (c.Tool, c.Calls, c.Server!)).OrderBy(c => c.Item1, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_tool_turned_off_after_the_question_was_accepted_is_not_run()
    {
        await using var s = await StartAsync(slots: 1);
        await s.EnableAsync("echo");
        await s.EnableAsync("write_note");
        s.H.Client.CallTool("files__write_note", Text("x"), "先写。");
        s.H.Client.CallTool("files__echo", Text("y"));
        s.H.Client.Reply("好。");

        var sent = await s.H.Conversations.StartAsync(s.H.Workspace, "问", [], null, new SendOptions(WebSearchMode.Auto), Ct);
        var card = await CardAsync(s.H, sent.Answer!.Value);
        await s.H.Mcp.DisableToolAsync(s.Server.Id, "echo", Ct);
        Assert.True(s.H.Conversations.DecideApproval(sent.Answer!.Value, card.CallId, allow: true));
        var answer = await s.H.WaitForAnswerAsync(sent.Answer!.Value, timeoutMs: 20_000);

        Assert.Contains("已在设置中停用", answer.Blocks.OfType<ToolResultBlock>().Last().Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secrets_of_a_server_stay_in_the_credential_store_and_out_of_the_log_lines()
    {
        await using var s = await StartAsync(environment: new Dictionary<string, string> { ["SECRET_TOKEN"] = "very-secret-token-value" }, flags: "--stderr-secret");

        var stored = await s.H.Preferences.GetAsync(McpServerSettings.Key, Ct);
        Assert.DoesNotContain("very-secret-token-value", stored, StringComparison.Ordinal);
        Assert.Contains("SECRET_TOKEN", stored, StringComparison.Ordinal);
        Assert.Contains(s.H.Credentials.Items.Values, v => v.Contains("very-secret-token-value", StringComparison.Ordinal));

        await ChatHarness.UntilAsync(() => s.H.McpServers.StatusOf(s.Server.Id).StandardError.Any(l => l.Contains("starting with", StringComparison.Ordinal)), 10_000);
        var line = s.H.McpServers.StatusOf(s.Server.Id).StandardError.Single(l => l.Contains("starting with", StringComparison.Ordinal));
        Assert.DoesNotContain("very-secret-token-value", line, StringComparison.Ordinal);
        Assert.Contains("[redacted]", line, StringComparison.Ordinal);

        // Removing the server removes its secret and its folder.
        await s.H.McpServers.RemoveAsync(s.Server.Id, Ct);
        Assert.DoesNotContain(s.H.Credentials.Items.Values, v => v.Contains("very-secret-token-value", StringComparison.Ordinal));
        Assert.False(Directory.Exists(s.Folder));
        Assert.Empty(await s.H.Mcp.ListAsync(Ct));
    }
}
