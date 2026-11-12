using Mindweave.Application.Conversations;
using Mindweave.Application.Recommendations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Recommended questions (requirements §10.3 共同规则 and §10.3.1, 2026-11-12) over the real stores and coordinator: what
/// is sent (titles and short excerpts of current versions only), how the answer is read, what is kept and avoided, when
/// the background prepares them (on a standing clock), at which priority, one request per workspace, and how deletions
/// and late results are handled.
/// </summary>
public class RecommendationTests
{
    private static readonly TimeSpan Delay = new RecommendationOptions().Delay;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── The prompt and the reading of the answer (pure) ─────────────────────────

    [Fact]
    public void A_JSON_answer_is_read_even_in_a_fence_after_a_lead_in_and_repeats_and_bad_items_are_dropped()
    {
        var input = Input(("S1", "蒸汽机"), ("S2", "铁轨"));
        var output = """
            好的，以下是推荐：
            ```json
            [
              {"question": "“蒸汽机为什么被淘汰？”", "reason": "理由：顺着往后看", "basis": "S1"},
              {"question": "火车是怎么发明的", "reason": "重复", "basis": ""},
              {"question": "问", "reason": "太短", "basis": "S2"},
              {"question": "铁轨为什么会热胀冷缩？", "reason": "这一句理由写得实在太长太长太长太长太长太长太长太长太长太长太长太长太长太长太长太长太长了吧", "basis": "S9"},
              {"question": "蒸汽机 为什么被淘汰", "reason": "与第一条重复", "basis": "S1"},
              {"question": "高铁为什么快？", "basis": "s2"},
              {"question": "第五个问题会被截掉吗？", "basis": "S1"},
            ]
            ```
            """;
        var avoid = new HashSet<string> { RecommendationText.Normalize("火车是怎么发明的？") };

        var items = RecommendationPrompt.Parse(output, input, new RecommendationAsk(RecommendationKind.Question, 3), avoid);

        Assert.Equal(["蒸汽机为什么被淘汰？", "铁轨为什么会热胀冷缩？", "高铁为什么快？"], items.Select(i => i.Question));
        Assert.Equal(["顺着往后看", null, null], items.Select(i => i.Reason));
        Assert.Equal([Node(1), null, Node(2)], items.Select(i => i.Basis));
    }

    [Fact]
    public void A_JSON_answer_with_unescaped_quotes_is_read_by_its_fields_and_never_as_lines_of_JSON()
    {
        // Review 2026-11-13: ASCII quotes inside a Chinese question make the array invalid JSON. Read line by line, its
        // field lines (`"question": "…",`) were short enough to pass as questions, quotes and key included.
        var input = Input(("S1", "蒸汽机"), ("S2", "铁轨"));
        var output = """
            [
              {
                "question": "为什么说"安全阀"救了蒸汽机车？",
                "reason": "从原理延伸",
                "basis": "S1"
              },
              {
                "question": "铁轨的"轨距"是怎么定下来的？",
                "reason": "相邻方向",
                "basis": "S2"
              }
            ]
            """;

        var items = RecommendationPrompt.Parse(output, input, new RecommendationAsk(RecommendationKind.Question, 3), new HashSet<string>());

        Assert.Equal(["为什么说\"安全阀\"救了蒸汽机车？", "铁轨的\"轨距\"是怎么定下来的？"], items.Select(i => i.Question));
        Assert.Equal(["从原理延伸", "相邻方向"], items.Select(i => i.Reason));
        Assert.Equal([Node(1), Node(2)], items.Select(i => i.Basis));

        // Unreadable JSON is never taken apart into lines: no item carries a key, a quote or a brace.
        var broken = "[\n  {\n    \"question\": \"蒸汽机效率多高？\"\n    \"reason\": \"缺了逗号\"\n  }\n";
        Assert.All(
            RecommendationPrompt.Parse(broken, input, new RecommendationAsk(RecommendationKind.Question, 3), new HashSet<string>()),
            i => Assert.DoesNotContain("question", i.Question, StringComparison.Ordinal));
    }

    [Fact]
    public void Numbered_lines_are_read_when_there_is_no_JSON_and_a_node_needs_its_basis()
    {
        var input = Input(("S1", "蒸汽机"), ("S2", "铁轨"));
        var output = "以下是三个问题：\n1. 蒸汽机的效率有多高？ | 理由：从原理延伸 | S1\n2）铁轨怎么铺设？｜延伸自：S2\n- 刹车是怎么工作的？\n";

        var questions = RecommendationPrompt.Parse(output, input, new RecommendationAsk(RecommendationKind.Question, 3), new HashSet<string>());
        Assert.Equal(["蒸汽机的效率有多高？", "铁轨怎么铺设？", "刹车是怎么工作的？"], questions.Select(i => i.Question));
        Assert.Equal(["从原理延伸", null, null], questions.Select(i => i.Reason));
        Assert.Equal([Node(1), Node(2), null], questions.Select(i => i.Basis));

        // A recommended node must extend a summary listed (§10.3.2): the third is dropped.
        var nodes = RecommendationPrompt.Parse(output, input, new RecommendationAsk(RecommendationKind.Node, 3), new HashSet<string>());
        Assert.Equal(["蒸汽机的效率有多高？", "铁轨怎么铺设？"], nodes.Select(i => i.Question));
        Assert.Contains("不能留空", RecommendationPrompt.SystemPrompt(new RecommendationAsk(RecommendationKind.Node, 2)), StringComparison.Ordinal);
        Assert.Empty(RecommendationPrompt.Parse("抱歉，我无法回答。", input, new RecommendationAsk(RecommendationKind.Node, 1), new HashSet<string>()));
    }

    [Fact]
    public void An_excerpt_is_the_start_of_the_body_as_plain_text_without_the_title_line()
    {
        var body = "# 蒸汽机的原理\n\n**蒸汽**推动[活塞](https://example.org)往复运动。\n- 曲柄把往复变成旋转\n> 瓦特改进了冷凝器\n" + new string('长', 200);
        var excerpt = RecommendationPrompt.Excerpt(body, "蒸汽机的原理");
        Assert.StartsWith("蒸汽推动活塞往复运动。 曲柄把往复变成旋转 瓦特改进了冷凝器 长", excerpt, StringComparison.Ordinal);
        Assert.Equal(RecommendationPrompt.ExcerptLength + 1, new System.Globalization.StringInfo(excerpt).LengthInTextElements);
        Assert.EndsWith("…", excerpt, StringComparison.Ordinal);
        Assert.Equal("短", RecommendationPrompt.Excerpt("短", "标题"));
        Assert.Equal(RecommendationText.Normalize("火车 是怎么发明的？"), RecommendationText.Normalize("火车是怎么发明的"));
    }

    [Fact]
    public void Only_titles_are_sent_archived_ones_included_and_the_lists_are_bounded()
    {
        static ConversationSummary Conversation(string? title, string question, bool hidden = false) =>
            new(new Conversation(ConversationId.New(), WorkspaceId.New(), title, title is null ? TitleSource.None : TitleSource.Auto, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), question, MessageState.Complete, IsHidden: hidden);

        var conversations = new List<ConversationSummary>
        {
            Conversation("归档的标题", "q0", hidden: true),
            Conversation(null, "只有问题的会话"),
            Conversation("蒸汽机原理", "q1"),
        };
        conversations.AddRange(Enumerable.Range(0, 40).Select(i => Conversation($"标题{i}", "q")));
        var bases = Enumerable.Range(0, 20).Select(i => new RecommendationBasis(Node(i + 1), $"总结{i}", $"总结{i}", "正文", DateTimeOffset.UnixEpoch)).ToList();

        var input = RecommendationPrompt.SelectInput(conversations, bases, 20, ["用过的问题", "用过的问题"]);
        Assert.Equal(RecommendationPrompt.MaxConversations, input.Conversations.Count);
        Assert.Equal("蒸汽机原理", input.Conversations[0]);
        Assert.DoesNotContain("只有问题的会话", input.Conversations);
        Assert.Equal(42, input.ConversationCount);
        Assert.Equal(RecommendationPrompt.MaxSummaries, input.Summaries.Count);
        Assert.Equal(["S1", "S12"], new[] { input.Summaries[0].Label, input.Summaries[^1].Label });
        Assert.Equal(["用过的问题"], input.Avoid);

        // Few titles: the archived one is sent too, after the visible ones.
        var small = RecommendationPrompt.SelectInput(conversations.Take(3).ToList(), [], 0, []);
        Assert.Equal(["蒸汽机原理", "归档的标题"], small.Conversations);
        Assert.True(RecommendationPrompt.SelectInput([Conversation(null, "只有问题")], [], 0, []).IsEmpty);
    }

    // ── Requests over the real stores ──────────────────────────────────────────

    [Fact]
    public async Task Changing_the_batch_sends_titles_and_current_excerpts_only_and_keeps_three_questions_with_their_basis()
    {
        await using var h = await ChatHarness.StartAsync();
        var steam = await TitledAsync(h, "蒸汽机是怎么工作的？", "蒸汽机原理");
        await SummaryAsync(h, steam, "蒸汽机", "# 蒸汽机的说明\n蒸汽推动活塞往复运动，再经曲柄变成旋转。");
        await h.AskAsync(steam, "效率呢？");
        await SummaryAsync(h, steam, "蒸汽机", "候选正文不应发送", candidate: true);
        var rails = await TitledAsync(h, "铁轨为什么是工字形？", "铁轨形状");
        await SummaryAsync(h, rails, "铁轨", "工字形截面省材料又抗弯。");
        var first = await TitledAsync(h, "第一条铁路在哪里？", "最早的铁路");
        Assert.True((await h.Conversations.ArchiveAsync(first, Ct)).Archived);
        h.Client.Titles.FailNext(GenerationErrorKind.ServerError);
        var untitled = await h.RootWithAnswerAsync("这一句只是问题不是标题");
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0);
        Assert.Null((await h.Store.GetAsync(untitled.Root, Ct))!.Title);
        h.Client.PromptTokens = r => FakeChatClient.IsRecommendation(r) ? 520 : null;
        h.Client.CompletionTokens = r => FakeChatClient.IsRecommendation(r) ? 80 : null;

        var outcome = await h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct);

        Assert.Equal(new RecommendationOutcome(3), outcome);
        var request = Assert.Single(h.Client.Requests, FakeChatClient.IsRecommendation);
        var sent = request.Turns[1].Content;
        Assert.Contains("蒸汽机原理", sent, StringComparison.Ordinal);
        Assert.Contains("铁轨形状", sent, StringComparison.Ordinal);
        Assert.Contains("最早的铁路", sent, StringComparison.Ordinal);
        Assert.Contains("摘录：蒸汽推动活塞往复运动，再经曲柄变成旋转。", sent, StringComparison.Ordinal);
        Assert.Contains("摘录：工字形截面省材料又抗弯。", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("候选正文", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("这一句只是问题", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("答：", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("效率呢", sent, StringComparison.Ordinal);
        Assert.Equal(RecommendationPrompt.OutputTokens, request.Model.MaxOutputTokens);

        // Kept in order, each with the summary it extends by its shown name; recorded for the workspace as 推荐.
        var offer = await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct);
        Assert.True(offer is { Enabled: true, HasContent: true, IsPreparing: false, Failure: null });
        Assert.Equal(["蒸汽机车为什么被电力机车取代？", "高铁的轨道和普通铁轨有什么不同？", "火车的刹车是怎样工作的？"], offer.Items.Select(i => i.Question));
        Assert.Equal(["铁轨的说明", "蒸汽机的说明", null], offer.Items.Select(i => i.BasisName));
        var recorded = Assert.Single(await h.Requests.ForWorkspaceAsync(h.Workspace, Ct));
        Assert.Equal((RequestAttribution.RecommendQuestions, 520, 80), (recorded.Attribution!.Kind, recorded.PromptTokens, recorded.CompletionTokens));
        Assert.Equal(0, await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM generation_task WHERE kind NOT IN ('answer', 'title', 'summary');"), Ct));
    }

    [Fact]
    public async Task Nothing_is_sent_without_anything_to_draw_on_without_a_model_or_while_off()
    {
        await using var h = await ChatHarness.StartAsync();
        Assert.Equal(RecommendationProblem.NoContent, (await h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct)).Problem);
        Assert.False((await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct)).HasContent);

        await TitledAsync(h, "蒸汽机是怎么工作的？", "蒸汽机原理");
        Assert.True((await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct)).HasContent);
        await h.RecommendationSettings.SetQuestionsAsync(false, Ct);
        Assert.Equal(RecommendationProblem.Disabled, (await h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct)).Problem);
        Assert.False((await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct)).Enabled);
        await h.RecommendationSettings.SetQuestionsAsync(true, Ct);

        await h.Database.WriteAsync((c, t) => c.Execute(t, "DELETE FROM app_state WHERE key = 'active_model_config_id';"), Ct);
        Assert.Equal(RecommendationProblem.NotConfigured, (await h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct)).Problem);
        Assert.DoesNotContain(h.Client.Requests, FakeChatClient.IsRecommendation);
        Assert.Empty(await h.Requests.ForWorkspaceAsync(h.Workspace, Ct));
    }

    [Fact]
    public async Task Without_a_model_stored_recommendations_are_not_shown_and_the_background_never_seems_to_prepare()
    {
        // Review 2026-11-13, §10.3 共同规则 / §11 "关闭推荐或未配置模型：不显示推荐、不报错、不发请求": stored ones were still
        // listed with the model removed, and each look briefly reported a request running (a flashing "正在准备…").
        await using var h = await ChatHarness.StartAsync();
        var steam = await TitledAsync(h, "蒸汽机是怎么工作的？", "蒸汽机原理");
        await SummaryAsync(h, steam, "蒸汽机", "蒸汽推动活塞往复运动。");
        Assert.Equal(3, (await h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct)).Added);
        Assert.Equal(1, (await h.Recommendations.RefreshNodesAsync(h.Workspace, Ct)).Added);

        await h.Database.WriteAsync((c, t) => c.Execute(t, "DELETE FROM app_state WHERE key = 'active_model_config_id';"), Ct);
        var questions = await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct);
        Assert.True(questions is { Enabled: true, HasContent: true, Failure: null });
        Assert.Empty(questions.Items);
        var nodes = await h.Recommendations.GetNodesAsync(h.Workspace, Ct);
        Assert.True(nodes is { Enabled: true, SummaryCount: 1, Failure: null });
        Assert.Empty(nodes.Items);

        // All used up: a look would prepare, but there is no model, so it never even starts a request.
        foreach (var item in (await h.RecommendationStore.ListAsync(h.Workspace, RecommendationKind.Question, Ct)).Where(r => r.State == RecommendationState.Pending))
        {
            await h.Recommendations.IgnoreAsync(item, Ct);
        }

        var changes = 0;
        h.Recommendations.Changed += (_, _) => Interlocked.Increment(ref changes);
        h.Recommendations.NewExplorationOpened(h.Workspace);
        await h.RecommendationStore.RemovePendingAsync(h.Workspace, RecommendationKind.Node, [.. nodes.Items.Select(i => i.Id)], Ct);
        await h.Database.WriteAsync((c, t) => c.Execute(t, "DELETE FROM app_state WHERE key LIKE '%recommend.nodes.plan:%';"), Ct);
        h.Recommendations.NetworkOpened(h.Workspace);
        await AdvanceAsync(h, Delay);
        Assert.Equal(0, Volatile.Read(ref changes));
        Assert.Equal(2, Requests(h));

        // With a model again a look prepares (nothing was sent, so nothing was remembered as tried).
        await h.Models.SaveAsync(new Application.Models.ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test"), Ct);
        h.Client.Recommendations.Replies.Enqueue("[\"活塞为什么要往复运动？\",\"曲柄怎样把往复变成旋转？\",\"蒸汽机的效率有多高？\"]");
        h.Recommendations.NewExplorationOpened(h.Workspace);
        await AdvanceAsync(h, Delay);
        Assert.Equal(3, Requests(h));
        Assert.Equal(3, (await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct)).Items.Count);
    }

    [Fact]
    public async Task After_a_refused_key_stored_recommendations_are_not_shown_until_the_model_is_saved_again()
    {
        // Review 2026-11-13, §10.3 共同规则: "未配置模型或凭据失效时，推荐区域不报错、不显示推荐". A refused key left the
        // stored questions and nodes on show.
        await using var h = await ChatHarness.StartAsync();
        var steam = await TitledAsync(h, "蒸汽机是怎么工作的？", "蒸汽机原理");
        await SummaryAsync(h, steam, "蒸汽机", "蒸汽推动活塞往复运动。");
        Assert.Equal(3, (await h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct)).Added);
        Assert.Equal(1, (await h.Recommendations.RefreshNodesAsync(h.Workspace, Ct)).Added);

        h.Client.Recommendations.FailNext(GenerationErrorKind.Unauthorized);
        Assert.Equal(RecommendationProblem.Credentials, (await h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct)).Problem);
        var questions = await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct);
        Assert.True(questions is { Enabled: true, HasContent: true, Failure: null });
        Assert.Empty(questions.Items);
        Assert.Empty((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);
        Assert.Equal(3, await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM recommendation WHERE kind = 'question' AND state = 'pending';"), Ct));

        // A new key saved: the stored ones are shown again, and the pages are told.
        var changed = 0;
        h.Recommendations.Changed += (_, w) => { if (w == h.Workspace) { Interlocked.Increment(ref changed); } };
        await h.Models.SaveAsync(new Application.Models.ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-new"), Ct);
        Assert.True(Volatile.Read(ref changed) > 0);
        Assert.Equal(3, (await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct)).Items.Count);
        Assert.Single((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);

        // Refused again, then a request that gets through: shown again too.
        h.Client.NodeRecommendations.FailNext(GenerationErrorKind.Unauthorized);
        Assert.Equal(RecommendationProblem.Credentials, (await h.Recommendations.RefreshNodesAsync(h.Workspace, Ct)).Problem);
        Assert.Empty((await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct)).Items);
        h.Client.Recommendations.Replies.Enqueue("[\"活塞为什么要往复运动？\",\"曲柄怎样把往复变成旋转？\",\"蒸汽机的效率有多高？\"]");
        Assert.Equal(3, (await h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct)).Added);
        Assert.Equal(3, (await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct)).Items.Count);
        Assert.Single((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);
    }

    [Fact]
    public async Task A_failure_keeps_the_shown_questions_and_says_why_and_a_refused_key_is_told_apart()
    {
        await using var h = await ChatHarness.StartAsync();
        await TitledAsync(h, "蒸汽机是怎么工作的？", "蒸汽机原理");
        await h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct);
        var before = (await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct)).Items.Select(i => i.Id).ToList();

        h.Client.Recommendations.FailNext(GenerationErrorKind.Network);
        Assert.Equal(RecommendationOutcome.Refused(RecommendationProblem.Failed, GenerationErrorKind.Network), await h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct));
        h.Client.Recommendations.FailNext(GenerationErrorKind.Unauthorized);
        Assert.Equal(RecommendationProblem.Credentials, (await h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct)).Problem);
        h.Client.Recommendations.TruncateNext();
        Assert.Equal(RecommendationProblem.Failed, (await h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct)).Problem);

        // Every question a repeat of one shown, used or ignored, or of a conversation: nothing usable, nothing replaced.
        h.Client.Recommendations.Replies.Enqueue("[\"蒸汽机车为什么被电力机车取代\", \"蒸汽机原理\", \"蒸汽机是怎么工作的\"]");
        Assert.Equal(RecommendationProblem.Empty, (await h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct)).Problem);

        var offer = await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct);
        Assert.Equal(before, offer.Items.Select(i => i.Id));

        // A request the user made explains itself: nothing is kept to show quietly.
        Assert.Null(offer.Failure);
    }

    [Fact]
    public async Task Ignored_and_used_questions_are_not_offered_again_and_the_next_batch_is_told_to_avoid_them()
    {
        await using var h = await ChatHarness.StartAsync();
        await TitledAsync(h, "蒸汽机是怎么工作的？", "蒸汽机原理");
        await h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct);
        var items = (await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct)).Items;
        await h.Recommendations.IgnoreAsync(items[0], Ct);
        await h.Recommendations.MarkUsedAsync(items[1], Ct);
        Assert.Equal([items[2].Id], (await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct)).Items.Select(i => i.Id));

        // The model offers the ignored and the used one again, one more time with other spacing and marks: only new ones stay.
        h.Client.Recommendations.Replies.Enqueue("[\"蒸汽机车为什么 被电力机车取代？\", \"《高铁的轨道和普通铁轨有什么不同》\", \"铁路信号灯怎么工作？\"]");
        Assert.Equal(new RecommendationOutcome(1), await h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct));
        Assert.Equal(["铁路信号灯怎么工作？"], (await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct)).Items.Select(i => i.Question));
        var sent = h.Client.Requests.Where(FakeChatClient.IsRecommendation).Last().Turns[1].Content;
        Assert.Contains("不再推荐：", sent, StringComparison.Ordinal);
        Assert.Contains("蒸汽机车为什么被电力机车取代", sent, StringComparison.Ordinal);
        Assert.Contains("高铁的轨道和普通铁轨有什么不同？", sent, StringComparison.Ordinal);
        Assert.Contains("火车的刹车是怎样工作的？", sent, StringComparison.Ordinal);
    }

    // ── Prepared in the background (§10.3.1) ──────────────────────────────────

    [Fact]
    public async Task Opening_new_exploration_prepares_questions_once_and_again_only_when_the_rules_say_so()
    {
        await using var h = await ChatHarness.StartAsync();
        var steam = await TitledAsync(h, "蒸汽机是怎么工作的？", "蒸汽机原理");
        var service = h.Recommendations;

        // None stored: prepared shortly after the page opens.
        service.NewExplorationOpened(h.Workspace);
        Assert.True(service.IsWaiting(h.Workspace));
        Assert.DoesNotContain(h.Client.Requests, FakeChatClient.IsRecommendation);
        await AdvanceAsync(h, Delay);
        Assert.Equal(1, Requests(h));
        Assert.Equal(3, (await service.GetQuestionsAsync(h.Workspace, Ct)).Items.Count);

        // Opened again with nothing new: nothing is asked.
        service.NewExplorationOpened(h.Workspace);
        await AdvanceAsync(h, Delay);
        Assert.Equal(1, Requests(h));

        // Some used, some ignored, one left: still nothing; the last one gone: prepared again.
        var items = (await service.GetQuestionsAsync(h.Workspace, Ct)).Items;
        await service.MarkUsedAsync(items[0], Ct);
        await service.IgnoreAsync(items[1], Ct);
        await AdvanceAsync(h, Delay);
        Assert.Equal(1, Requests(h));
        h.Client.Recommendations.Replies.Enqueue("[\"铁路信号灯怎么工作？\", \"火车为什么要编组？\"]");
        await service.IgnoreAsync(items[2], Ct);
        await AdvanceAsync(h, Delay);
        Assert.Equal(2, Requests(h));
        Assert.Equal(2, (await service.GetQuestionsAsync(h.Workspace, Ct)).Items.Count);

        // A summary made since: prepared when the page is opened again, not before; the old ones stay meanwhile.
        h.PreparationTime.Advance(TimeSpan.FromMinutes(1));
        await SummaryAsync(h, steam, "蒸汽机", "蒸汽推动活塞。");
        await AdvanceAsync(h, Delay);
        Assert.Equal(2, Requests(h));
        var gate = h.Client.Recommendations.Hold();
        h.Client.Recommendations.Replies.Enqueue("[\"蒸汽机的效率有多高？\", \"冷凝器有什么用？\", \"瓦特改进了什么？\"]");
        service.NewExplorationOpened(h.Workspace);
        h.PreparationTime.Advance(Delay);
        await ChatHarness.UntilAsync(() => Requests(h) == 3);
        var during = await service.GetQuestionsAsync(h.Workspace, Ct);
        Assert.True(during.IsPreparing);
        Assert.Equal(2, during.Items.Count);
        h.Client.Recommendations.Gate = null;
        gate.TrySetResult();
        await service.SettledAsync();
        Assert.Equal(3, (await service.GetQuestionsAsync(h.Workspace, Ct)).Items.Count);
    }

    [Fact]
    public async Task Nothing_is_prepared_with_automatic_preparation_or_questions_off_and_a_quiet_failure_is_kept_to_show()
    {
        await using var h = await ChatHarness.StartAsync();
        await TitledAsync(h, "蒸汽机是怎么工作的？", "蒸汽机原理");
        var service = h.Recommendations;
        await h.RecommendationSettings.SetAutoPrepareAsync(false, Ct);
        service.NewExplorationOpened(h.Workspace);
        await AdvanceAsync(h, Delay);
        await h.RecommendationSettings.SetAutoPrepareAsync(true, Ct);
        await h.RecommendationSettings.SetQuestionsAsync(false, Ct);
        service.NewExplorationOpened(h.Workspace);
        await AdvanceAsync(h, Delay);
        Assert.Equal(0, Requests(h));
        await h.RecommendationSettings.SetQuestionsAsync(true, Ct);

        // A background failure is kept (in memory) to be mentioned quietly, and not retried by opening again.
        h.Client.Recommendations.FailNext(GenerationErrorKind.ServerError);
        service.NewExplorationOpened(h.Workspace);
        await AdvanceAsync(h, Delay);
        Assert.Equal(1, Requests(h));
        Assert.Equal(RecommendationOutcome.Refused(RecommendationProblem.Failed, GenerationErrorKind.ServerError), (await service.GetQuestionsAsync(h.Workspace, Ct)).Failure);
        service.NewExplorationOpened(h.Workspace);
        await AdvanceAsync(h, Delay);
        Assert.Equal(1, Requests(h));

        // A refused key is not mentioned at all; 重试 (a manual request) clears the note.
        Assert.Equal(new RecommendationOutcome(3), await service.RefreshQuestionsAsync(h.Workspace, Ct));
        Assert.Null((await service.GetQuestionsAsync(h.Workspace, Ct)).Failure);
    }

    [Fact]
    public async Task Without_a_model_the_background_asks_nothing_and_asks_once_a_model_is_there()
    {
        await using var h = await ChatHarness.StartAsync();
        await TitledAsync(h, "蒸汽机是怎么工作的？", "蒸汽机原理");
        await h.Database.WriteAsync((c, t) => c.Execute(t, "DELETE FROM app_state WHERE key = 'active_model_config_id';"), Ct);
        h.Recommendations.NewExplorationOpened(h.Workspace);
        await AdvanceAsync(h, Delay);
        Assert.Equal(0, Requests(h));
        Assert.Null((await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct)).Failure);

        await h.Models.SaveAsync(new Application.Models.ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test"), Ct);
        h.Recommendations.NewExplorationOpened(h.Workspace);
        await AdvanceAsync(h, Delay);
        Assert.Equal(1, Requests(h));
    }

    // ── Priority, one at a time, deletions and late results ─────────────────────

    [Fact]
    public async Task A_recommendation_never_starts_while_the_users_work_waits_and_is_not_interrupted_once_it_runs()
    {
        await using var h = await ChatHarness.StartAsync(slots: 1);
        var steam = await TitledAsync(h, "蒸汽机是怎么工作的？", "蒸汽机原理");

        // The only slot answers: the recommendation waits.
        var held = h.Client.Hold("一");
        await h.Conversations.AskAsync(steam, "再问一", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var refresh = h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct);
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 2);
        Assert.Equal(0, Requests(h));
        var gate = h.Client.Recommendations.Hold();
        held.Finish("完");

        // Once it runs, a new question waits for it instead of taking its slot.
        await ChatHarness.UntilAsync(() => Requests(h) == 1);
        var waiting = h.Client.Hold("二");
        await h.Conversations.AskAsync(steam, "再问二", Ct);
        await ChatHarness.StaysAsync(() => !waiting.Started.Task.IsCompleted, 200);
        h.Client.Recommendations.Gate = null;
        gate.TrySetResult();
        Assert.Equal(new RecommendationOutcome(3), await refresh);
        await waiting.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        waiting.Finish("完");
    }

    [Fact]
    public async Task One_request_per_workspace_at_a_time()
    {
        await using var h = await ChatHarness.StartAsync();
        await TitledAsync(h, "蒸汽机是怎么工作的？", "蒸汽机原理");
        var gate = h.Client.Recommendations.Hold();
        var first = h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct);
        var second = h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct);
        h.Recommendations.NewExplorationOpened(h.Workspace);
        h.PreparationTime.Advance(Delay);
        await ChatHarness.UntilAsync(() => Requests(h) == 1);
        await ChatHarness.StaysAsync(() => Requests(h) == 1, 200);
        h.Client.Recommendations.Gate = null;
        gate.TrySetResult();
        Assert.Equal(await first, await second);
        await h.Recommendations.SettledAsync();
        Assert.Equal(1, Requests(h));
    }

    [Fact]
    public async Task Deleting_the_workspace_stops_its_request_and_removes_every_recommendation()
    {
        await using var h = await ChatHarness.StartAsync();
        var steam = await TitledAsync(h, "蒸汽机是怎么工作的？", "蒸汽机原理");
        await SummaryAsync(h, steam, "蒸汽机", "蒸汽推动活塞。");
        await h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct);
        var items = (await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct)).Items;
        await h.Recommendations.IgnoreAsync(items[2], Ct);

        var gate = h.Client.Recommendations.Hold();
        var running = h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct);
        await ChatHarness.UntilAsync(() => Requests(h) == 2);
        var deleted = await h.WorkspaceDeletion.DeleteAsync(h.Workspace, Ct);
        Assert.Equal(Application.Workspaces.WorkspaceDeleteStatus.Deleted, deleted.Status);
        Assert.Equal(RecommendationProblem.Cancelled, (await running.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Problem);
        gate.TrySetResult();
        Assert.Equal(0, await h.CountAsync("recommendation"));
    }

    [Fact]
    public async Task Deleting_a_conversation_or_a_summarys_last_version_removes_what_drew_on_it_and_a_late_item_for_it_is_left_out()
    {
        await using var h = await ChatHarness.StartAsync();
        var steam = await TitledAsync(h, "蒸汽机是怎么工作的？", "蒸汽机原理");
        var steamNode = await SummaryAsync(h, steam, "蒸汽机", "蒸汽推动活塞。");
        var rails = await TitledAsync(h, "铁轨为什么是工字形？", "铁轨形状");
        var railsNode = await SummaryAsync(h, rails, "铁轨", "工字形截面。");
        h.Client.Recommendations.Replies.Enqueue("[{\"question\":\"蒸汽机的效率有多高？\",\"basis\":\"S2\"},{\"question\":\"铁轨怎么铺？\",\"basis\":\"S1\"},{\"question\":\"火车怎么转弯？\",\"basis\":\"\"}]");
        await h.Recommendations.RefreshQuestionsAsync(h.Workspace, Ct);
        var items = (await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct)).Items;
        Assert.Equal([steamNode, railsNode, null], items.Select(i => i.Basis));
        await h.Recommendations.IgnoreAsync(items[0], Ct);

        // The steam summary's last version deleted: its node goes, and with it the row that drew on it (an ignored one).
        var detail = (await h.SummaryService.GetAsync(steamNode, Ct))!;
        await h.SummaryService.DeleteVersionsAsync(steamNode, detail.Versions.Select(v => v.Id).ToHashSet(), Ct);
        Assert.Equal(2, await h.CountAsync("recommendation"));

        // The rails conversation deleted: its summary goes, and the row extending it.
        Assert.Equal(ConversationDeleteStatus.Deleted, (await h.DeleteAsync(rails)).Status);
        Assert.Equal(["火车怎么转弯？"], (await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct)).Items.Select(i => i.Question));

        // A batch that arrives after its basis went: that item is left out, the others are kept.
        var stored = await h.RecommendationStore.AddBatchAsync(h.Workspace, RecommendationKind.Question,
            new RecommendationBatch(Guid.CreateVersion7(), DateTimeOffset.UtcNow, [new("蒸汽机还在吗？", null, steamNode), new("轨距是多少？", null, null)]), true, Ct);
        Assert.Equal(1, stored);
        Assert.Equal(["轨距是多少？"], (await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct)).Items.Select(i => i.Question));

        // Review 2026-11-13: a batch whose every item lost its basis stores nothing and replaces nothing; the shown ones
        // stay, as after any failure (§11 "生成失败时旧推荐保留").
        stored = await h.RecommendationStore.AddBatchAsync(h.Workspace, RecommendationKind.Question,
            new RecommendationBatch(Guid.CreateVersion7(), DateTimeOffset.UtcNow, [new("蒸汽机还在吗？", null, steamNode)]), true, Ct);
        Assert.Equal(0, stored);
        Assert.Equal(["轨距是多少？"], (await h.Recommendations.GetQuestionsAsync(h.Workspace, Ct)).Items.Select(i => i.Question));
    }

    private static SummaryNodeId Node(int n) => new(new Guid(n, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]));

    private static RecommendationInput Input(params (string Label, string Name)[] summaries) =>
        new(["火车历史"], 1, [.. summaries.Select((s, i) => new RecommendationSource(s.Label, Node(i + 1), s.Name, s.Name, "摘录"))], summaries.Length, []);

    private static int Requests(ChatHarness h) => h.Client.Requests.Count(FakeChatClient.IsRecommendation);

    /// <summary>Moves the standing clock, then waits for whatever that started to end.</summary>
    private static async Task AdvanceAsync(ChatHarness h, TimeSpan by)
    {
        h.PreparationTime.Advance(by);
        await h.Recommendations.SettledAsync();
    }

    /// <summary>A new conversation with its automatic title <paramref name="title"/> stored.</summary>
    private static async Task<ConversationId> TitledAsync(ChatHarness h, string question, string title)
    {
        h.Client.Titles.Replies.Enqueue(title);
        var (root, _) = await h.RootWithAnswerAsync(question);
        await h.SettleAsync(root);
        return root;
    }

    /// <summary>A summary of the conversation with this short name and body (a candidate when one exists already).</summary>
    private static async Task<SummaryNodeId> SummaryAsync(ChatHarness h, ConversationId conversation, string name, string body, bool candidate = false)
    {
        h.Client.Knowledge.Replies.Enqueue($"标题：{name}的说明\n短名：{name}\n\n{body}");
        var node = await h.SummarizeAsync(conversation);
        if (candidate)
        {
            Assert.NotNull((await h.SummaryService.ForConversationAsync(conversation, Ct))!.Node.Pending);
        }

        return node;
    }
}
