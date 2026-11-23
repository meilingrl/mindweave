using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// A seeded workspace for the knowledge-network UI tests: summarized conversations made through the services, and the
/// network page opened in a shown window.
/// </summary>
internal static class SummaryNetworkScenario
{
    /// <summary>Root "火车" summarized; a branch without a summary; its branch summarized; a sibling summarized.</summary>
    internal static async Task SeedAsync(DesktopHarness app, bool secondExploration = false)
    {
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var summaries = app.Services.GetRequiredService<SummaryService>();

        // The second exploration's summary comes first, so pairs with it keep it first (a pair is stored older end first).
        if (secondExploration)
        {
            var steam = await AskAsync(app, conversations, null, workspace, "蒸汽机是怎么工作的？");
            app.Client.Knowledge.Replies.Enqueue("标题：蒸汽机的详细说明\n短名：蒸汽机\n\n正文");
            var first = await summaries.SummarizeAsync(steam, CancellationToken.None);
            Assert.True(first.Accepted, first.Rejection?.ToString());
            await UiWait.UntilAsync(() => summaries.LatestTaskAsync(steam, CancellationToken.None).Result is { State: GenerationTaskState.Completed });
        }

        var root = await AskAsync(app, conversations, null, workspace, "火车是怎么发明的？");
        var middle = await BranchAsync(app, conversations, root, "谁造了第一台？");
        var leaf = await BranchAsync(app, conversations, middle, "它跑多快？");
        var sibling = await BranchAsync(app, conversations, root, "铁轨用什么做？");
        foreach (var (conversation, title) in new[] { (root, "火车起源"), (leaf, "火箭号速度"), (sibling, "铁轨") })
        {
            app.Client.Knowledge.Replies.Enqueue($"标题：{title}的详细说明\n短名：{title}\n\n正文");
            var outcome = await summaries.SummarizeAsync(conversation, CancellationToken.None);
            Assert.True(outcome.Accepted, outcome.Rejection?.ToString());
            await UiWait.UntilAsync(() => summaries.LatestTaskAsync(conversation, CancellationToken.None).Result is { State: GenerationTaskState.Completed });
        }
    }

    /// <summary>
    /// <see cref="SeedAsync"/> with every answer seen: the hints its answers popped up are gone, so none covers the details
    /// panel a test clicks in (they come up a moment after the answers, so a click may or may not race them otherwise).
    /// </summary>
    /// <param name="secondExploration">
    /// Also a second exploration: one summarized conversation, "蒸汽机", of its own. Link suggestions pair summaries across
    /// explorations or in separate branches, never on one line of descent (design session-network §8), so tests that need
    /// several suggestible pairs ask for it.
    /// </param>
    internal static async Task SeedSeenAsync(DesktopHarness app, bool secondExploration = false)
    {
        await SeedAsync(app, secondExploration);
        var marks = app.Services.GetRequiredService<ConversationMarksService>();
        var all = (await app.Services.GetRequiredService<ConversationService>().ListAsync(app.Workbench.CurrentWorkspace!.Id, CancellationToken.None))
            .Select(c => c.Conversation.Id).ToList();
        await UiWait.UntilAsync(() => all.All(marks.IsUnread));
        foreach (var id in all)
        {
            marks.MarkSeen(id);
        }

        await UiWait.UntilAsync(() => app.ShellPage.WorkspaceNews!.Toasts.Count == 0);
    }

    internal static async Task<ConversationId> AskAsync(DesktopHarness app, ConversationService conversations, ConversationId? existing, WorkspaceId workspace, string question)
    {
        var sent = existing is { } id ? await conversations.AskAsync(id, question, CancellationToken.None) : await conversations.StartAsync(workspace, question, CancellationToken.None);
        await UiWait.UntilAsync(() => conversations.LoadAsync(sent.Conversation, CancellationToken.None).Result!.Messages.All(m => m.IsFinished));
        _ = app;
        return sent.Conversation;
    }

    internal static async Task<ConversationId> BranchAsync(DesktopHarness app, ConversationService conversations, ConversationId parent, string question)
    {
        var source = (await conversations.LoadAsync(parent, CancellationToken.None))!.Messages.Last(m => m.Role == MessageRole.Assistant);
        var branch = await conversations.CreateBranchAsync(parent, new BranchSelection(source.Id, 0, source.Text[..2], "{\"v\":1}", 1, "test-profile"), CancellationToken.None);
        Assert.True(branch.Accepted);
        return await AskAsync(app, conversations, branch.Child, default, question);
    }

    internal static async Task<SummaryNetworkHomeViewModel> OpenSummaryNetworkAsync(DesktopHarness app, MainWindow window)
    {
        app.ShellPage.ShowSectionCommand.Execute(MainSection.SummaryNetwork);
        var network = Assert.IsType<SummaryNetworkHomeViewModel>(app.Workbench.CurrentPage);
        await network.Loaded;
        await UiWait.UntilAsync(() => network.Nodes.Count > 0);
        window.UpdateLayout();
        return network;
    }

    /// <summary>Moves a saved position away so that "arrange" visibly changes the layout.</summary>
    internal static async Task MoveSomethingAsync(DesktopHarness app, SummaryNetworkHomeViewModel network)
    {
        var node = network.Nodes[0];
        await app.Services.GetRequiredService<SqliteDatabase>().WriteAsync(
            (c, t) => c.Execute(t, "UPDATE node_layout SET x = x + 900 WHERE node_id = $id;", ("$id", node.Id.ToString())), CancellationToken.None);
        network.Session.DiscardChanges();
        await network.Session.LoadAsync(CancellationToken.None);
    }
}
