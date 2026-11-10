using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Desktop.ViewModels.Conversations;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// New answers in a workspace that is not open (2026-11-10): the sidebar marks only the open workspace's conversations,
/// so the workspace picker shows where the others are.
/// </summary>
public class WorkspaceNewsTests
{
    [AvaloniaFact]
    public async Task An_answer_that_ends_in_another_workspace_shows_on_the_picker_until_that_workspace_is_open()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        var trains = await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var news = app.ShellPage.WorkspaceNews!;
        var picker = Find<ComboBox>(window, "WorkspacePicker");
        var badge = Find<Border>(window, "OtherWorkspacesNews");

        // An answer is still running in 火车 when the user opens a second workspace.
        var held = app.Client.Hold("半句");
        var sent = await app.Services.GetRequiredService<ConversationService>().StartAsync(trains.Id, "火车是怎么发明的？", CancellationToken.None);
        Assert.Null(sent.Rejection);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        app.ShellPage.NewWorkspace.Name = "高铁";
        await app.ShellPage.NewWorkspace.SubmitCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentWorkspace?.Name.Value == "高铁");
        await news.Loaded;
        window.UpdateLayout();
        Assert.False(badge.IsEffectivelyVisible);
        Assert.Null(ToolTip.GetTip(picker));

        // It ends while 高铁 is open: the closed picker shows a badge with the count, and its tooltip names the workspace.
        held.Finish("的回答。");
        await UiWait.UntilAsync(() => news.HasNews);
        window.UpdateLayout();
        Assert.True(badge.IsEffectivelyVisible);
        Assert.Equal("1", Find<TextBlock>(window, "OtherWorkspacesNewsCount").Text);
        Assert.Equal(UiText.OtherWorkspacesNews(["火车"]), ToolTip.GetTip(picker));
        Assert.Equal(UiText.OtherWorkspacesNews(["火车"]), AutomationProperties.GetHelpText(picker));

        // In the list, the dot is beside 火车 only; the open workspace never has one here.
        picker.IsDropDownOpen = true;
        window.UpdateLayout();
        var entries = Enumerable.Range(0, picker.ItemCount).Select(i => (ComboBoxItem)picker.ContainerFromIndex(i)!).ToList();
        foreach (var entry in entries)
        {
            entry.UpdateLayout();
        }

        bool Dotted(string name) => entries.Single(e => ((Core.Workspaces.Workspace)e.DataContext!).Name.Value == name)
            .GetVisualDescendants().OfType<Ellipse>().Single(x => x.Name == "WorkspaceNewsDot").IsVisible;
        Assert.True(Dotted("火车"));
        Assert.False(Dotted("高铁"));
        var trainsPanel = entries.Single(e => ((Core.Workspaces.Workspace)e.DataContext!).Name.Value == "火车").GetVisualDescendants().OfType<DockPanel>().First();
        Assert.Equal(UiText.WorkspaceWithNews("火车"), AutomationProperties.GetName(trainsPanel));
        picker.IsDropDownOpen = false;

        // Opening 火车: the hint goes; that workspace's own sidebar shows the conversation's mark instead.
        app.ShellPage.SelectedWorkspace = trains;
        await UiWait.UntilAsync(() => app.Workbench.CurrentWorkspace?.Id == trains.Id && !news.HasNews);
        window.UpdateLayout();
        Assert.False(badge.IsEffectivelyVisible);
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.SingleOrDefault(i => i.Id == sent.Conversation)?.Mark == ConversationMark.Unread);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Seeing_the_conversation_clears_the_hint_for_its_workspace()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        var trains = await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var news = app.ShellPage.WorkspaceNews!;
        var held = app.Client.Hold("半句");
        var sent = await app.Services.GetRequiredService<ConversationService>().StartAsync(trains.Id, "火车是怎么发明的？", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        app.ShellPage.NewWorkspace.Name = "高铁";
        await app.ShellPage.NewWorkspace.SubmitCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentWorkspace?.Name.Value == "高铁");
        held.Finish("的回答。");
        await UiWait.UntilAsync(() => news.HasNews);

        // Back in 火车, the conversation is opened (seen); on returning to 高铁 nothing is left to point at.
        app.ShellPage.SelectedWorkspace = trains;
        await UiWait.UntilAsync(() => app.Workbench.CurrentWorkspace?.Id == trains.Id);
        app.Services.GetRequiredService<IConversationMarks>().SetViewing(sent.Conversation);
        var highSpeed = app.Workbench.Workspaces.Single(w => w.Name.Value == "高铁");
        app.ShellPage.SelectedWorkspace = highSpeed;
        await UiWait.UntilAsync(() => app.Workbench.CurrentWorkspace?.Id == highSpeed.Id);
        // Past the picker's 100 ms refresh delay, the refresh for this switch has run.
        await Task.Delay(300);
        await news.Loaded;
        Assert.False(news.HasNews);
        window.Close();
    }
}
