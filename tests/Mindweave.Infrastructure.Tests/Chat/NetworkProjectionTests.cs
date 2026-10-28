using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// The network is derived from summaries and real branch sources (architecture §11): a summarized parent links
/// directly; unsummarized conversations in between stay on the edge; positions persist without reviving deleted nodes.
/// </summary>
public class NetworkProjectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Edges_follow_real_sources_and_keep_unsummarized_conversations_in_between()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await ConversationAsync(h, "火车是怎么发明的？");
        var middle = await BranchAsync(h, root, "谁造了第一台？");
        var leaf = await BranchAsync(h, middle, "它跑多快？");
        var sibling = await BranchAsync(h, root, "铁轨用什么做？");
        var other = await ConversationAsync(h, "摄影怎么入门？");
        foreach (var conversation in new[] { root, leaf, sibling, other })
        {
            await SummarizeAsync(h, conversation);
        }

        var projection = await h.Network.ProjectAsync(h.Workspace, Ct);

        Assert.Equal(4, projection.Nodes.Count);
        var node = projection.Nodes.ToDictionary(n => n.Conversation);
        Assert.Equal(root, node[leaf].Root);
        Assert.Equal(other, node[other].Root);
        var edges = projection.Edges.ToDictionary(e => e.To);
        Assert.True(edges[node[sibling].Id].IsDirect);
        Assert.Equal(node[root].Id, edges[node[sibling].Id].From);
        Assert.Equal(node[root].Id, edges[node[leaf].Id].From);
        Assert.Equal([middle], edges[node[leaf].Id].Via);
        Assert.False(edges.ContainsKey(node[root].Id));
        Assert.False(edges.ContainsKey(node[other].Id));
    }

    [Fact]
    public async Task Network_nodes_show_the_short_title_and_keep_the_full_name()
    {
        await using var h = await ChatHarness.StartAsync();
        var conversation = await ConversationAsync(h, "火车是怎么发明的？");
        h.Client.SummaryReplies.Enqueue("标题：早期蒸汽机车在铁路运输中的作用与局限\n短名：蒸汽机车\n\n正文");
        await SummarizeAsync(h, conversation);

        var node = Assert.Single((await h.Network.ProjectAsync(h.Workspace, Ct)).Nodes);

        Assert.Equal(("蒸汽机车", "早期蒸汽机车在铁路运输中的作用与局限"), (node.ShortTitle, node.DisplayName));

        // An edit keeps the short title of the version it was edited from.
        var entry = (await h.SummaryService.ForConversationAsync(conversation, Ct))!;
        await h.SummaryService.SaveEditAsync(entry.Node.Id, entry.Node.Current, entry.Current.Title, "改写", Ct);
        Assert.Equal("蒸汽机车", Assert.Single((await h.Network.ProjectAsync(h.Workspace, Ct)).Nodes).ShortTitle);
    }

    [Fact]
    public async Task A_placed_node_keeps_its_first_position_and_saving_never_revives_a_deleted_node()
    {
        await using var h = await ChatHarness.StartAsync();
        var a = await ConversationAsync(h, "火车？");
        var b = await ConversationAsync(h, "摄影？");
        await SummarizeAsync(h, a);
        await SummarizeAsync(h, b);
        var nodes = (await h.Network.ProjectAsync(h.Workspace, Ct)).Nodes;

        Assert.True(await h.Network.PlaceAsync(h.Workspace, nodes[0].Id, new NodePosition(10, 20), Ct));
        Assert.False(await h.Network.PlaceAsync(h.Workspace, nodes[0].Id, new NodePosition(99, 99), Ct));
        Assert.Equal(new NodePosition(10, 20), (await h.Network.GetLayoutAsync(h.Workspace, Ct))[nodes[0].Id]);

        // The second summary is deleted between taking the snapshot and saving it.
        var snapshot = new Dictionary<SummaryNodeId, NodePosition> { [nodes[0].Id] = new(1, 2), [nodes[1].Id] = new(3, 4) };
        var detail = (await h.SummaryService.GetAsync(nodes[1].Id, Ct))!;
        await h.SummaryService.DeleteVersionsAsync(nodes[1].Id, detail.Versions.Select(v => v.Id).ToHashSet(), Ct);

        Assert.Equal(1, await h.Network.SaveLayoutAsync(h.Workspace, snapshot, Ct));
        var layout = await h.Network.GetLayoutAsync(h.Workspace, Ct);
        Assert.Equal(new NodePosition(1, 2), layout[nodes[0].Id]);
        Assert.False(layout.ContainsKey(nodes[1].Id));
        Assert.Null(await h.SummaryService.GetAsync(nodes[1].Id, Ct));
    }

    [Fact]
    public async Task Deleting_a_summary_removes_its_saved_position()
    {
        await using var h = await ChatHarness.StartAsync();
        var a = await ConversationAsync(h, "火车？");
        await SummarizeAsync(h, a);
        var node = Assert.Single((await h.Network.ProjectAsync(h.Workspace, Ct)).Nodes);
        await h.Network.PlaceAsync(h.Workspace, node.Id, new NodePosition(5, 5), Ct);

        var detail = (await h.SummaryService.GetAsync(node.Id, Ct))!;
        await h.SummaryService.DeleteVersionsAsync(node.Id, detail.Versions.Select(v => v.Id).ToHashSet(), Ct);

        Assert.Empty(await h.Network.GetLayoutAsync(h.Workspace, Ct));
    }

    private static async Task SummarizeAsync(ChatHarness h, ConversationId conversation)
    {
        var outcome = await h.SummaryService.SummarizeAsync(conversation, Ct);
        Assert.True(outcome.Accepted, outcome.Rejection?.ToString());
        await ChatHarness.UntilAsync(() => h.SummaryService.LatestTaskAsync(conversation, Ct).Result is { State: GenerationTaskState.Completed } s && s.Id == outcome.Task);
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
