using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// What lists, the network and naming read (architecture §11.2; review F4, R5): no summary body for the network, and only
/// bounded excerpts of the versions naming was read with.
/// </summary>
public partial class SummaryReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_list_entry_carries_no_body()
    {
        Assert.Null(typeof(SummaryVersionHead).GetProperty("Body"));
        Assert.Equal(typeof(SummaryVersionHead), typeof(SummaryEntry).GetProperty(nameof(SummaryEntry.Current))!.PropertyType);
    }

    [Fact]
    public async Task Drawing_the_network_reads_no_summary_body()
    {
        await using var h = await ChatHarness.StartAsync();
        var node = await SummarizeAsync(h, "火车", new string('长', 5000));
        Assert.NotNull(await h.SummaryService.SaveEditAsync(node.Id, node.Current, "火车", new string('改', 5000), Ct));

        var statements = new ConcurrentQueue<string>();
        SqliteCommands.Observer.Value = statements.Enqueue;
        var projection = await h.Network.ProjectAsync(h.Workspace, Ct);
        await h.SummaryService.ListAsync(h.Workspace, Ct);
        SqliteCommands.Observer.Value = null;

        Assert.Single(projection.Nodes);
        Assert.NotEmpty(statements);
        Assert.DoesNotContain(statements, BodyColumn().IsMatch);
    }

    [Fact]
    public async Task Excerpts_are_cut_at_code_points_and_follow_the_versions_asked_for()
    {
        await using var h = await ChatHarness.StartAsync();
        var body = new string('甲', 599) + "𠀀" + new string('乙', 50);
        var node = await SummarizeAsync(h, "火车", body);
        var first = node.Current;
        var second = (await h.SummaryService.SaveEditAsync(node.Id, first, "火车", "第二版", Ct))!.Value;

        // The version read as current earlier still gives its own excerpt after another became current.
        var excerpts = await h.SummaryNodes.ExcerptsAsync([first, second, SummaryVersionId.New()], 600, Ct);
        Assert.Equal(2, excerpts.Count);
        Assert.Equal(new string('甲', 599) + "𠀀", excerpts[first]);
        Assert.Equal(601, excerpts[first].Length);
        Assert.Equal("第二版", excerpts[second]);

        // Gone meanwhile: left out, and naming drops that member.
        await h.SummaryService.DeleteVersionsAsync(node.Id, new HashSet<SummaryVersionId> { first }, Ct);
        Assert.False((await h.SummaryNodes.ExcerptsAsync([first], 600, Ct)).ContainsKey(first));
    }

    [Fact]
    public async Task Naming_sends_bounded_excerpts_read_by_version_only()
    {
        await using var h = await ChatHarness.StartAsync();
        await SummarizeAsync(h, "同名", new string('甲', 1000));

        var statements = new ConcurrentQueue<string>();
        SqliteCommands.Observer.Value = statements.Enqueue;
        await SummarizeAsync(h, "同名", new string('乙', 1000));
        await ChatHarness.UntilAsync(() => h.Client.Requests.Any(IsNaming));
        SqliteCommands.Observer.Value = null;

        var prompt = h.Client.Requests.Single(IsNaming).Turns[^1].Content;
        Assert.Contains(new string('甲', 600), prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('甲', 601), prompt, StringComparison.Ordinal);
        Assert.Contains(new string('乙', 600), prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('乙', 601), prompt, StringComparison.Ordinal);

        // Any statement reading a version body in this flow is the bounded excerpt by version, or a single node's detail.
        Assert.Contains(statements, s => s.Contains("substr(body, 1, $max)", StringComparison.Ordinal) && s.Contains("WHERE id IN", StringComparison.Ordinal));
        Assert.DoesNotContain(statements, s => BodyColumn().IsMatch(s) && s.Contains("workspace_id", StringComparison.Ordinal));
    }

    private static bool IsNaming(Application.Ai.ChatRequest request) =>
        request.Turns[0].Content.Contains("区分词", StringComparison.Ordinal);

    private static async Task<SummaryNode> SummarizeAsync(ChatHarness h, string title, string body)
    {
        var sent = await h.Conversations.StartAsync(h.Workspace, title + "是什么？", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        h.Client.SummaryReplies.Enqueue($"标题：{title}\n\n{body}");
        Assert.True((await h.SummaryService.SummarizeAsync(sent.Conversation, Ct)).Accepted);
        await ChatHarness.UntilAsync(() => h.SummaryService.ForConversationAsync(sent.Conversation, Ct).Result is not null);
        return (await h.SummaryService.ForConversationAsync(sent.Conversation, Ct))!.Node;
    }

    [GeneratedRegex(@"\bbody\b", RegexOptions.CultureInvariant)]
    private static partial Regex BodyColumn();
}
