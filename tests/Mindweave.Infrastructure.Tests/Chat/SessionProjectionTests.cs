using Mindweave.Application.Conversations;
using Mindweave.Application.SummaryNetwork;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>The session projection: every conversation is a node, summaries are an attribute of a node.</summary>
public class SessionProjectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_conversation_is_a_node_and_summaries_keep_their_ids()
    {
        await using var h = await ChatHarness.StartAsync();
        var r1 = await ConversationAsync(h, "火车是怎么发明的？");
        var a = await BranchAsync(h, r1, "谁造了第一台？");
        var b = await BranchAsync(h, r1, "铁轨用什么做？");
        var r2 = await ConversationAsync(h, "摄影怎么入门？");
        var r3 = await ConversationAsync(h, "咖啡怎么冲？");
        var c = await BranchAsync(h, r2, "镜头怎么选？");
        await h.SummarizeAsync(c);
        var summaryNode = (await h.SummaryService.ForConversationAsync(c, Ct))!.Node.Id;

        var projection = await h.SummaryNetwork.ProjectSessionsAsync(h.Workspace, Ct);

        Assert.Equal(6, projection.Nodes.Count);
        var node = projection.Nodes.ToDictionary(n => n.Conversation);
        Assert.Equal(summaryNode, node[c].Id);
        Assert.True(node[c].HasSummary);
        foreach (var plain in new[] { r1, a, b, r2, r3 })
        {
            Assert.Equal(plain.Value, node[plain].Id.Value);
            Assert.False(node[plain].HasSummary);
        }

        Assert.Equal(r1, node[b].Root);
    }

    [Fact]
    public async Task Branch_edges_link_parents_and_the_time_thread_links_consecutive_roots()
    {
        await using var h = await ChatHarness.StartAsync();
        var r1 = await ConversationAsync(h, "火车是怎么发明的？");
        var a = await BranchAsync(h, r1, "谁造了第一台？");
        var b = await BranchAsync(h, r1, "铁轨用什么做？");
        var r2 = await ConversationAsync(h, "摄影怎么入门？");
        var r3 = await ConversationAsync(h, "咖啡怎么冲？");

        var projection = await h.SummaryNetwork.ProjectSessionsAsync(h.Workspace, Ct);

        var id = projection.Nodes.ToDictionary(n => n.Conversation, n => n.Id);
        var actual = projection.Edges.Select(e => (e.Kind, e.From, e.To)).ToHashSet();
        var expected = new HashSet<(SummaryNetworkEdgeKind, SummaryNodeId, SummaryNodeId)>
        {
            (SummaryNetworkEdgeKind.Branch, id[r1], id[a]),
            (SummaryNetworkEdgeKind.Branch, id[r1], id[b]),
            (SummaryNetworkEdgeKind.TimeThread, id[r1], id[r2]),
            (SummaryNetworkEdgeKind.TimeThread, id[r2], id[r3]),
        };
        Assert.Equal(4, projection.Edges.Count);
        Assert.True(expected.SetEquals(actual));

        // Edges follow node order (created_at, then id): branch edges by child, then the thread by its later end.
        var position = projection.Nodes.Select((n, i) => (n.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var branchTargets = projection.Edges.Where(e => e.Kind == SummaryNetworkEdgeKind.Branch).Select(e => position[e.To]).ToList();
        Assert.Equal(branchTargets.Order().ToList(), branchTargets);
        var threadTargets = projection.Edges.Where(e => e.Kind == SummaryNetworkEdgeKind.TimeThread).Select(e => position[e.To]).ToList();
        Assert.Equal(threadTargets.Order().ToList(), threadTargets);
        Assert.All(projection.Edges, e => Assert.True(e.IsDirect));
    }

    [Fact]
    public async Task A_summarized_node_carries_its_conversations_creation_time()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await ConversationAsync(h, "火车是怎么发明的？");
        await h.SummarizeAsync(root);

        var node = Assert.Single((await h.SummaryNetwork.ProjectSessionsAsync(h.Workspace, Ct)).Nodes);

        Assert.True(node.HasSummary);
        Assert.Equal((await h.Store.GetAsync(root, Ct))!.CreatedAt, node.CreatedAt);
    }

    [Fact]
    public async Task Weights_add_descendant_questions()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await ConversationAsync(h, "火车是怎么发明的？");
        var asked = await h.Conversations.AskAsync(root, "它跑多快？", Ct);
        await h.WaitForAnswerAsync(asked.Answer!.Value);
        var branch = await BranchAsync(h, root, "谁造了第一台？");
        var leaf = await BranchAsync(h, branch, "第一问？");
        foreach (var question in new[] { "第二问？", "第三问？" })
        {
            var more = await h.Conversations.AskAsync(leaf, question, Ct);
            await h.WaitForAnswerAsync(more.Answer!.Value);
        }

        var projection = await h.SummaryNetwork.ProjectSessionsAsync(h.Workspace, Ct);

        var weight = projection.Nodes.ToDictionary(n => n.Conversation, n => n.Weight);
        Assert.Equal(6, weight[root]);
        Assert.Equal(4, weight[branch]);
        Assert.Equal(3, weight[leaf]);
    }

    [Fact]
    public void Session_weights_count_at_least_one_per_conversation_and_need_no_recursion()
    {
        var chain = Enumerable.Range(0, 20000).Select(_ => ConversationId.New()).ToList();
        var parents = new Dictionary<ConversationId, ConversationId?>();
        for (var i = 0; i < chain.Count; i++)
        {
            parents[chain[i]] = i == 0 ? null : chain[i - 1];
        }

        var weights = SessionWeights.Of(parents, new Dictionary<ConversationId, int> { [chain[^1]] = 5 });

        Assert.Equal(20000 - 1 + 5, weights[chain[0]]);
        Assert.Equal(5, weights[chain[^1]]);
    }

    [Fact]
    public async Task Archived_conversations_stay_and_say_so()
    {
        await using var h = await ChatHarness.StartAsync();
        var keep = await ConversationAsync(h, "火车是怎么发明的？");
        var gone = await ConversationAsync(h, "摄影怎么入门？");
        Assert.True((await h.Conversations.ArchiveAsync(gone, Ct)).Archived);

        var projection = await h.SummaryNetwork.ProjectSessionsAsync(h.Workspace, Ct);

        var node = projection.Nodes.ToDictionary(n => n.Conversation);
        Assert.Equal(2, node.Count);
        Assert.True(node[gone].SourceArchived);
        Assert.False(node[keep].SourceArchived);
    }

    [Fact]
    public async Task An_untitled_conversation_is_labelled_by_its_first_question()
    {
        await using var h = await ChatHarness.StartAsync();
        const string question = "火车是怎么发明的？谁造了第一台蒸汽机车呢";
        var conversation = await ConversationAsync(h, question);
        await h.TitleEndedAsync(conversation);
        await h.Database.WriteAsync(
            (c, t) => c.Execute(t, "UPDATE conversation SET title = NULL, title_source = 'none' WHERE id = $id;", ("$id", conversation.ToString())),
            Ct);

        var node = Assert.Single((await h.SummaryNetwork.ProjectSessionsAsync(h.Workspace, Ct)).Nodes);

        Assert.Equal(question, node.ShortTitle);
        Assert.Equal(question, node.DisplayName);
        Assert.Equal(question, node.FirstQuestion);
    }

    [Fact]
    public async Task An_untitled_conversations_long_question_is_cut_as_the_sidebar_cuts_it()
    {
        // Final review M3 and I2: while there is no title yet, a dot is named as the sidebar names the conversation, on
        // one line and cut to the fallback title's length; its display name is the same. The details keep the whole question.
        await using var h = await ChatHarness.StartAsync();
        const string question = "火车是怎么发明的？\n谁造了第一台蒸汽机车呢？它最早在哪个国家的哪条线路上跑起来的";
        var conversation = await ConversationAsync(h, question);
        await h.TitleEndedAsync(conversation);
        await h.Database.WriteAsync(
            (c, t) => c.Execute(t, "UPDATE conversation SET title = NULL, title_source = 'none' WHERE id = $id;", ("$id", conversation.ToString())),
            Ct);

        var node = Assert.Single((await h.SummaryNetwork.ProjectSessionsAsync(h.Workspace, Ct)).Nodes);

        Assert.Equal(ConversationRules.FallbackTitle(question), node.ShortTitle);
        Assert.Equal("火车是怎么发明的？ 谁造了第一台蒸汽机车呢？它最早在哪个国家…", node.ShortTitle);
        Assert.Equal(node.ShortTitle, node.DisplayName);
        Assert.Equal(question, node.FirstQuestion);
    }

    [Fact]
    public async Task An_untitled_branch_is_named_by_its_quote()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await ConversationAsync(h, "火车是怎么发明的？");
        var branch = await BranchAsync(h, root, "谁造了第一台？");
        await h.TitleEndedAsync(branch);
        await h.Database.WriteAsync(
            (c, t) => c.Execute(t, "UPDATE conversation SET title = NULL, title_source = 'none' WHERE id = $id;", ("$id", branch.ToString())),
            Ct);
        var quote = (await h.Conversations.ListAsync(h.Workspace, Ct)).Single(c => c.Conversation.Id == branch).Quote!;

        var node = (await h.SummaryNetwork.ProjectSessionsAsync(h.Workspace, Ct)).Nodes.Single(n => n.Conversation == branch);

        Assert.Equal("「" + quote + "」", node.ShortTitle);
        Assert.Equal(ConversationNames.Quoted(quote), node.ShortTitle);
        Assert.Equal(node.ShortTitle, node.DisplayName);
    }

    [Fact]
    public async Task A_session_dot_carries_its_whole_title()
    {
        // The timeline gives every label the room it needs, so a dot's short title is never cut.
        await using var h = await ChatHarness.StartAsync();
        const string title = "蒸汽机车与工业革命如何改变了城市与乡村生活方式的历史回顾展望";
        Assert.Equal(30, title.Length);
        var conversation = await ConversationAsync(h, "火车是怎么发明的？");
        await h.TitleEndedAsync(conversation);
        await h.Database.WriteAsync(
            (c, t) => c.Execute(t, "UPDATE conversation SET title = $title, title_source = 'manual' WHERE id = $id;", ("$title", title), ("$id", conversation.ToString())),
            Ct);

        var node = Assert.Single((await h.SummaryNetwork.ProjectSessionsAsync(h.Workspace, Ct)).Nodes);

        Assert.Equal(title, node.ShortTitle);
    }

    [Fact]
    public async Task A_conversation_with_neither_title_nor_question_is_a_new_conversation()
    {
        await using var h = await ChatHarness.StartAsync();
        var bare = ConversationId.New();
        await h.Database.WriteAsync(
            (c, t) => c.Execute(
                t,
                "INSERT INTO conversation (id, workspace_id, title, title_source, created_at, updated_at) VALUES ($id, $ws, NULL, 'none', 1, 1);",
                ("$id", bare.ToString()), ("$ws", h.Workspace.ToString())),
            Ct);

        var node = Assert.Single((await h.SummaryNetwork.ProjectSessionsAsync(h.Workspace, Ct)).Nodes);

        Assert.Equal(bare, node.Conversation);
        Assert.Null(node.FirstQuestion);
        Assert.Equal("新会话", node.ShortTitle);
        Assert.Equal("新会话", node.DisplayName);
    }

    [Fact]
    public async Task Conversations_started_at_the_same_moment_are_ordered_by_id_on_every_load()
    {
        await using var h = await ChatHarness.StartAsync();
        List<ConversationId> started = [await ConversationAsync(h, "火车？"), await ConversationAsync(h, "摄影？"), await ConversationAsync(h, "咖啡？")];
        var byId = started.OrderBy(c => c.Value).ToList();

        // One created_at for all; the store lists by updated_at, first in id order, then reversed.
        foreach (var listing in new[] { byId, Enumerable.Reverse(byId).ToList() })
        {
            await h.Database.WriteAsync(
                (c, t) => listing.Select((id, i) => c.Execute(
                    t,
                    "UPDATE conversation SET created_at = 1000, updated_at = $updated WHERE id = $id;",
                    ("$updated", 2000 + i), ("$id", id.ToString()))).Sum(),
                Ct);
            Assert.Equal(Enumerable.Reverse(listing), (await h.Conversations.ListAsync(h.Workspace, Ct)).Select(c => c.Conversation.Id));

            var projection = await h.SummaryNetwork.ProjectSessionsAsync(h.Workspace, Ct);

            Assert.Equal(byId, projection.Nodes.Select(n => n.Conversation));
            var thread = projection.Edges.Where(e => e.Kind == SummaryNetworkEdgeKind.TimeThread).Select(e => (e.From.Value, e.To.Value));
            Assert.Equal([(byId[0].Value, byId[1].Value), (byId[1].Value, byId[2].Value)], thread);
        }
    }

    private static async Task<ConversationId> ConversationAsync(ChatHarness h, string question)
    {
        var sent = await h.Conversations.StartAsync(h.Workspace, question, Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        return sent.Conversation;
    }

    private static async Task<ConversationId> BranchAsync(ChatHarness h, ConversationId parent, string question)
    {
        var source = (await h.Conversations.LoadAsync(parent, Ct))!.Messages.Last(m => m.Role == MessageRole.Assistant);
        var branch = await h.Conversations.CreateBranchAsync(parent, BranchFlowTests.Selection(source, source.Text[..2]), Ct);
        Assert.True(branch.Accepted, branch.Rejection?.ToString());
        var asked = await h.Conversations.AskAsync(branch.Child, question, Ct);
        await h.WaitForAnswerAsync(asked.Answer!.Value);
        return branch.Child;
    }
}
