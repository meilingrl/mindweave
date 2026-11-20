using System.Text;
using System.Text.Json;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Preferences;
using Mindweave.Application.Storage;
using Mindweave.Application.Tools;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Phase 3 acceptance (requirements §10.4.5 and §11, agent platform phase 3; architecture §15.13) through the real store,
/// coordinator and run loop, with a scripted search service and page reader (no network): when web tools are offered, the
/// switch's three positions, 出处 and numbers that name nothing, untrusted material, approval for invented addresses (allow,
/// deny, stop and restart while waiting, the slot given up), failures of the service and of pages, later context and
/// statistics. The HTTP side (address checks, redirects, limits, Tavily's errors) is tested in Tools/.
/// </summary>
public class WebRunFlowTests
{
    private const string Key = "tvly-secret-key-1234567890";

    private static readonly ModelSettingsInput ToolModel = new(
        "fake", "https://fake.example/v1", "fake-model", "sk-test",
        Capabilities: ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = true });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Query(string q) => JsonSerializer.Serialize(new { query = q });

    private static string Url(string u) => JsonSerializer.Serialize(new { url = u });

    private static async Task<ChatHarness> StartAsync(bool key = true, int? slots = null, TempDataDirectory? directory = null)
    {
        var h = await ChatHarness.StartAsync(model: ToolModel, slots: slots, directory: directory);
        if (key)
        {
            await h.WebSearch.SetKeyAsync(Key, Ct);
        }

        return h;
    }

    private static async Task<(SendOutcome Sent, Message Answer)> AskAsync(ChatHarness h, string question, WebSearchMode mode = WebSearchMode.Auto)
    {
        var sent = await h.Conversations.StartAsync(h.Workspace, question, [], null, new SendOptions(mode), Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        return (sent, await h.WaitForAnswerAsync(sent.Answer!.Value));
    }

    [Fact]
    public async Task Without_a_key_or_with_the_switch_off_no_web_tool_is_offered()
    {
        await using var h = await StartAsync(key: false);
        var (_, plain) = await AskAsync(h, "今天的新闻？");
        Assert.Equal(MessageState.Complete, plain.State);
        Assert.False(Assert.Single(h.Client.Requests, FakeChatClient.IsAnswer).HasTools);

        await h.WebSearch.SetKeyAsync(Key, Ct);
        await AskAsync(h, "再问一次", WebSearchMode.Off);
        Assert.All(h.Client.Requests.Where(FakeChatClient.IsAnswer), r => Assert.False(r.HasTools));
        Assert.Empty(h.Search.Searches);
    }

    [Fact]
    public async Task Auto_offers_both_web_tools_numbers_results_and_turns_cited_numbers_into_citations()
    {
        await using var h = await StartAsync();
        h.Client.CallTool(WebSearchTool.ToolName, Query("蒸汽机 发明"));
        h.Client.Reply("瓦特改良了蒸汽机[1]。第一条铁路 1825 年通车[2]。这句话没有来源[9]。");

        var (sent, answer) = await AskAsync(h, "蒸汽机是谁改良的？");

        Assert.Equal(MessageState.Complete, answer.State);
        var first = h.Client.Requests.Where(FakeChatClient.IsAnswer).First();
        Assert.Equal([WebSearchTool.ToolName, FetchPageTool.ToolName], first.Tools!.Select(t => t.Name));

        // The search went out with the key and the query; its results reached the model numbered and marked untrusted.
        var search = Assert.Single(h.Search.Searches);
        Assert.Equal(("蒸汽机 发明", Key, WebSearchTool.MaxResults), search);
        var material = h.Client.Requests.Where(FakeChatClient.IsAnswer).Last().Turns[^1].Content;
        Assert.StartsWith(WebMaterial.Opening, material, StringComparison.Ordinal);
        Assert.Contains("[1] 蒸汽机 - 百科\n网址：https://example.org/steam", material, StringComparison.Ordinal);
        Assert.Contains("[2] 铁路的历史", material, StringComparison.Ordinal);
        Assert.EndsWith(WebMaterial.Closing, material, StringComparison.Ordinal);

        // [1] and [2] are 出处; [9] names nothing and stays text. The text itself is unchanged.
        var citations = ContentBlocks.CitationsOf(answer.Blocks);
        Assert.Equal([1, 2], citations.Select(c => c.Number));
        Assert.Equal(("蒸汽机 - 百科", new Uri("https://example.org/steam")), (citations[0].Title, citations[0].Url));
        Assert.Contains("[9]", answer.Text, StringComparison.Ordinal);
        Assert.IsType<CitationBlock>(answer.Blocks[^1]);

        // The results are one stored web resource, external; a citation's range is its own result.
        var stored = (await h.ResourceService.GetAsync(citations[0].Resource, Ct))!;
        Assert.Equal((ResourceOrigin.Web, ResourceProvenance.External, sent.Conversation), (stored.Resource.Origin, stored.Resource.Provenance, stored.Resource.ConversationId));
        var passage = stored.Text![citations[0].Start..citations[0].End];
        Assert.StartsWith("[1] 蒸汽机 - 百科", passage, StringComparison.Ordinal);
        Assert.DoesNotContain("[2]", passage, StringComparison.Ordinal);

        // The step went to the search service's host; its query is the step's input.
        var step = (await h.Runs.RunsAsync(sent.Conversation, Ct))[answer.Id].Steps.Single(s => s.Kind == RunStepKind.Tool);
        Assert.Equal(("api.tavily.com", "蒸汽机 发明", RunStepState.Completed), (step.Target, step.Input, step.State));
    }

    [Fact]
    public async Task On_searches_the_question_before_the_first_request()
    {
        await using var h = await StartAsync();
        h.Client.Reply("1 加 1 等于 2。");

        var (sent, answer) = await AskAsync(h, "1 加 1 等于几？", WebSearchMode.On);

        Assert.Equal(MessageState.Complete, answer.State);
        Assert.Equal("1 加 1 等于几？", Assert.Single(h.Search.Searches).Query);
        var request = Assert.Single(h.Client.Requests, FakeChatClient.IsAnswer);
        var question = request.Turns[^1].Content;
        Assert.StartsWith(WebMaterial.PreSearchLead, question, StringComparison.Ordinal);
        Assert.Contains(WebMaterial.Opening, question, StringComparison.Ordinal);
        Assert.EndsWith("1 加 1 等于几？", question, StringComparison.Ordinal);
        Assert.True(request.HasTools);

        var call = answer.Blocks.OfType<ToolCallBlock>().Single();
        Assert.Equal((WebMaterial.PreSearchCallId, WebSearchTool.ToolName), (call.CallId, call.Tool));
        Assert.Equal(RunStepKind.Tool, (await h.Runs.RunsAsync(sent.Conversation, Ct))[answer.Id].Steps[0].Kind);
    }

    [Fact]
    public async Task A_page_from_the_results_is_read_without_asking_and_its_citation_points_at_the_cited_passage()
    {
        await using var h = await StartAsync();
        h.Pages.Page(
            "https://example.org/steam", "蒸汽机的历史",
            "开头介绍。\n\n" + new string('甲', 1500) + "\n\n纽科门在 1712 年制造了第一台实用的蒸汽机。\n\n" + new string('乙', 1500) + "\n\n瓦特在 1769 年取得了分离冷凝器的专利。",
            "https://example.org/watt");
        h.Client.CallTool(WebSearchTool.ToolName, Query("蒸汽机"));
        h.Client.CallTool(FetchPageTool.ToolName, Url("https://example.org/steam"));
        h.Client.CallTool(FetchPageTool.ToolName, Url("https://example.org/watt"));
        h.Client.Reply("瓦特取得了分离冷凝器的专利[1]。");
        h.Pages.Page("https://example.org/watt", "瓦特", "瓦特的生平。");

        var (sent, answer) = await AskAsync(h, "瓦特做了什么？");

        Assert.Equal(MessageState.Complete, answer.State);
        Assert.Null(h.Conversations.Observe(answer.Id)?.Snapshot().PendingApproval);
        Assert.Equal(["https://example.org/steam", "https://example.org/watt"], h.Pages.Fetched.Select(u => u.AbsoluteUri));

        // The page keeps the number its search result had; the citation now points at the page, at the cited passage.
        var citation = Assert.Single(ContentBlocks.CitationsOf(answer.Blocks));
        Assert.Equal((1, "蒸汽机的历史"), (citation.Number, citation.Title));
        var page = (await h.ResourceService.GetAsync(citation.Resource, Ct))!;
        Assert.Equal((ResourceOrigin.Web, new Uri("https://example.org/steam")), (page.Resource.Origin, page.Resource.SourceUrl));
        Assert.Contains("分离冷凝器", page.Text![citation.Start..citation.End], StringComparison.Ordinal);
        Assert.All((await h.Runs.RunsAsync(sent.Conversation, Ct))[answer.Id].Steps, s => Assert.Null(s.Approval));
    }

    [Fact]
    public async Task An_address_the_user_wrote_is_read_without_asking()
    {
        await using var h = await StartAsync();
        h.Pages.Page("https://docs.example.net/guide", "指南", "指南正文。");
        h.Client.CallTool(FetchPageTool.ToolName, Url("https://docs.example.net/guide"));
        h.Client.Reply("读完了[1]。");

        var (_, answer) = await AskAsync(h, "帮我总结 https://docs.example.net/guide。");

        Assert.Equal(MessageState.Complete, answer.State);
        Assert.Single(h.Pages.Fetched);
        Assert.Equal("指南", Assert.Single(ContentBlocks.CitationsOf(answer.Blocks)).Title);
    }

    [Fact]
    public async Task An_invented_address_waits_for_approval_without_its_slot_and_is_read_once_allowed()
    {
        await using var h = await StartAsync(slots: 1);
        h.Pages.Page("https://invented.example/secret?q=data", "编出来的网页", "正文。");
        h.Client.CallTool(FetchPageTool.ToolName, Url("https://invented.example/secret?q=data"), "我想读一个网页。");

        var sent = await h.Conversations.StartAsync(h.Workspace, "问", [], null, new SendOptions(WebSearchMode.Auto), Ct);
        var live = h.Conversations.Observe(sent.Answer!.Value)!;
        await ChatHarness.UntilAsync(() => Task.FromResult(live.Snapshot().PendingApproval is not null));
        var card = live.Snapshot().PendingApproval!;
        Assert.Equal((FetchPageTool.ToolName, "https://invented.example/secret?q=data"), (card.Tool, card.Target));
        Assert.Empty(h.Pages.Fetched);
        var waiting = (await h.Runs.RunsAsync(sent.Conversation, Ct))[sent.Answer!.Value].Steps.Single(s => s.Kind == RunStepKind.Tool);
        Assert.Equal((RunStepState.AwaitingApproval, Approval.Pending), (waiting.State, waiting.Approval));

        // With one slot, another question still runs while the card waits: the run gave its slot up.
        h.Client.Reply("另一个回答。");
        var other = await h.Conversations.StartAsync(h.Workspace, "另一个问题", [], null, new SendOptions(WebSearchMode.Off), Ct);
        Assert.Equal(MessageState.Complete, (await h.WaitForAnswerAsync(other.Answer!.Value)).State);

        h.Client.Reply("读到了[1]。");
        Assert.True(h.Conversations.DecideApproval(sent.Answer!.Value, card.CallId, allow: true));
        Assert.False(h.Conversations.DecideApproval(sent.Answer!.Value, card.CallId, allow: true));
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal(MessageState.Complete, answer.State);
        Assert.Single(h.Pages.Fetched);
        Assert.Equal("编出来的网页", Assert.Single(ContentBlocks.CitationsOf(answer.Blocks)).Title);
        var step = (await h.Runs.RunsAsync(sent.Conversation, Ct))[answer.Id].Steps.Single(s => s.Kind == RunStepKind.Tool);
        Assert.Equal((RunStepState.Completed, Approval.Approved, "invented.example"), (step.State, step.Approval, step.Target));
    }

    [Fact]
    public async Task A_refused_address_is_not_read_and_the_model_is_told()
    {
        await using var h = await StartAsync();
        h.Client.CallTool(FetchPageTool.ToolName, Url("https://invented.example/x"));
        h.Client.Reply("没能读取，根据已知回答。");

        var sent = await h.Conversations.StartAsync(h.Workspace, "问", [], null, new SendOptions(WebSearchMode.Auto), Ct);
        var live = h.Conversations.Observe(sent.Answer!.Value)!;
        await ChatHarness.UntilAsync(() => Task.FromResult(live.Snapshot().PendingApproval is not null));
        Assert.True(h.Conversations.DecideApproval(sent.Answer!.Value, live.Snapshot().PendingApproval!.CallId, allow: false));
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal(MessageState.Complete, answer.State);
        Assert.Empty(h.Pages.Fetched);
        var result = answer.Blocks.OfType<ToolResultBlock>().Single();
        Assert.Equal(ToolResultStatus.Denied, result.Status);
        Assert.Equal(WebMaterial.DeniedResult, h.Client.Requests.Where(FakeChatClient.IsAnswer).Last().Turns[^1].Content);
        var step = (await h.Runs.RunsAsync(sent.Conversation, Ct))[answer.Id].Steps.Single(s => s.Kind == RunStepKind.Tool);
        Assert.Equal((RunStepState.Denied, Approval.Denied), (step.State, step.Approval));
        Assert.Empty(ContentBlocks.CitationsOf(answer.Blocks));
    }

    [Fact]
    public async Task Stopping_while_the_card_waits_stops_the_answer_and_reads_nothing()
    {
        await using var h = await StartAsync();
        h.Client.CallTool(FetchPageTool.ToolName, Url("https://invented.example/x"), "等一下。");

        var sent = await h.Conversations.StartAsync(h.Workspace, "问", [], null, new SendOptions(WebSearchMode.Auto), Ct);
        var live = h.Conversations.Observe(sent.Answer!.Value)!;
        await ChatHarness.UntilAsync(() => Task.FromResult(live.Snapshot().PendingApproval is not null));
        var callId = live.Snapshot().PendingApproval!.CallId;
        await h.Conversations.StopAsync(sent.Answer!.Value);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal((MessageState.Stopped, "等一下。"), (answer.State, answer.Text));
        Assert.Null(live.Snapshot().PendingApproval);
        Assert.False(h.Conversations.DecideApproval(sent.Answer!.Value, callId, allow: true));
        Assert.Empty(h.Pages.Fetched);
        var step = (await h.Runs.RunsAsync(sent.Conversation, Ct))[answer.Id].Steps.Single(s => s.Kind == RunStepKind.Tool);
        Assert.Equal(RunStepState.Stopped, step.State);
        Assert.Single(h.Client.Requests, FakeChatClient.IsAnswer);
    }

    [Fact]
    public async Task A_restart_while_the_card_waits_marks_the_answer_interrupted_and_reads_nothing()
    {
        using var dir = new TempDataDirectory();
        MessageId answer;
        ConversationId conversation;
        await using (var before = await StartAsync(directory: dir))
        {
            before.Client.CallTool(FetchPageTool.ToolName, Url("https://invented.example/x"), "等一下。");
            var sent = await before.Conversations.StartAsync(before.Workspace, "问", [], null, new SendOptions(WebSearchMode.Auto), Ct);
            (answer, conversation) = (sent.Answer!.Value, sent.Conversation);
            var live = before.Conversations.Observe(answer)!;
            await ChatHarness.UntilAsync(() => Task.FromResult(live.Snapshot().PendingApproval is not null));
            await ChatHarness.UntilAsync(async () => (await before.Runs.RunsAsync(conversation, Ct)).GetValueOrDefault(answer)?.Steps.Any(s => s.State == RunStepState.AwaitingApproval) == true);
            await before.WaitForAnswerAsync(answer, m => m.Text == "等一下。");
            await before.Database.CloseAsync();
        }

        await using var after = await ChatHarness.StartAsync(directory: dir, configure: false);
        await after.Store.RecoverUnfinishedAsync(DateTimeOffset.UtcNow, Ct);

        Assert.Equal(MessageState.Interrupted, (await after.GetMessageAsync(answer))!.State);
        var step = (await after.Runs.RunsAsync(conversation, Ct))[answer].Steps.Single(s => s.Kind == RunStepKind.Tool);
        Assert.Equal((RunStepState.Stopped, Approval.Pending), (step.State, step.Approval));
        Assert.Empty(after.Pages.Fetched);
        Assert.Empty(after.Client.Requests);
    }

    [Fact]
    public async Task Links_on_a_page_read_may_be_read_next_without_asking()
    {
        await using var h = await StartAsync();
        h.Pages.Page("https://a.example/", "A", "A 的正文。", "https://b.example/next");
        h.Pages.Page("https://b.example/next", "B", "B 的正文。");
        h.Client.CallTool(FetchPageTool.ToolName, Url("https://a.example/"));
        h.Client.CallTool(FetchPageTool.ToolName, Url("https://b.example/next#part"));
        h.Client.Reply("见[1]与[2]。");

        var (_, answer) = await AskAsync(h, "读 https://a.example/");

        Assert.Equal(MessageState.Complete, answer.State);
        Assert.Equal(["A", "B"], ContentBlocks.CitationsOf(answer.Blocks).Select(c => c.Title));
    }

    [Fact]
    public async Task A_page_refused_by_the_reader_is_an_error_the_model_sees_and_the_run_goes_on()
    {
        await using var h = await StartAsync();
        h.Search.Returns(new SearchHit("内网", new Uri("http://intranet.example/"), "内部页面"));
        h.Pages.Fails("http://intranet.example/", FetchFailureKind.BlockedAddress, "拒绝读取：intranet.example 指向本机或局域网地址。");
        h.Client.CallTool(WebSearchTool.ToolName, Query("内网"));
        h.Client.CallTool(FetchPageTool.ToolName, Url("http://intranet.example/"));
        h.Client.Reply("那个网页不能读取。");

        var (_, answer) = await AskAsync(h, "问");

        Assert.Equal(MessageState.Complete, answer.State);
        var failed = answer.Blocks.OfType<ToolResultBlock>().Last();
        Assert.Equal(ToolResultStatus.Failed, failed.Status);
        Assert.Contains("局域网", failed.Preview, StringComparison.Ordinal);
        Assert.Contains("局域网", h.Client.Requests.Where(FakeChatClient.IsAnswer).Last().Turns[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_key_or_used_up_credits_are_said_in_the_step_and_three_failures_withdraw_search()
    {
        await using var h = await StartAsync();
        h.Search.Fails(SearchFailureKind.KeyRejected, "搜索服务拒绝了密钥，请在设置 › 工具 › 网络搜索中检查。");
        h.Search.Fails(SearchFailureKind.QuotaExhausted, "搜索服务的额度已用完。");
        h.Search.Fails(SearchFailureKind.RateLimited, "搜索过于频繁，请稍后再试。");
        h.Client.CallTool(WebSearchTool.ToolName, Query("一"));
        h.Client.CallTool(WebSearchTool.ToolName, Query("二"));
        h.Client.CallTool(WebSearchTool.ToolName, Query("三"));
        h.Client.Reply("搜索不可用，凭已知回答。");

        var (_, answer) = await AskAsync(h, "问");

        Assert.Equal(MessageState.Complete, answer.State);
        var results = answer.Blocks.OfType<ToolResultBlock>().ToList();
        Assert.All(results, r => Assert.Equal(ToolResultStatus.Failed, r.Status));
        Assert.Contains("拒绝了密钥", results[0].Preview, StringComparison.Ordinal);
        Assert.Contains("额度已用完", results[1].Preview, StringComparison.Ordinal);
        Assert.Contains("过于频繁", results[2].Preview, StringComparison.Ordinal);
        Assert.DoesNotContain(WebSearchTool.ToolName, h.Client.Requests.Where(FakeChatClient.IsAnswer).Last().Tools?.Select(t => t.Name) ?? []);
    }

    [Fact]
    public async Task A_key_removed_after_the_question_was_accepted_fails_the_search_with_the_reason()
    {
        await using var h = await StartAsync(slots: 1);

        // The only slot is taken by another answer, so the run is accepted (with web tools) but waits.
        var held = h.Client.Hold();
        var other = await h.Conversations.StartAsync(h.Workspace, "先问", [], null, new SendOptions(WebSearchMode.Off), Ct);
        await held.Started.Task.WaitAsync(Ct);
        h.Client.CallTool(WebSearchTool.ToolName, Query("x"));
        h.Client.Reply("好。");
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", [], null, new SendOptions(WebSearchMode.Auto), Ct);
        await h.WebSearch.RemoveKeyAsync(Ct);
        held.Finish("先答。");
        await h.WaitForAnswerAsync(other.Answer!.Value);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        var result = answer.Blocks.OfType<ToolResultBlock>().Single();
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("还没有设置搜索服务的密钥", result.Preview, StringComparison.Ordinal);
        Assert.Empty(h.Search.Searches);
    }

    [Fact]
    public async Task Later_questions_and_summaries_carry_the_citation_list_as_text()
    {
        await using var h = await StartAsync();
        h.Client.CallTool(WebSearchTool.ToolName, Query("蒸汽机"));
        h.Client.Reply("瓦特改良了蒸汽机[1]。");
        var (sent, _) = await AskAsync(h, "蒸汽机？");

        h.Client.Reply("好的。");
        var next = await h.Conversations.AskAsync(sent.Conversation, "然后呢？", [], new SendOptions(WebSearchMode.Off), Ct);
        await h.WaitForAnswerAsync(next.Answer!.Value);
        var earlier = h.Client.Requests.Where(FakeChatClient.IsAnswer).Last().Turns.Single(t => t.Role == Application.Ai.ChatRole.Assistant);
        Assert.Contains("瓦特改良了蒸汽机[1]。\n\n出处：\n[1] 蒸汽机 - 百科 https://example.org/steam", earlier.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("1769", earlier.Content, StringComparison.Ordinal);

        // Q14: a summary's input has the [n] and the list as text; nothing in it is a link.
        Assert.True((await h.SummaryService.SummarizeAsync(sent.Conversation, Ct)).Accepted);
        await ChatHarness.UntilAsync(() => Task.FromResult(h.Client.Requests.Any(FakeChatClient.IsKnowledgeSummary)));
        var summaryInput = string.Join("\n", h.Client.Requests.First(FakeChatClient.IsKnowledgeSummary).Turns.Select(t => t.Content));
        Assert.Contains("[1] 蒸汽机 - 百科 https://example.org/steam", summaryInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retry_uses_the_switch_at_that_moment()
    {
        await using var h = await StartAsync();
        h.Client.Reply("不搜索。");
        var (sent, first) = await AskAsync(h, "问", WebSearchMode.Off);

        h.Client.Reply("这次也回答。");
        var retry = await h.Conversations.RetryAsync(sent.Conversation, first.Id, new SendOptions(WebSearchMode.On), Ct);
        await h.WaitForAnswerAsync(retry.Answer!.Value);
        Assert.Single(h.Search.Searches);
    }

    [Fact]
    public async Task Search_and_page_calls_are_counted_per_tool_in_statistics()
    {
        await using var h = await StartAsync();
        h.Pages.Page("https://example.org/steam", "蒸汽机", "正文。");
        h.Client.CallTool(WebSearchTool.ToolName, Query("一"));
        h.Client.CallTool(WebSearchTool.ToolName, Query("二"));
        h.Client.CallTool(FetchPageTool.ToolName, Url("https://example.org/steam"));
        h.Client.Reply("好[1]。");
        await AskAsync(h, "问");

        var usage = await h.StatisticsStore.ToolUsageAsync(h.Workspace, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), Ct);
        Assert.Equal(2, usage.Calls.Single(c => c.Tool == WebSearchTool.ToolName).Calls);
        Assert.Equal(1, usage.Calls.Single(c => c.Tool == FetchPageTool.ToolName).Calls);
    }

    [Fact]
    public async Task The_key_is_kept_only_in_the_credential_store_never_in_the_database_steps_or_blocks()
    {
        await using var h = await StartAsync();
        h.Client.CallTool(WebSearchTool.ToolName, Query("蒸汽机"));
        h.Client.Reply("好[1]。");
        var (sent, answer) = await AskAsync(h, "问");

        var reference = await h.Preferences.GetAsync(WebSearchSettingsService.CredentialKey, Ct);
        Assert.Equal(Key, await h.Credentials.ReadAsync(reference!, Ct));
        Assert.NotEqual(Key, reference);
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(answer.Blocks.Select(b => b.ToString())), StringComparison.Ordinal);
        Assert.DoesNotContain(h.Client.Requests, r => r.Turns.Any(t => t.Content.Contains(Key, StringComparison.Ordinal)));
        foreach (var file in Directory.EnumerateFiles(h.DataDirectory, "*", SearchOption.AllDirectories))
        {
            // The database is still open: Windows only lets it be read when the read shares it.
            using var copy = new MemoryStream();
            await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                await stream.CopyToAsync(copy, Ct);
            }

            var bytes = copy.ToArray();
            Assert.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Key)) >= 0, file);
        }

        Assert.NotNull(sent.Answer);
    }

    [Fact]
    public async Task Settings_store_the_default_replace_and_remove_the_key_and_test_it()
    {
        await using var h = await StartAsync(key: false);
        Assert.Equal(new WebSearchState("tavily", "Tavily", false, WebSearchMode.Auto), await h.WebSearch.GetAsync(Ct));
        Assert.Equal(SearchFailureKind.MissingKey, (await h.WebSearch.TestAsync(null, Ct)).Kind);

        await h.WebSearch.SetKeyAsync("  first-key  ", Ct);
        var firstReference = await h.Preferences.GetAsync(WebSearchSettingsService.CredentialKey, Ct);
        await h.WebSearch.SetKeyAsync("second-key", Ct);
        Assert.Null(await h.Credentials.ReadAsync(firstReference!, Ct));
        Assert.Equal("second-key", await h.WebSearch.ReadKeyAsync(Ct));

        Assert.True((await h.WebSearch.TestAsync(null, Ct)).Succeeded);
        Assert.Equal(("Mindweave", "second-key", 1), h.Search.Searches.Last());
        h.Search.Fails(SearchFailureKind.KeyRejected, "搜索服务拒绝了密钥。");
        var failed = await h.WebSearch.TestAsync("typed-key", Ct);
        Assert.Equal((false, SearchFailureKind.KeyRejected), (failed.Succeeded, failed.Kind));
        Assert.Equal("typed-key", h.Search.Searches.Last().Key);

        await h.WebSearch.SetDefaultModeAsync(WebSearchMode.On, Ct);
        await h.WebSearch.RemoveKeyAsync(Ct);
        Assert.Equal(new WebSearchState("tavily", "Tavily", false, WebSearchMode.On), await h.WebSearch.GetAsync(Ct));
        Assert.Equal("on", await h.Preferences.GetAsync(WebSearchSettingsService.DefaultKey, Ct));
    }
}
