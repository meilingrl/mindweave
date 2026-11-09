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
internal static class NetworkScenario
{
    /// <summary>Root "火车" summarized; a branch without a summary; its branch summarized; a sibling summarized.</summary>
    internal static async Task SeedAsync(DesktopHarness app)
    {
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var root = await AskAsync(app, conversations, null, workspace, "火车是怎么发明的？");
        var middle = await BranchAsync(app, conversations, root, "谁造了第一台？");
        var leaf = await BranchAsync(app, conversations, middle, "它跑多快？");
        var sibling = await BranchAsync(app, conversations, root, "铁轨用什么做？");
        var summaries = app.Services.GetRequiredService<SummaryService>();
        foreach (var (conversation, title) in new[] { (root, "火车起源"), (leaf, "火箭号速度"), (sibling, "铁轨") })
        {
            app.Client.SummaryReplies.Enqueue($"标题：{title}的详细说明\n短名：{title}\n\n正文");
            var outcome = await summaries.SummarizeAsync(conversation, CancellationToken.None);
            Assert.True(outcome.Accepted, outcome.Rejection?.ToString());
            await UiWait.UntilAsync(() => summaries.LatestTaskAsync(conversation, CancellationToken.None).Result is { State: GenerationTaskState.Completed });
        }
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

    internal static async Task<NetworkHomeViewModel> OpenNetworkAsync(DesktopHarness app, MainWindow window)
    {
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Network);
        var network = Assert.IsType<NetworkHomeViewModel>(app.Workbench.CurrentPage);
        await network.Loaded;
        await UiWait.UntilAsync(() => network.Nodes.Count > 0);
        window.UpdateLayout();
        return network;
    }

    /// <summary>Moves a saved position away so that "arrange" visibly changes the layout.</summary>
    internal static async Task MoveSomethingAsync(DesktopHarness app, NetworkHomeViewModel network)
    {
        var node = network.Nodes[0];
        await app.Services.GetRequiredService<SqliteDatabase>().WriteAsync(
            (c, t) => c.Execute(t, "UPDATE node_layout SET x = x + 900 WHERE node_id = $id;", ("$id", node.Id.ToString())), CancellationToken.None);
        network.Session.DiscardChanges();
        await network.Session.LoadAsync(CancellationToken.None);
    }
}
