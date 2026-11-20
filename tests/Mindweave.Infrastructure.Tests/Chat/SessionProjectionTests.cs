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
        Assert.All(projection.Edges, e => Assert.True(e.IsDirect));
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

        Assert.Equal(question[..10] + "…", node.ShortTitle);
        Assert.Equal(question, node.DisplayName);
        Assert.Equal(question, node.FirstQuestion);
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
