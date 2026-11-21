using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views.Pages;
using Mindweave.Infrastructure.Sqlite;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// How the session network names and counts behind its gate: a dot, its details and the sidebar name a conversation by
/// one rule; the list view names a dot by that name, never its whole question; and 建议关联 counts summaries, not dots.
/// </summary>
public class SessionNetworkNamingTests
{
    [AvaloniaFact]
    public async Task Suggest_links_counts_summaries_not_dots()
    {
        // Final review I1: four conversations, one summary. 建议关联 needs three summaries, so it stays unavailable.
        await using var app = SessionNetworkUiTests.Gated();
        var window = app.ShowWindow(1100, 720);
        await SessionNetworkUiTests.SeedAsync(app);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        await SummaryNetworkScenario.AskAsync(app, conversations, null, app.Workbench.CurrentWorkspace!.Id, "咖啡怎么冲？");
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == 4);

        Assert.Single(network.Nodes, n => n.HasSummary);
        Assert.NotNull(network.Suggestions);
        Assert.False(network.Suggestions.CanSuggest);
        Assert.Equal(UiText.SuggestLinksNeedsSummaries, network.Suggestions.SuggestHelp);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_list_row_shows_the_title_not_a_long_question_and_only_summaries_are_muted()
    {
        // Final review I2: a 2,000-character question over many lines names its row by the title, as the details do, and
        // the row stays narrower than the list. A row without a summary has no reading state, so it is not muted as read.
        await using var app = SessionNetworkUiTests.Gated();
        var window = app.ShowWindow(1100, 720);
        var (_, _, plain) = await SessionNetworkUiTests.SeedAsync(app);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var question = string.Concat(Enumerable.Repeat("火车为什么这样跑？\n", 200));
        Assert.Equal(2000, question.Length);
        var longOne = await SummaryNetworkScenario.AskAsync(app, conversations, null, app.Workbench.CurrentWorkspace!.Id, question);
        await conversations.RenameAsync(longOne, "一个很长的提问", CancellationToken.None);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == 4);
        network.IsListView = true;
        window.UpdateLayout();
        window.UpdateLayout();

        var id = network.Session.Projection.Nodes.Single(n => n.Conversation == longOne).Id;
        Assert.Equal("一个很长的提问", network.Items.Single(i => i.Node == id).DisplayName);
        var list = Find<ItemsControl>(window.GetVisualDescendants().OfType<SummaryNetworkHomeView>().Single(), "SummaryList");
        var rows = list.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("list-name")).ToList();
        Assert.Equal(4, rows.Count);
        var row = rows.Single(b => Equals(b.CommandParameter, id));
        Assert.Equal("一个很长的提问", row.Content);
        Assert.True(row.Bounds.Width < list.Bounds.Width, $"the row is {row.Bounds.Width} wide in a list of {list.Bounds.Width}");

        var plainId = network.Session.Projection.Nodes.Single(n => n.Conversation == plain).Id;
        Assert.False(rows.Single(b => Equals(b.CommandParameter, plainId)).Classes.Contains("read"), "a row without a summary is muted as read");
        Screenshots.Capture(window, "session-network-list-long-question");
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_conversation_has_one_name_on_its_dot_in_its_details_and_in_the_sidebar()
    {
        // Final review M3: an untitled branch is named by its quote, an untitled conversation by its first question, and
        // one with neither by 新会话, the same on the dot, as the details heading and in the sidebar.
        await using var app = SessionNetworkUiTests.Gated();
        var window = app.ShowWindow(1100, 720);
        var (summarized, branch, plain) = await SessionNetworkUiTests.SeedAsync(app);
        var bare = ConversationId.New();
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var generation = app.Services.GetRequiredService<GenerationCoordinator>();
        await UiWait.UntilAsync(() => generation.UnfinishedCount == 0);
        await app.Services.GetRequiredService<SqliteDatabase>().WriteAsync(
            (c, t) => c.Execute(
                t,
                "UPDATE conversation SET title = NULL, title_source = 'none' WHERE id IN ($branch, $plain);",
                ("$branch", branch.ToString()), ("$plain", plain.ToString()))
                + c.Execute(
                    t,
                    "INSERT INTO conversation (id, workspace_id, title, title_source, created_at, updated_at) VALUES ($id, $ws, NULL, 'none', 1, 1);",
                    ("$id", bare.ToString()), ("$ws", workspace.ToString())),
            CancellationToken.None);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == 4);

        var listed = await app.Services.GetRequiredService<IConversationStore>().ListAsync(workspace, CancellationToken.None);
        var sidebar = listed.ToDictionary(c => c.Conversation.Id, c => UiText.ConversationTitle(c.Conversation, c.FirstQuestion, c.Quote));
        Assert.StartsWith("「", sidebar[branch], StringComparison.Ordinal);
        Assert.Equal("摄影怎么入门？", sidebar[plain]);
        Assert.Equal(UiText.NewConversation, sidebar[bare]);
        foreach (var conversation in new[] { branch, plain, bare })
        {
            var node = network.Session.Projection.Nodes.Single(n => n.Conversation == conversation);
            network.Select(node.Id);
            Assert.Equal(sidebar[conversation], network.Nodes.Single(n => n.Id == node.Id).ShortTitle);
            Assert.Equal(sidebar[conversation], network.Selected!.DisplayName);
            Assert.Equal(sidebar[conversation], network.Items.Single(i => i.Node == node.Id).DisplayName);
        }

        Assert.NotEqual(sidebar[summarized], network.Nodes.Single(n => n.HasSummary).ShortTitle);
        window.Close();
    }
}
