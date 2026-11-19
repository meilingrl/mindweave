using Mindweave.Application.Recommendations;
using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// A conversation started from a recommendation (requirements §10.3.5, owner's decision 2026-11-17) through the real
/// services and SQLite: it remembers the recommendation and its basis summary; its first summary is linked to the basis
/// once, as an ordinary content link (note "来自推荐", editable, deletable) and never as a branch source; a basis deleted
/// before, a question without a basis, a summary node made again, and deleting the conversation or workspace.
/// </summary>
public class StartingRecommendationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_first_summary_is_linked_to_the_basis_once_and_never_becomes_a_branch_source()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, basis) = await SummaryLinkFlowTests.SummarizedRootAsync(h, "蒸汽机是怎么工作的？");
        var recommendation = RecommendationId.New();
        var started = await StartAsync(h, "锅炉为什么会爆炸？", new(recommendation, RecommendationKind.Node, basis));
        Assert.Equal(
            (recommendation.ToString(), "node", basis.ToString(), true),
            await h.Database.ReadAsync(c => (c.Scalar<string>(null, "SELECT recommendation_id FROM starting_recommendation;"), c.Scalar<string>(null, "SELECT kind FROM starting_recommendation;"), c.Scalar<string>(null, "SELECT basis_node_id FROM starting_recommendation;"), c.Scalar<long?>(null, "SELECT applied_at FROM starting_recommendation;") is null), Ct));
        var changed = new List<Core.Workspaces.WorkspaceId>();
        h.Links.Changed += (_, ws) => changed.Add(ws);

        var node = await SummarizeAsync(h, started, "锅炉为什么会爆炸");

        var link = Assert.Single(await h.Links.ListAsync(h.Workspace, Ct));
        Assert.Equal((SummaryLinkPair.Normalize(node, basis), "来自推荐", SummaryLinkOrigin.Recommended), ((link.NodeA, link.NodeB), link.Note, link.Origin));
        // The notice may arrive just after the summary task reports done (2026-11-19: failed 1 in 20 runs under load).
        await ChatHarness.UntilAsync(() => changed.Count > 0);
        Assert.Equal([h.Workspace], changed);
        Assert.Equal(node, Assert.Single(await h.Links.ListForNodeAsync(basis, Ct)).Other);
        Assert.Null(await h.Conversations.GetSourceAsync(started, Ct));
        Assert.Equal(0, await h.CountAsync("branch_source"));
        Assert.Equal(1, await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM starting_recommendation WHERE applied_at IS NOT NULL;"), Ct));

        // Summarized again: a candidate, adopted; still the one link.
        await h.AskAsync(started, "怎么预防？");
        await h.SettleAsync(started);
        Assert.Equal(node, await SummarizeAsync(h, started, "锅炉安全"));
        await h.SummaryService.AdoptAsync(node, (await h.SummaryService.GetAsync(node, Ct))!.Pending!.Id, Ct);
        Assert.Equal([link.Id], (await h.Links.ListAsync(h.Workspace, Ct)).Select(l => l.Id));

        // Like any link: the note can be changed, and a deleted one is not made again by a later summary.
        Assert.Equal("原因", (await h.Links.EditNoteAsync(link.Id, "原因", Ct)).Note);
        Assert.True(await h.Links.DeleteAsync(link.Id, Ct));
        await h.AskAsync(started, "现在还会爆炸吗？");
        await h.SettleAsync(started);
        await SummarizeAsync(h, started, "现代锅炉");
        Assert.Empty(await h.Links.ListAsync(h.Workspace, Ct));
    }

    [Fact]
    public async Task A_basis_deleted_before_sending_or_before_the_first_summary_and_a_question_without_one_link_nothing()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, gone) = await SummaryLinkFlowTests.SummarizedRootAsync(h, "火车是怎么发明的？");
        var (_, later) = await SummaryLinkFlowTests.SummarizedRootAsync(h, "铁轨用什么做？");
        await DeleteSummaryAsync(h, gone);

        // The page still showed the deleted summary as the basis: the conversation remembers the recommendation only.
        var before = await StartAsync(h, "第一列火车跑多快？", new(RecommendationId.New(), RecommendationKind.Question, gone));
        var after = await StartAsync(h, "铁轨为什么要留缝？", new(RecommendationId.New(), RecommendationKind.Node, later));
        var none = await StartAsync(h, "高铁为什么快？", new(RecommendationId.New(), RecommendationKind.Question, null));
        Assert.Equal(3, await h.CountAsync("starting_recommendation"));
        Assert.Equal(1, await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM starting_recommendation WHERE basis_node_id IS NOT NULL;"), Ct));

        // The basis goes after sending, before the first summary.
        await DeleteSummaryAsync(h, later);
        Assert.Equal(0, await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM starting_recommendation WHERE basis_node_id IS NOT NULL;"), Ct));

        await SummarizeAsync(h, before, "第一列火车");
        await SummarizeAsync(h, after, "铁轨的缝");
        await SummarizeAsync(h, none, "高铁");
        Assert.Equal(0, await h.CountAsync("summary_link"));
        Assert.Equal(3, await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM starting_recommendation WHERE applied_at IS NOT NULL;"), Ct));
    }

    [Fact]
    public async Task A_summary_node_made_again_after_its_deletion_is_not_linked_again()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, basis) = await SummaryLinkFlowTests.SummarizedRootAsync(h, "蒸汽机是怎么工作的？");
        var started = await StartAsync(h, "锅炉为什么会爆炸？", new(RecommendationId.New(), RecommendationKind.Question, basis));
        var first = await SummarizeAsync(h, started, "锅炉为什么会爆炸");
        Assert.Single(await h.Links.ListAsync(h.Workspace, Ct));

        // Deleting the summary's last version deletes the node and, with it, the link (§10.3.3).
        var versions = (await h.SummaryService.GetAsync(first, Ct))!.Versions.Select(v => v.Id).ToHashSet();
        await h.SummaryService.DeleteVersionsAsync(first, versions, Ct);
        Assert.Null(await h.SummaryService.ForConversationAsync(started, Ct));
        Assert.Equal(0, await h.CountAsync("summary_link"));

        var second = await SummarizeAsync(h, started, "锅炉");
        Assert.NotEqual(first, second);
        Assert.Equal(0, await h.CountAsync("summary_link"));
    }

    [Fact]
    public async Task Deleting_the_conversation_or_the_workspace_deletes_what_it_remembers_and_a_plain_start_remembers_nothing()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, basis) = await SummaryLinkFlowTests.SummarizedRootAsync(h, "蒸汽机是怎么工作的？");
        var plain = await StartAsync(h, "火车有多重？", null);
        Assert.Equal(0, await h.CountAsync("starting_recommendation"));

        var started = await StartAsync(h, "锅炉为什么会爆炸？", new(RecommendationId.New(), RecommendationKind.Node, basis));
        var other = await StartAsync(h, "司机怎么看信号？", new(RecommendationId.New(), RecommendationKind.Node, basis));
        await SummarizeAsync(h, started, "锅炉为什么会爆炸");
        Assert.Equal(1, await h.CountAsync("summary_link"));

        // The conversation is deleted with its summary: its record and the link go; the basis summary stays.
        Assert.True((await h.DeleteAsync(started)).IsDeleted);
        Assert.Equal((1L, 0L), (await h.CountAsync("starting_recommendation"), await h.CountAsync("summary_link")));
        Assert.NotNull(await h.SummaryService.GetAsync(basis, Ct));

        Assert.Equal(Application.Workspaces.WorkspaceDeleteStatus.Deleted, (await h.WorkspaceDeletion.DeleteAsync(h.Workspace, Ct)).Status);
        Assert.Equal(0, await h.CountAsync("starting_recommendation"));
        Assert.Null(await h.Database.ReadAsync(c => SqliteConnections.ForeignKeyViolation(c), Ct));
        _ = (plain, other);
    }

    private static async Task<ConversationId> StartAsync(ChatHarness h, string question, StartingRecommendation? from)
    {
        var sent = await h.Conversations.StartAsync(h.Workspace, question, from, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        await h.SettleAsync(sent.Conversation);
        return sent.Conversation;
    }

    private static Task<SummaryNodeId> SummarizeAsync(ChatHarness h, ConversationId conversation, string name)
    {
        h.Client.Knowledge.Replies.Enqueue($"标题：{name}的总结\n短名：{name}\n\n关于{name}的正文。");
        return h.SummarizeAsync(conversation);
    }

    /// <summary>A summary deleted as its last version's deletion does it (its node row goes; cascades do the rest).</summary>
    private static Task<int> DeleteSummaryAsync(ChatHarness h, SummaryNodeId node) =>
        h.Database.WriteAsync(
            (c, t) => c.Execute(t, "DELETE FROM summary_version WHERE node_id = $id;", ("$id", node.ToString()))
                + c.Execute(t, "DELETE FROM summary_node WHERE id = $id;", ("$id", node.ToString())),
            Ct);
}
