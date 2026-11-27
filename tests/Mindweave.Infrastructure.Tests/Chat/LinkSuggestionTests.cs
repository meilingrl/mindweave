using Mindweave.Application.Conversations;
using Mindweave.Application.Recommendations;
using Mindweave.Application.Statistics;
using Mindweave.Application.Storage;
using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// AI-suggested links the user confirms (requirements §10.3.4, 2026-11-16) over the real stores and coordinator: what is
/// sent (titles and current excerpts only, archived summaries included, and the pairs to avoid), how the answer is read
/// and every pair checked, how a new request replaces the pending ones, accepting (a confirmed link), ignoring (remembered),
/// 全部忽略, the request's priority, one per workspace, its record and statistics row, and deletions and late results.
/// </summary>
public class LinkSuggestionTests
{
    private const string Steam = "蒸汽机";
    private const string Rails = "铁轨";
    private const string Rocket = "火箭号";
    private const string Gauge = "轨距";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── The prompt and the reading of the answer (pure) ─────────────────────────

    [Fact]
    public void Every_invalid_pair_is_rejected_notes_are_checked_reasons_shortened_and_at_most_five_are_kept()
    {
        var input = Input(6);
        var output = $$"""
            好的，下面是建议：
            ```json
            [
              {"a": "S1", "b": "S2", "note": " “对比” ", "reason": "理由：同一时期的两条路线"},
              {"a": "S2", "b": "S1", "note": "重复", "reason": "反过来也是同一对"},
              {"a": "S3", "b": "s3", "note": "自己", "reason": "同一个总结"},
              {"a": "S1", "b": "S9", "note": "没有", "reason": "S9 不存在"},
              {"a": "S6", "b": "S5", "note": "已关联", "reason": "避开的一对，倒过来"},
              {"a": 3, "b": "[S4]", "note": "这个说明一共有十七个字超过了上限啦", "reason": "{{new string('长', 80)}}"},
              {"a": "S1", "b": "S3"},
              {"a": "S1", "b": "S4", "note": "", "reason": "  "},
              {"a": "S2", "b": "S3", "note": "例子", "reason": "第五对"},
              {"a": "S2", "b": "S4", "note": "第六对", "reason": "超过五对的部分不要"},
            ]
            ```
            """;

        var items = LinkSuggestionPrompt.Parse(output, input, [(Node(5), Node(6))]);

        Assert.Equal(
            [Pair(1, 2), Pair(3, 4), Pair(1, 3), Pair(1, 4), Pair(2, 3)],
            items.Select(i => (i.NodeA, i.NodeB)));
        Assert.All(items, i => Assert.True(string.CompareOrdinal(i.NodeA.ToString(), i.NodeB.ToString()) < 0));
        Assert.Equal(["对比", null, null, null, "例子"], items.Select(i => i.Note));
        Assert.Equal("同一时期的两条路线", items[0].Reason);
        Assert.Equal(new string('长', LinkSuggestionPrompt.MaxReasonLength) + "…", items[1].Reason);
        Assert.Equal([null, null], items.Skip(2).Take(2).Select(i => i.Reason));

        // Exactly 16 characters is a note; 17 is none (the pair stays).
        Assert.Equal(new string('例', 16), LinkSuggestionPrompt.CleanNote(new string('例', 16)));
        Assert.Null(LinkSuggestionPrompt.CleanNote(new string('例', 17)));
    }

    [Fact]
    public void A_summary_is_never_paired_with_one_on_its_own_line_of_descent_but_separate_branches_and_explorations_are()
    {
        // S1 starts exploration E1; S2 branched from it and S3 from S2; S4 is another branch of S1; S5 starts exploration E2
        // (design session-network §8: a branch line already joins a line of descent, so such a link says nothing new).
        var c = Enumerable.Range(1, 5).Select(i => new ConversationId(new Guid(100 + i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]))).ToArray();
        var lineage = new Dictionary<SummaryNodeId, SummaryLineage>
        {
            [Node(1)] = new(c[0], c[0], []),
            [Node(2)] = new(c[1], c[0], [c[0]]),
            [Node(3)] = new(c[2], c[0], [c[0], c[1]]),
            [Node(4)] = new(c[3], c[0], [c[0]]),
            [Node(5)] = new(c[4], c[4], []),
        };
        var input = Input(5) with { Lineage = lineage };
        var output = """
            [{"a":"S1","b":"S2","note":"父子"},{"a":"S3","b":"S1","note":"祖孙"},{"a":"S2","b":"S3","note":"父子"},
             {"a":"S3","b":"S4","note":"堂亲"},{"a":"S1","b":"S5","note":"跨探索"}]
            """;

        var kept = LinkSuggestionPrompt.Parse(output, input, []);

        Assert.Equal([Pair(3, 4), Pair(1, 5)], kept.Select(k => (k.NodeA, k.NodeB)));
        Assert.True(input.OnOneLine(Node(3), Node(1)));
        Assert.False(input.OnOneLine(Node(2), Node(4)));

        // The request says where each summary sits: its exploration, and the nearest listed summary it branched from.
        var sent = LinkSuggestionPrompt.UserText(input);
        var lines = sent.Split('\n');
        Assert.Contains(lines, l => l.StartsWith("[S1]", StringComparison.Ordinal) && l.Contains("E1", StringComparison.Ordinal) && !System.Text.RegularExpressions.Regex.IsMatch(l[4..], @"S\d"));
        Assert.Contains(lines, l => l.StartsWith("[S3]", StringComparison.Ordinal) && l.Contains("E1", StringComparison.Ordinal) && l.Contains("S2", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("[S4]", StringComparison.Ordinal) && l.Contains("S1", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("[S5]", StringComparison.Ordinal) && l.Contains("E2", StringComparison.Ordinal));

        // Without lineage nothing is said about explorations and nothing is dropped for it.
        Assert.DoesNotContain("E1", LinkSuggestionPrompt.UserText(Input(5)), StringComparison.Ordinal);
        Assert.Equal(5, LinkSuggestionPrompt.Parse(output, Input(5), []).Count);
    }

    [Fact]
    public async Task A_pending_suggestion_on_one_line_of_descent_stored_before_the_rule_is_not_offered()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, _) = await h.RootWithAnswerAsync("蒸汽机是怎么工作的？");
        await h.SettleAsync(root);
        h.Client.Knowledge.Replies.Enqueue($"标题：{Steam}\n短名：{Steam}\n\n正文");
        var parent = await h.SummarizeAsync(root);
        var answer = (await h.Store.GetMessagesAsync(root, Ct)).Last(m => m.Role == MessageRole.Assistant);
        var child = (await h.Conversations.CreateBranchAsync(root, BranchFlowTests.Selection(answer, "q"), Ct)).Child;
        await h.Conversations.AskAsync(child, "效率呢？", Ct);
        await h.SettleAsync(child);
        h.Client.Knowledge.Replies.Enqueue("标题：效率\n短名：效率\n\n正文");
        var branch = await h.SummarizeAsync(child);
        var rails = await SummarizedAsync(h, "铁轨为什么是工字形？", Rails, "工字形截面省材料又抗弯。");

        // Stored as a build before the rule could have: a summary with its own branch, and a pair across explorations.
        var (a1, b1) = SummaryLinkPair.Normalize(parent, branch);
        var (a2, b2) = SummaryLinkPair.Normalize(branch, rails);
        await new SqliteLinkSuggestionStore(h.Database).AddBatchAsync(
            h.Workspace, new LinkSuggestionBatch(Guid.CreateVersion7(), DateTimeOffset.UtcNow, [new(a1, b1, null, null), new(a2, b2, null, null)]), Ct);

        var offered = Assert.Single((await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items);
        Assert.True(offered.Touches(rails) && offered.Touches(branch));
    }

    [Fact]
    public void JSON_that_does_not_parse_is_read_by_its_fields_plain_lines_are_read_and_anything_else_is_nothing()
    {
        var input = Input(4);

        // ASCII quotes left unescaped inside the reason: not JSON, read field by field.
        var broken = """
            [{"a": "S1", "b": "S2", "note": "对比", "reason": "都讲"火车"的动力"},
             {"a": "S3", "b": "S4", "note": "原因", "reason": "后者是前者的"前提"
            """;
        var items = LinkSuggestionPrompt.Parse(broken, input, []);
        Assert.Equal([Pair(1, 2), Pair(3, 4)], items.Select(i => (i.NodeA, i.NodeB)));
        Assert.Equal(["对比", "原因"], items.Select(i => i.Note));
        Assert.Equal("都讲\"火车\"的动力", items[0].Reason);

        // One pair per line.
        var lines = """
            以下是建议：
            1. S1 | S3 | 对比 | 一个讲动力一个讲轨道
            - [S2] 与 [S4]：例子
            S4 - S4 | 自己
            """;
        items = LinkSuggestionPrompt.Parse(lines, input, []);
        Assert.Equal([Pair(1, 3), Pair(2, 4)], items.Select(i => (i.NodeA, i.NodeB)));
        Assert.Equal(("对比", "一个讲动力一个讲轨道"), (items[0].Note, items[0].Reason));
        Assert.Equal("例子", items[1].Note);

        Assert.Empty(LinkSuggestionPrompt.Parse("抱歉，我找不到相关的总结。", input, []));
        Assert.Empty(LinkSuggestionPrompt.Parse("[]", input, []));
        Assert.Empty(LinkSuggestionPrompt.Parse("[{\"a\":\"S1\"}]", input, []));
    }

    // ── Requests over the real stores ──────────────────────────────────────────

    [Fact]
    public async Task A_request_sends_only_titles_and_current_excerpts_archived_ones_included_and_the_pairs_to_avoid_and_is_recorded()
    {
        await using var h = await ChatHarness.StartAsync();
        var steam = await SummarizedAsync(h, "蒸汽机是怎么工作的？", Steam, "旧版正文不应发送。");
        var steamConversation = (await h.SummaryService.GetAsync(steam, Ct))!.Node.Conversation;
        await h.AskAsync(steamConversation, "效率呢？");
        await CandidateAsync(h, steamConversation, Steam, "蒸汽推动活塞往复运动，再经曲柄变成旋转。");
        var detail = (await h.SummaryService.GetAsync(steam, Ct))!;
        await h.SummaryService.AdoptAsync(steam, detail.Node.Pending!.Value, Ct);
        await CandidateAsync(h, steamConversation, Steam, "候选正文不应发送。");
        var rails = await SummarizedAsync(h, "铁轨为什么是工字形？", Rails, "工字形截面省材料又抗弯。");
        var rocket = await SummarizedAsync(h, "火箭号跑多快？", Rocket, "雨山试验中最高时速约 47 公里。");
        var gauge = await SummarizedAsync(h, "轨距为什么是 1435？", Gauge, "沿用马车轮距。");
        Assert.True((await h.Conversations.ArchiveAsync((await h.SummaryService.GetAsync(rocket, Ct))!.Node.Conversation, Ct)).Archived);

        // Linked (by the user) and ignored (a link the user deleted): both listed as not to suggest.
        await h.Links.CreateAsync(steam, rails, "对比", Ct);
        var deleted = await h.Links.CreateAsync(rocket, gauge, null, Ct);
        await h.Links.DeleteAsync(deleted.Id, Ct);
        h.Client.PromptTokens = r => FakeChatClient.IsLinkSuggestion(r) ? 640 : null;
        h.Client.CompletionTokens = r => FakeChatClient.IsLinkSuggestion(r) ? 90 : null;
        h.Client.LinkSuggestionAnswer = r => FakeChatClient.PairsByName(r, (Steam, Rocket, "原因", "火箭号用的就是蒸汽机"));

        var outcome = await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct);

        Assert.Equal(new RecommendationOutcome(1), outcome);
        var request = Assert.Single(h.Client.Requests, FakeChatClient.IsLinkSuggestion);
        var sent = string.Join("\n", request.Turns.Select(t => t.Content));
        Assert.Contains("总结（共 4 个，列出 4 个）", sent, StringComparison.Ordinal);
        foreach (var name in new[] { Steam, Rails, Rocket, Gauge })
        {
            // Each summary here starts an exploration of its own, as the summary network places it.
            Assert.Matches($@"\] {name}（探索 E\d）\n", sent);
        }

        Assert.Contains("摘录：蒸汽推动活塞往复运动，再经曲柄变成旋转。", sent, StringComparison.Ordinal);
        Assert.Contains("摘录：雨山试验中最高时速约 47 公里。", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("旧版正文", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("候选正文", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("效率呢", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("答：", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("是怎么工作的", sent, StringComparison.Ordinal);
        Assert.Contains("不要再建议", sent, StringComparison.Ordinal);
        Assert.Equal(
            new HashSet<string> { Avoided(sent, Steam, Rails), Avoided(sent, Rocket, Gauge) },
            sent.Split('\n').Where(l => l.StartsWith("- S", StringComparison.Ordinal)).Select(l => string.Join("|", l[2..].Split(" 与 ").Order(StringComparer.Ordinal))).ToHashSet());
        Assert.Equal(LinkSuggestionPrompt.OutputTokens, request.Model.MaxOutputTokens);

        // Stored pending with its note and reason and both names.
        var item = Assert.Single((await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items);
        Assert.Equal((SummaryLinkPair.Normalize(steam, rocket), "原因", "火箭号用的就是蒸汽机"), ((item.NodeA, item.NodeB), item.Note, item.Reason));
        Assert.Equal(new HashSet<string> { Steam, Rocket }, new HashSet<string> { item.NameA, item.NameB });

        // Recorded for the workspace as its own kind, with its tokens, and counted as 关联建议; no task row.
        var recorded = Assert.Single(await h.Requests.ForWorkspaceAsync(h.Workspace, Ct));
        Assert.Equal((RequestAttribution.SuggestLinks, 640, 90), (recorded.Attribution!.Kind, recorded.PromptTokens, recorded.CompletionTokens));
        Assert.Equal(UsagePurpose.LinkSuggestion, UsageStatisticsService.PurposeOf(RequestAttribution.SuggestLinks));
        Assert.Equal(0, await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM generation_task WHERE kind NOT IN ('answer', 'title', 'summary');"), Ct));
    }

    [Fact]
    public async Task A_new_request_replaces_the_pending_ones_only_once_it_stored_some_and_says_so_quietly_when_none_is_new()
    {
        await using var h = await ChatHarness.StartAsync();
        await FourAsync(h);
        h.Client.LinkSuggestionAnswer = r => FakeChatClient.PairsByName(r, (Steam, Rails, "对比", "一"), (Rocket, Gauge, "例子", "二"));
        Assert.Equal(new RecommendationOutcome(2), await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct));
        var first = (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items;
        Assert.Equal(2, first.Count);

        // A failure, a cut-off answer and an answer with nothing valid leave them, and each says why.
        h.Client.LinkSuggestions.FailNext(GenerationErrorKind.Network);
        Assert.Equal(RecommendationOutcome.Refused(RecommendationProblem.Failed, GenerationErrorKind.Network), await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct));
        Assert.Equal(RecommendationProblem.Failed, (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Outcome!.Problem);
        h.Client.LinkSuggestions.TruncateNext();
        Assert.Equal(RecommendationProblem.Failed, (await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct)).Problem);
        h.Client.LinkSuggestions.Replies.Enqueue("[]");
        Assert.Equal(RecommendationProblem.Empty, (await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct)).Problem);
        h.Client.LinkSuggestionAnswer = r => FakeChatClient.PairsByName(r, (Steam, Steam, "自己", "同一个"), (Steam, "不存在的总结", null, null));
        Assert.Equal(RecommendationProblem.Empty, (await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct)).Problem);
        Assert.Equal(RecommendationProblem.Empty, (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Outcome!.Problem);
        Assert.Equal(first.Select(i => i.Id), (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items.Select(i => i.Id));

        // A batch with a new pair and one still pending: both stored (the pending one takes the new note), the other
        // pending one simply goes, not remembered as ignored.
        h.Client.LinkSuggestionAnswer = r => FakeChatClient.PairsByName(r, (Rails, Rocket, "前提", "新"), (Rails, Steam, "原因", "又一次"));
        Assert.Equal(new RecommendationOutcome(2), await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct));
        var second = await h.LinkSuggestions.GetAsync(h.Workspace, Ct);
        Assert.Null(second.Outcome);
        Assert.Equal(["前提", "原因"], second.Items.Select(i => i.Note));
        Assert.Equal(first[0].Id, second.Items[1].Id);
        Assert.Equal(0, await IgnoredAsync(h));
        Assert.Equal(2, await h.CountAsync("link_suggestion"));
    }

    [Fact]
    public async Task Accepting_makes_a_confirmed_link_with_the_edited_note_and_an_ignored_or_unlinked_pair_is_never_suggested_again()
    {
        await using var h = await ChatHarness.StartAsync();
        var (steam, rails, rocket, gauge) = await FourAsync(h);
        h.Client.LinkSuggestionAnswer = r => FakeChatClient.PairsByName(r, (Steam, Rails, "对比", "一"), (Rocket, Gauge, "例子", "二"), (Steam, Rocket, "原因", "三"));
        await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct);
        var items = (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items;
        var linkChanges = 0;
        h.Links.Changed += (_, _) => Interlocked.Increment(ref linkChanges);

        // A note over 16 characters is refused and nothing changes; the edited note is used.
        Assert.Equal(SummaryLinkRefusal.NoteTooLong, (await Assert.ThrowsAsync<SummaryLinkRefusedException>(() => h.LinkSuggestions.AcceptAsync(items[0], new string('长', 17), Ct))).Reason);
        var link = await h.LinkSuggestions.AcceptAsync(items[0], "  结构对比 ", Ct);
        Assert.Equal((SummaryLinkPair.Normalize(steam, rails), "结构对比", SummaryLinkOrigin.Confirmed), ((link.NodeA, link.NodeB), link.Note, link.Origin));
        Assert.Equal(SummaryLinkOrigin.Confirmed, Assert.Single(await h.Links.ListAsync(h.Workspace, Ct)).Origin);
        Assert.True(Volatile.Read(ref linkChanges) > 0);
        Assert.Equal(2, (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items.Count);
        Assert.Equal(SummaryLinkRefusal.Missing, (await Assert.ThrowsAsync<SummaryLinkRefusedException>(() => h.LinkSuggestions.AcceptAsync(items[0], null, Ct))).Reason);

        // Ignored: kept without its reason, never shown again.
        Assert.True(await h.LinkSuggestions.IgnoreAsync(items[1], Ct));
        Assert.False(await h.LinkSuggestions.IgnoreAsync(items[1], Ct));
        Assert.Equal([items[2].Id], (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items.Select(i => i.Id));
        Assert.Equal(("ignored", (string?)null), await RowAsync(h, rocket, gauge));

        // The confirmed link deleted later: its pair counts as ignored, like any link (8a).
        Assert.True(await h.Links.DeleteAsync(link.Id, Ct));
        Assert.Equal(("ignored", (string?)null), await RowAsync(h, steam, rails));

        // Suggested again, the linked, ignored and unlinked pairs are all dropped: nothing new, nothing replaced.
        h.Client.LinkSuggestionAnswer = r => FakeChatClient.PairsByName(r, (Rails, Steam, "对比", "再来"), (Gauge, Rocket, "例子", "再来"));
        Assert.Equal(RecommendationProblem.Empty, (await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct)).Problem);
        Assert.Equal([items[2].Id], (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items.Select(i => i.Id));
        Assert.Equal(2, h.Client.Requests.Last(FakeChatClient.IsLinkSuggestion).Turns[1].Content.Split('\n').Count(l => l.StartsWith("- S", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Accepting_a_pair_linked_meanwhile_says_so_and_drops_the_suggestion_and_linking_by_hand_takes_it_along()
    {
        await using var h = await ChatHarness.StartAsync();
        var (steam, rails, rocket, _) = await FourAsync(h);
        h.Client.LinkSuggestionAnswer = r => FakeChatClient.PairsByName(r, (Steam, Rails, "对比", "一"), (Steam, Rocket, "原因", "二"));
        await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct);
        var items = (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items;

        // Linked behind its back (another path wrote the link but left the suggestion): accepting says so and drops it.
        var (a, b) = SummaryLinkPair.Normalize(steam, rails);
        await h.Database.WriteAsync(
            (c, t) => c.Execute(t, "INSERT INTO summary_link (id, workspace_id, node_a, node_b, origin, created_at) VALUES ($id, $ws, $a, $b, 'user', 1);",
                ("$id", Guid.NewGuid().ToString("D")), ("$ws", h.Workspace.ToString()), ("$a", a.ToString()), ("$b", b.ToString())),
            Ct);
        Assert.Equal(SummaryLinkRefusal.Duplicate, (await Assert.ThrowsAsync<SummaryLinkRefusedException>(() => h.LinkSuggestions.AcceptAsync(items[0], "对比", Ct))).Reason);
        Assert.Equal([items[1].Id], (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items.Select(i => i.Id));
        Assert.Equal(SummaryLinkOrigin.User, Assert.Single(await h.Links.ListAsync(h.Workspace, Ct)).Origin);

        // Linked by hand through the service: the pending suggestion of that pair goes in the same transaction.
        await h.Links.CreateAsync(rocket, steam, null, Ct);
        Assert.Empty((await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items);
        Assert.Equal(0, await h.CountAsync("link_suggestion"));
    }

    [Fact]
    public async Task Ignore_all_ignores_every_pending_one_and_a_paused_storage_changes_nothing()
    {
        // Recovery never probes during the test: with the harness's 50 ms a loaded machine saw storage recover before the last check.
        await using var h = await ChatHarness.StartAsync(storageRetry: TimeSpan.FromHours(1));
        await FourAsync(h);
        await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct);
        var items = (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items;
        Assert.Equal(2, items.Count);

        h.Health.Report(new StorageWriteException(StorageFailureKind.Full, "disk full", null));
        await Assert.ThrowsAsync<StorageWriteException>(() => h.LinkSuggestions.IgnoreAllAsync(h.Workspace, [.. items.Select(i => i.Id)], Ct));
        await Assert.ThrowsAsync<StorageWriteException>(() => h.LinkSuggestions.AcceptAsync(items[0], null, Ct));
        await Assert.ThrowsAsync<StorageWriteException>(() => h.LinkSuggestions.IgnoreAsync(items[0], Ct));
        Assert.Equal(RecommendationProblem.StoragePaused, (await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct)).Problem);
        Assert.Equal(2, (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items.Count);
    }

    [Fact]
    public async Task Ignore_all_ignores_every_pending_suggestion_at_once()
    {
        await using var h = await ChatHarness.StartAsync();
        await FourAsync(h);
        await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct);
        var asked = (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items.Select(i => i.Id).ToList();

        Assert.Equal(2, await h.LinkSuggestions.IgnoreAllAsync(h.Workspace, asked, Ct));

        Assert.Empty((await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items);
        Assert.Equal(2, await IgnoredAsync(h));
        Assert.Equal(0, await h.LinkSuggestions.IgnoreAllAsync(h.Workspace, asked, Ct));
    }

    [Fact]
    public async Task Ignore_all_ignores_only_the_suggestions_asked_about_not_ones_stored_after_or_of_another_workspace()
    {
        await using var h = await ChatHarness.StartAsync();
        var (steam, rails, rocket, gauge) = await FourAsync(h);
        h.Client.LinkSuggestionAnswer = r => FakeChatClient.PairsByName(r, (Steam, Rails, "对比", "一"));
        await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct);
        var asked = (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items.Select(i => i.Id).ToList();

        // A request ending before the confirmation replaces the asked one with two new ones: none of them is ignored.
        h.Client.LinkSuggestionAnswer = r => FakeChatClient.PairsByName(r, (Rocket, Gauge, "例子", "二"), (Rails, Gauge, "前提", "三"));
        Assert.Equal(new RecommendationOutcome(2), await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct));
        Assert.Equal(0, await h.LinkSuggestions.IgnoreAllAsync(h.Workspace, asked, Ct));
        var pending = (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items;
        Assert.Equal(2, pending.Count);
        Assert.Equal(0, await IgnoredAsync(h));

        // Ids of this workspace named for another one change nothing.
        Assert.Equal(0, await h.LinkSuggestions.IgnoreAllAsync(new Core.Workspaces.WorkspaceId(Guid.NewGuid()), [.. pending.Select(i => i.Id)], Ct));
        Assert.Equal(2, await h.LinkSuggestions.IgnoreAllAsync(h.Workspace, [.. pending.Select(i => i.Id)], Ct));
        Assert.Equal(2, await IgnoredAsync(h));
        Assert.True(pending.All(i => !i.Touches(steam)));
    }

    [Fact]
    public async Task Fewer_than_three_summaries_no_model_or_a_refused_key_send_nothing_or_say_why()
    {
        await using var h = await ChatHarness.StartAsync();
        await SummarizedAsync(h, "蒸汽机是怎么工作的？", Steam, "活塞。");
        await SummarizedAsync(h, "铁轨为什么是工字形？", Rails, "抗弯。");
        Assert.Equal(RecommendationProblem.NoContent, (await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct)).Problem);
        Assert.DoesNotContain(h.Client.Requests, FakeChatClient.IsLinkSuggestion);

        await SummarizedAsync(h, "火箭号跑多快？", Rocket, "很快。");
        h.Client.LinkSuggestions.FailNext(GenerationErrorKind.Unauthorized);
        Assert.Equal(RecommendationOutcome.Refused(RecommendationProblem.Credentials, GenerationErrorKind.Unauthorized), await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct));
        Assert.Equal(RecommendationProblem.Credentials, (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Outcome!.Problem);

        await h.Database.WriteAsync((c, t) => c.Execute(t, "DELETE FROM app_state WHERE key = 'active_model_config_id';"), Ct);
        Assert.Equal(RecommendationProblem.NotConfigured, (await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct)).Problem);
        Assert.Single(h.Client.Requests, FakeChatClient.IsLinkSuggestion);
        Assert.Empty((await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items);
    }

    // ── Priority, one at a time, deletions and late results ─────────────────────

    [Fact]
    public async Task A_suggestion_never_starts_while_the_users_work_waits_and_is_not_interrupted_once_it_runs()
    {
        await using var h = await ChatHarness.StartAsync(slots: 1);
        await FourAsync(h);
        var listed = await h.Conversations.ListAsync(h.Workspace, Ct);
        var (steam, rails) = (listed[0].Conversation.Id, listed[1].Conversation.Id);

        // The only slot answers: the suggestion waits.
        var held = h.Client.Hold("一");
        await h.Conversations.AskAsync(steam, "再问一", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var suggest = h.LinkSuggestions.SuggestAsync(h.Workspace, Ct);
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 2);
        Assert.True((await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).IsRunning);
        Assert.Equal(0, Requests(h));

        // A question asked after it, while it waits, still goes first: the user's work comes before it.
        var later = h.Client.Hold("后");
        await h.Conversations.AskAsync(rails, "后来的问题", Ct);
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 3);
        held.Finish("完");
        await later.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(0, Requests(h));
        var gate = h.Client.LinkSuggestions.Hold();
        later.Finish("完");

        // Once it runs, a new question waits for it instead of taking its slot.
        await ChatHarness.UntilAsync(() => Requests(h) == 1);
        var waiting = h.Client.Hold("二");
        await h.Conversations.AskAsync(steam, "再问二", Ct);
        await ChatHarness.StaysAsync(() => !waiting.Started.Task.IsCompleted, 200);
        h.Client.LinkSuggestions.Gate = null;
        gate.TrySetResult();
        Assert.Equal(new RecommendationOutcome(2), await suggest);
        await waiting.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        waiting.Finish("完");
    }

    [Fact]
    public async Task One_request_per_workspace_a_second_click_joins_the_running_one()
    {
        await using var h = await ChatHarness.StartAsync();
        await FourAsync(h);
        var gate = h.Client.LinkSuggestions.Hold();
        var first = h.LinkSuggestions.SuggestAsync(h.Workspace, Ct);
        await ChatHarness.UntilAsync(() => Requests(h) == 1);
        var second = h.LinkSuggestions.SuggestAsync(h.Workspace, Ct);
        Assert.True((await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).IsRunning);
        await ChatHarness.StaysAsync(() => Requests(h) == 1, 200);
        h.Client.LinkSuggestions.Gate = null;
        gate.TrySetResult();
        Assert.Equal(await first, await second);
        await h.LinkSuggestions.SettledAsync();
        Assert.Equal(1, Requests(h));
        Assert.False((await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).IsRunning);
    }

    [Fact]
    public async Task Deleting_the_workspace_stops_its_request_and_removes_every_suggestion_ignored_ones_included()
    {
        await using var h = await ChatHarness.StartAsync();
        await FourAsync(h);
        await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct);
        await h.LinkSuggestions.IgnoreAsync((await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items[0], Ct);

        var gate = h.Client.LinkSuggestions.Hold();
        var running = h.LinkSuggestions.SuggestAsync(h.Workspace, Ct);
        await ChatHarness.UntilAsync(() => Requests(h) == 2);
        Assert.Equal(Application.Workspaces.WorkspaceDeleteStatus.Deleted, (await h.WorkspaceDeletion.DeleteAsync(h.Workspace, Ct)).Status);
        Assert.Equal(RecommendationProblem.Cancelled, (await running.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Problem);
        gate.TrySetResult();
        await h.LinkSuggestions.SettledAsync();
        Assert.Equal(0, await h.CountAsync("link_suggestion"));
        Assert.Null((await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Outcome);
    }

    [Fact]
    public async Task A_late_result_leaves_out_pairs_whose_summaries_went_and_keeps_the_old_ones_when_nothing_is_left()
    {
        await using var h = await ChatHarness.StartAsync();
        var (steam, rails, rocket, _) = await FourAsync(h);
        h.Client.LinkSuggestionAnswer = r => FakeChatClient.PairsByName(r, (Rails, Gauge, "例子", "旧"));
        await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct);
        Assert.Single((await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items);

        // The answer names pairs with the steam summary and one without; the steam summary is deleted while it is out.
        h.Client.LinkSuggestionAnswer = r => FakeChatClient.PairsByName(r, (Steam, Rails, "对比", "一"), (Rocket, Gauge, "原因", "二"));
        var gate = h.Client.LinkSuggestions.Hold();
        var running = h.LinkSuggestions.SuggestAsync(h.Workspace, Ct);
        await ChatHarness.UntilAsync(() => Requests(h) == 2);
        await DeleteSummaryAsync(h, steam);
        h.Client.LinkSuggestions.Gate = null;
        gate.TrySetResult();
        Assert.Equal(new RecommendationOutcome(1), await running);
        var item = Assert.Single((await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items);
        Assert.True(item.Touches(rocket));

        // Every pair of a late answer gone: nothing stored, the pending one not replaced.
        h.Client.LinkSuggestionAnswer = r => FakeChatClient.PairsByName(r, (Rails, Gauge, "对比", "一"));
        gate = h.Client.LinkSuggestions.Hold();
        running = h.LinkSuggestions.SuggestAsync(h.Workspace, Ct);
        await ChatHarness.UntilAsync(() => Requests(h) == 3);
        await DeleteSummaryAsync(h, rails);
        h.Client.LinkSuggestions.Gate = null;
        gate.TrySetResult();
        Assert.Equal(RecommendationProblem.Empty, (await running).Problem);
        Assert.Equal([item.Id], (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Suggestions_go_with_their_summary_on_every_deletion_path_stay_through_archive_and_restore_and_are_in_the_backup()
    {
        await using var h = await ChatHarness.StartAsync();
        var (steam, rails, rocket, gauge) = await FourAsync(h);
        h.Client.LinkSuggestionAnswer = r => FakeChatClient.PairsByName(r, (Steam, Rails, "对比", "一"), (Rocket, Gauge, "例子", "二"), (Rails, Gauge, "前提", "三"));
        await h.LinkSuggestions.SuggestAsync(h.Workspace, Ct);
        var pending = (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items;
        await h.LinkSuggestions.IgnoreAsync(pending.Single(i => i.Touches(rocket)), Ct);
        Assert.Equal(3, await h.CountAsync("link_suggestion"));

        // Archived and restored: nothing changes (the summary stays in the network).
        var railsConversation = (await h.SummaryService.GetAsync(rails, Ct))!.Node.Conversation;
        Assert.True((await h.Conversations.ArchiveAsync(railsConversation, Ct)).Archived);
        Assert.Equal(2, (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items.Count);
        await h.Conversations.RestoreAsync(railsConversation, Ct);
        Assert.Equal(2, (await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items.Count);

        // A summary's last version deleted: its pending suggestion goes, the others stay.
        await DeleteSummaryAsync(h, steam);
        Assert.Equal(2, await h.CountAsync("link_suggestion"));

        // Its conversation deleted: the ignored row of the rocket summary goes with it; the backup made first still has it.
        var outcome = await h.DeleteAsync((await h.SummaryService.GetAsync(rocket, Ct))!.Node.Conversation);
        Assert.True(outcome.IsDeleted);
        Assert.Equal(1, await h.CountAsync("link_suggestion"));
        using (var copy = SqliteConnections.OpenReadOnly(outcome.BackupPath!))
        {
            Assert.Equal(2, copy.Scalar<long>(null, "SELECT count(*) FROM link_suggestion;"));
        }

        Assert.Null(await h.Database.ReadAsync(c => SqliteConnections.ForeignKeyViolation(c), Ct));
        Assert.True((await h.LinkSuggestions.GetAsync(h.Workspace, Ct)).Items.Single().Touches(gauge));
    }

    private static SummaryNodeId Node(int n) => new(new Guid(n, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]));

    private static (SummaryNodeId, SummaryNodeId) Pair(int first, int second) => SummaryLinkPair.Normalize(Node(first), Node(second));

    private static LinkSuggestionInput Input(int summaries) =>
        new([.. Enumerable.Range(1, summaries).Select(i => new RecommendationSource($"S{i}", Node(i), $"总结{i}", $"总结{i}", "摘录"))], summaries, []);

    private static int Requests(ChatHarness h) => h.Client.Requests.Count(FakeChatClient.IsLinkSuggestion);

    /// <summary>The avoided pair of two summaries as the request lists it, by their labels in order ("S1|S3").</summary>
    private static string Avoided(string sent, string first, string second) =>
        string.Join("|", new[] { first, second }.Select(n => System.Text.RegularExpressions.Regex.Match(sent, $@"\[(S\d+)\] {n}(?:（探索 E\d+(?:，分支自 S\d+)?）)?\n").Groups[1].Value).Order(StringComparer.Ordinal));

    private static Task<long> IgnoredAsync(ChatHarness h) =>
        h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM link_suggestion WHERE state = 'ignored';"), Ct);

    private static Task<(string?, string?)> RowAsync(ChatHarness h, SummaryNodeId first, SummaryNodeId second)
    {
        var (a, b) = SummaryLinkPair.Normalize(first, second);
        return h.Database.ReadAsync(
            c =>
            {
                using var command = c.Command(null, "SELECT state, reason FROM link_suggestion WHERE node_a = $a AND node_b = $b;", ("$a", a.ToString()), ("$b", b.ToString()));
                using var reader = command.ExecuteReader();
                return reader.Read() ? (reader.GetString(0), reader.NullableString(1)) : ((string?)null, (string?)null);
            },
            Ct);
    }

    /// <summary>Four summaries, oldest first: steam, rails, rocket, gauge.</summary>
    private static async Task<(SummaryNodeId Steam, SummaryNodeId Rails, SummaryNodeId Rocket, SummaryNodeId Gauge)> FourAsync(ChatHarness h) => (
        await SummarizedAsync(h, "蒸汽机是怎么工作的？", Steam, "蒸汽推动活塞往复运动。"),
        await SummarizedAsync(h, "铁轨为什么是工字形？", Rails, "工字形截面省材料又抗弯。"),
        await SummarizedAsync(h, "火箭号跑多快？", Rocket, "最高时速约 47 公里。"),
        await SummarizedAsync(h, "轨距为什么是 1435？", Gauge, "沿用马车轮距。"));

    /// <summary>A root conversation with one answer, its title settled, summarized with <paramref name="name"/> as its title and shown name.</summary>
    private static async Task<SummaryNodeId> SummarizedAsync(ChatHarness h, string question, string name, string body)
    {
        var (root, _) = await h.RootWithAnswerAsync(question);
        await h.SettleAsync(root);
        h.Client.Knowledge.Replies.Enqueue($"标题：{name}\n短名：{name}\n\n{body}");
        return await h.SummarizeAsync(root);
    }

    /// <summary>Summarizes the conversation again: a candidate for its summary.</summary>
    private static async Task CandidateAsync(ChatHarness h, ConversationId conversation, string name, string body)
    {
        h.Client.Knowledge.Replies.Enqueue($"标题：{name}\n短名：{name}\n\n{body}");
        await h.SummarizeAsync(conversation);
        Assert.NotNull((await h.SummaryService.ForConversationAsync(conversation, Ct))!.Node.Pending);
    }

    private static async Task DeleteSummaryAsync(ChatHarness h, SummaryNodeId node)
    {
        var detail = (await h.SummaryService.GetAsync(node, Ct))!;
        Assert.Null(await h.SummaryService.DeleteVersionsAsync(node, detail.Versions.Select(v => v.Id).ToHashSet(), Ct));
    }
}
