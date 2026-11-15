using Mindweave.Application.Conversations;
using Mindweave.Application.Recommendations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;
using Mindweave.Testing.Usage;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Recommended network nodes (requirements §10.3.2, 2026-11-12) over the real stores and coordinator: how many by the
/// number of summaries, when the network prepares them by itself (on a standing clock) and when it must not, what 忽略,
/// exploring, 换一批推荐 and 隐藏推荐 do, and what deleting a basis summary or the workspace leaves.
/// </summary>
public class RecommendedNodeTests
{
    private static readonly TimeSpan Delay = new RecommendationOptions().Delay;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_number_follows_the_tiers_of_saved_summaries()
    {
        Assert.Equal([0, 1, 1, 2, 2, 3, 3], new[] { 0, 1, 10, 11, 30, 31, 200 }.Select(RecommendationService.NodeTarget));
    }

    [Fact]
    public async Task Opening_a_network_with_one_summary_prepares_one_node_once_and_opening_again_sends_nothing()
    {
        await using var h = await ChatHarness.StartAsync();
        var seed = new UsageSeed(h.Database);
        await SummariesAsync(h, seed, 1);
        Assert.Empty((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);

        await OpenAsync(h);

        var request = Assert.Single(h.Client.Requests, FakeChatClient.IsNodeRecommendation);
        Assert.Contains("推荐 1 个", request.Turns[0].Content, StringComparison.Ordinal);
        var offer = await h.Recommendations.GetNodesAsync(h.Workspace, Ct);
        Assert.True(offer is { Enabled: true, SummaryCount: 1, Hidden: false, IsPreparing: false, Failure: null });
        var node = Assert.Single(offer.Items);
        Assert.Equal(("蒸汽机车的锅炉为什么会爆炸？", "从动力往安全延伸", "总结"), (node.Question, node.Reason, node.BasisName));
        Assert.Equal((await NodesAsync(h))[0], node.Basis);
        var recorded = Assert.Single(await h.Requests.ForWorkspaceAsync(h.Workspace, Ct));
        Assert.Equal(RequestAttribution.RecommendNodes, recorded.Attribution!.Kind);

        // Valid ones are kept: opening the network again, even after a restart's worth of forgetting in this run, asks nothing.
        await OpenAsync(h);
        await OpenAsync(h);
        Assert.Single(h.Client.Requests, FakeChatClient.IsNodeRecommendation);

        // Other network changes do not ask either: a new titled conversation, a summary within the same tier.
        await SummariesAsync(h, seed, 1);
        await OpenAsync(h);
        Assert.Single(h.Client.Requests, FakeChatClient.IsNodeRecommendation);
        Assert.Single((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);
    }

    [Theory]
    [InlineData(11, 2)]
    [InlineData(31, 3)]
    public async Task Eleven_and_thirty_one_summaries_get_two_and_three_nodes(int summaries, int expected)
    {
        await using var h = await ChatHarness.StartAsync();
        await SummariesAsync(h, new UsageSeed(h.Database), summaries);

        await OpenAsync(h);

        var request = Assert.Single(h.Client.Requests, FakeChatClient.IsNodeRecommendation);
        Assert.Contains($"推荐 {expected} 个", request.Turns[0].Content, StringComparison.Ordinal);
        var offer = await h.Recommendations.GetNodesAsync(h.Workspace, Ct);
        Assert.Equal(expected, offer.Items.Count);
        Assert.All(offer.Items, n => Assert.NotNull(n.Basis));
    }

    [Fact]
    public async Task Rising_across_a_tier_asks_only_for_the_missing_one_and_falling_drops_the_extra_without_a_request()
    {
        await using var h = await ChatHarness.StartAsync();
        var seed = new UsageSeed(h.Database);
        await SummariesAsync(h, seed, 10);
        await OpenAsync(h);
        var first = Assert.Single((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);

        // The 11th summary: one more node, asked for alone, avoiding the one shown; the first stays.
        await SummariesAsync(h, seed, 1);
        await OpenAsync(h);
        var requests = h.Client.Requests.Where(FakeChatClient.IsNodeRecommendation).ToList();
        Assert.Equal(2, requests.Count);
        Assert.Contains("推荐 1 个", requests[1].Turns[0].Content, StringComparison.Ordinal);
        Assert.Contains(first.Question, requests[1].Turns[1].Content, StringComparison.Ordinal);
        var two = (await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items;
        Assert.Equal(2, two.Count);
        Assert.Equal(first.Id, two[0].Id);

        // Back to ten (a summary that is no basis deleted): the newer one goes at once, and no request is made.
        var bases = two.Select(n => n.Basis!.Value).ToHashSet();
        var other = (await NodesAsync(h)).First(n => !bases.Contains(n));
        await DeleteSummaryAsync(h, other);
        Assert.Equal([first.Id], (await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items.Select(n => n.Id));
        await OpenAsync(h);
        Assert.Equal(2, h.Client.Requests.Count(FakeChatClient.IsNodeRecommendation));
        Assert.Equal(1, await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM recommendation WHERE kind = 'node';"), Ct));
    }

    [Fact]
    public async Task An_ignored_slot_stays_empty_until_a_new_batch_or_a_higher_tier()
    {
        await using var h = await ChatHarness.StartAsync();
        var seed = new UsageSeed(h.Database);
        await SummariesAsync(h, seed, 1);
        await OpenAsync(h);
        var node = Assert.Single((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);

        await h.Recommendations.IgnoreAsync(node, Ct);
        Assert.Empty((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);
        await OpenAsync(h);
        await SummariesAsync(h, seed, 2);
        await OpenAsync(h);
        Assert.Single(h.Client.Requests, FakeChatClient.IsNodeRecommendation);
        Assert.Empty((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);

        // 换一批推荐: as many as the tier allows, the ignored question not offered again.
        h.Client.NodeRecommendations.Replies.Enqueue("[{\"question\":\"蒸汽机车的锅炉为什么会爆炸\",\"basis\":\"S1\"},{\"question\":\"火车头为什么要加沙子？\",\"basis\":\"S2\"}]");
        Assert.Equal(new RecommendationOutcome(1), await h.Recommendations.RefreshNodesAsync(h.Workspace, Ct));
        var fresh = Assert.Single((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);
        Assert.Equal("火车头为什么要加沙子？", fresh.Question);
        Assert.Contains(RecommendationText.Normalize(node.Question), h.Client.Requests.Last(FakeChatClient.IsNodeRecommendation).Turns[1].Content, StringComparison.Ordinal);

        // Ignored again, then the tier rises: every slot of the new tier is filled, the ignored one included.
        await h.Recommendations.IgnoreAsync(fresh, Ct);
        await SummariesAsync(h, seed, 8);
        await OpenAsync(h);
        Assert.Equal(2, (await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items.Count);
        Assert.Contains("推荐 2 个", h.Client.Requests.Last(FakeChatClient.IsNodeRecommendation).Turns[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_explored_node_is_used_and_leaves_its_slot_empty()
    {
        await using var h = await ChatHarness.StartAsync();
        await SummariesAsync(h, new UsageSeed(h.Database), 1);
        await OpenAsync(h);
        var node = Assert.Single((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);

        await h.Recommendations.MarkUsedAsync(h.Workspace, node.Id, Ct);

        var row = Assert.Single(await h.RecommendationStore.ListAsync(h.Workspace, RecommendationKind.Node, Ct));
        Assert.Equal((RecommendationState.Used, node.Question), (row.State, row.Question));
        await OpenAsync(h);
        Assert.Single(h.Client.Requests, FakeChatClient.IsNodeRecommendation);
        Assert.Empty((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);
    }

    [Fact]
    public async Task Deleting_a_basis_summary_removes_its_node_and_prepares_one_for_the_free_slot()
    {
        await using var h = await ChatHarness.StartAsync();
        await SummariesAsync(h, new UsageSeed(h.Database), 2);
        await OpenAsync(h);
        var node = Assert.Single((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);

        await DeleteSummaryAsync(h, node.Basis!.Value);
        Assert.Empty((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);
        Assert.Equal(0, await h.CountAsync("recommendation"));

        await OpenAsync(h);
        Assert.Equal(2, h.Client.Requests.Count(FakeChatClient.IsNodeRecommendation));
        var again = Assert.Single((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);
        Assert.Equal((await NodesAsync(h)).Single(), again.Basis);
    }

    [Fact]
    public async Task Deleting_the_summary_an_ignored_or_explored_node_extended_does_not_refill_its_slot()
    {
        // Review 2026-11-13: the ignored row goes with its basis summary (cascade), and the slot it had spent was then
        // counted free again: deleting that summary made a request §10.3.2 does not ask for (忽略后的空位不自动补足).
        await using var h = await ChatHarness.StartAsync();
        var seed = new UsageSeed(h.Database);
        await SummariesAsync(h, seed, 3);
        await OpenAsync(h);
        var node = Assert.Single((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);
        await h.Recommendations.IgnoreAsync(node, Ct);

        await DeleteSummaryAsync(h, node.Basis!.Value);
        Assert.Equal(0, await h.CountAsync("recommendation"));
        await OpenAsync(h);
        Assert.Single(h.Client.Requests, FakeChatClient.IsNodeRecommendation);
        Assert.Empty((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);

        // The same for an explored one, after 换一批推荐 filled the slot again.
        h.Client.NodeRecommendations.Replies.Enqueue("[{\"question\":\"火车头为什么要加沙子？\",\"basis\":\"S1\"}]");
        Assert.Equal(new RecommendationOutcome(1), await h.Recommendations.RefreshNodesAsync(h.Workspace, Ct));
        var explored = Assert.Single((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);
        await h.Recommendations.MarkUsedAsync(h.Workspace, explored.Id, Ct);
        await DeleteSummaryAsync(h, explored.Basis!.Value);
        await OpenAsync(h);
        Assert.Equal(2, h.Client.Requests.Count(FakeChatClient.IsNodeRecommendation));

        // A shown node's basis deleted still frees its slot.
        h.Client.NodeRecommendations.Replies.Enqueue("[{\"question\":\"铁轨为什么要留缝？\",\"basis\":\"S1\"}]");
        Assert.Equal(new RecommendationOutcome(1), await h.Recommendations.RefreshNodesAsync(h.Workspace, Ct));
        var shown = Assert.Single((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);
        await DeleteSummaryAsync(h, shown.Basis!.Value);
        await SummariesAsync(h, seed, 1);
        h.Client.NodeRecommendations.Replies.Enqueue("[{\"question\":\"枕木为什么换成了水泥？\",\"basis\":\"S1\"}]");
        await OpenAsync(h);
        Assert.Equal(4, h.Client.Requests.Count(FakeChatClient.IsNodeRecommendation));
        Assert.Single((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);
    }

    [Fact]
    public async Task Hidden_nodes_are_remembered_and_not_prepared_and_off_or_manual_only_prepares_nothing()
    {
        await using var h = await ChatHarness.StartAsync();
        await SummariesAsync(h, new UsageSeed(h.Database), 1);

        await h.Recommendations.SetNodesHiddenAsync(h.Workspace, true, Ct);
        Assert.Equal("1", await h.Preferences.GetAsync(RecommendationService.NodesHiddenKeyPrefix + h.Workspace, Ct));
        Assert.True((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Hidden);
        await OpenAsync(h);
        Assert.DoesNotContain(h.Client.Requests, FakeChatClient.IsRecommendation);

        // Shown again: prepared then.
        await h.Recommendations.SetNodesHiddenAsync(h.Workspace, false, Ct);
        Assert.Null(await h.Preferences.GetAsync(RecommendationService.NodesHiddenKeyPrefix + h.Workspace, Ct));
        await AdvanceAsync(h, Delay);
        Assert.Single(h.Client.Requests, FakeChatClient.IsNodeRecommendation);
    }

    [Fact]
    public async Task Turned_off_or_without_auto_preparation_nothing_is_asked_by_itself()
    {
        await using var h = await ChatHarness.StartAsync();
        await SummariesAsync(h, new UsageSeed(h.Database), 1);

        await h.RecommendationSettings.SetNodesAsync(false, Ct);
        await OpenAsync(h);
        Assert.False((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Enabled);
        Assert.Equal(RecommendationProblem.Disabled, (await h.Recommendations.RefreshNodesAsync(h.Workspace, Ct)).Problem);

        await h.RecommendationSettings.SetNodesAsync(true, Ct);
        await h.RecommendationSettings.SetAutoPrepareAsync(false, Ct);
        await OpenAsync(h);
        Assert.DoesNotContain(h.Client.Requests, FakeChatClient.IsRecommendation);

        // Only 换一批推荐 makes them then.
        Assert.Equal(new RecommendationOutcome(1), await h.Recommendations.RefreshNodesAsync(h.Workspace, Ct));
        Assert.Single((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Items);
    }

    [Fact]
    public async Task Without_summaries_nothing_is_offered_and_a_background_failure_is_kept_quietly_and_not_retried()
    {
        await using var h = await ChatHarness.StartAsync();
        var seed = new UsageSeed(h.Database);
        await seed.ConversationAsync(h.Workspace, DateTimeOffset.UtcNow);
        await OpenAsync(h);
        Assert.Equal(RecommendationProblem.NoContent, (await h.Recommendations.RefreshNodesAsync(h.Workspace, Ct)).Problem);
        Assert.DoesNotContain(h.Client.Requests, FakeChatClient.IsRecommendation);

        await SummariesAsync(h, seed, 1);
        h.Client.NodeRecommendations.FailNext(GenerationErrorKind.Network);
        await OpenAsync(h);
        var offer = await h.Recommendations.GetNodesAsync(h.Workspace, Ct);
        Assert.Equal(RecommendationProblem.Failed, offer.Failure?.Problem);
        await OpenAsync(h);
        Assert.Single(h.Client.Requests, FakeChatClient.IsNodeRecommendation);

        // 重试 clears it.
        Assert.Equal(new RecommendationOutcome(1), await h.Recommendations.RefreshNodesAsync(h.Workspace, Ct));
        Assert.Null((await h.Recommendations.GetNodesAsync(h.Workspace, Ct)).Failure);
    }

    [Fact]
    public async Task Deleting_the_workspace_removes_its_nodes_and_what_the_network_remembered()
    {
        await using var h = await ChatHarness.StartAsync();
        await SummariesAsync(h, new UsageSeed(h.Database), 1);
        await OpenAsync(h);
        await h.Recommendations.SetNodesHiddenAsync(h.Workspace, true, Ct);
        Assert.NotNull(await h.Preferences.GetAsync(RecommendationService.NodePlanKeyPrefix + h.Workspace, Ct));

        var deleted = await h.WorkspaceDeletion.DeleteAsync(h.Workspace, Ct);

        Assert.Equal(Application.Workspaces.WorkspaceDeleteStatus.Deleted, deleted.Status);
        Assert.Equal(0, await h.CountAsync("recommendation"));
        Assert.Null(await h.Preferences.GetAsync(RecommendationService.NodePlanKeyPrefix + h.Workspace, Ct));
        Assert.Null(await h.Preferences.GetAsync(RecommendationService.NodesHiddenKeyPrefix + h.Workspace, Ct));
    }

    /// <summary>
    /// <paramref name="count"/> more summaries in the workspace, each of its own titled conversation, written straight
    /// into the schema (a few seconds apart, so the newest is plain).
    /// </summary>
    private static async Task SummariesAsync(ChatHarness h, UsageSeed seed, int count)
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(-30);
        for (var i = 0; i < count; i++)
        {
            var conversation = await seed.ConversationAsync(h.Workspace, at);
            await seed.SummaryAsync(conversation, at.AddSeconds(i));
        }
    }

    private static async Task<List<SummaryNodeId>> NodesAsync(ChatHarness h) =>
        await h.Database.ReadAsync(
            c =>
            {
                using var command = c.Command(null, "SELECT id FROM summary_node WHERE workspace_id = $ws ORDER BY created_at, rowid;", ("$ws", h.Workspace.ToString()));
                using var reader = command.ExecuteReader();
                var list = new List<SummaryNodeId>();
                while (reader.Read())
                {
                    list.Add(SummaryNodeId.Parse(reader.GetString(0)));
                }

                return list;
            },
            Ct);

    /// <summary>A summary deleted as its last version's deletion does it (its node row goes; cascades do the rest).</summary>
    private static Task<int> DeleteSummaryAsync(ChatHarness h, SummaryNodeId node) =>
        h.Database.WriteAsync(
            (c, t) => c.Execute(t, "DELETE FROM summary_version WHERE node_id = $id;", ("$id", node.ToString()))
                + c.Execute(t, "DELETE FROM summary_node WHERE id = $id;", ("$id", node.ToString())),
            Ct);

    /// <summary>The network opened (or its summaries changed): the standing clock moves past the wait, and what started ends.</summary>
    private static async Task OpenAsync(ChatHarness h)
    {
        h.Recommendations.SummaryNetworkOpened(h.Workspace);
        await AdvanceAsync(h, Delay);
    }

    private static async Task AdvanceAsync(ChatHarness h, TimeSpan by)
    {
        h.PreparationTime.Advance(by);
        await h.Recommendations.SettledAsync();
    }
}
