using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Preferences;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using Mindweave.Testing;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Names prepared in the background (user decision of 2026-11-11) in the real application: titles in any workspace,
/// not only the open one, start the wait; the names are then offered when that workspace is renamed. 设置 › 提示 has the
/// switch, on by default; off sends nothing in the background.
/// </summary>
public class WorkspaceNamePreparationUiTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [AvaloniaFact]
    public async Task Titles_in_a_workspace_that_is_not_open_prepare_its_names_and_its_rename_offers_them()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SummaryNetworkScenario.SeedAsync(app);
        var preparer = app.Services.GetRequiredService<WorkspaceNamePreparer>();
        var (city, _) = await WorkspaceSwitcherTests.AddAsync(app, "城市交通", "摄影史");
        Assert.NotEqual(city.Id, app.Workbench.CurrentWorkspace!.Id);

        await TitledAsync(app, city.Id, "第一条地铁在哪里？", "公交车是谁发明的？", "有轨电车还有吗？");
        Assert.True(preparer.IsWaiting(city.Id));
        Assert.DoesNotContain(app.Client.Requests, FakeChatClient.IsWorkspaceNaming);

        app.PreparationTime.Advance(Wait);
        await preparer.SettledAsync();
        Assert.Contains(app.Client.Requests, r => FakeChatClient.IsWorkspaceNaming(r) && r.Turns[1].Content.Contains("当前名称：城市交通", StringComparison.Ordinal));

        // Renaming 城市交通 offers them at once, without asking again.
        var sent = app.Client.Requests.Count(FakeChatClient.IsWorkspaceNaming);
        app.ShellPage.ManageWorkspacesCommand.Execute(null);
        var page = Assert.IsType<WorkspacesViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        var row = page.Rows.Single(r => r.Workspace.Id == city.Id);
        row.BeginRenameCommand.Execute(null);
        await page.KeptNamesRead;
        window.UpdateLayout();
        var box = window.GetVisualDescendants().OfType<WorkspacesView>().Single().GetVisualDescendants().OfType<GhostTextBox>()
            .Single(b => ReferenceEquals(b.DataContext, row));
        await UiWait.UntilAsync(() => box.IsFocused);
        Assert.Equal("蒸汽机车史", box.Offered);
        Assert.Equal(sent, app.Client.Requests.Count(FakeChatClient.IsWorkspaceNaming));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_switch_is_on_by_default_and_off_sends_nothing_in_the_background_even_after_a_restart()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        window.UpdateLayout();

        var toggle = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "PrepareNamesToggle");
        Assert.True(toggle.IsEffectivelyVisible);
        Assert.True(toggle.IsChecked);
        Assert.Equal(UiText.PrepareNames, AutomationProperties.GetName(toggle));
        Assert.Equal(UiText.PrepareNamesHint, AutomationProperties.GetHelpText(toggle));

        SettingsInput.Click(window, toggle);
        await settings.Saving;
        Assert.False(settings.PreparesNames);
        Assert.False(await app.Services.GetRequiredService<INewsAlerts>().GetPrepareNamesAsync(CancellationToken.None));
        window.Close();

        await app.RestartAsync();
        Assert.False(await app.Services.GetRequiredService<INewsAlerts>().GetPrepareNamesAsync(CancellationToken.None));
        var preparer = app.Services.GetRequiredService<WorkspaceNamePreparer>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        await TitledAsync(app, workspace, "火车是怎么发明的？", "铁轨用什么做？", "第一条铁路在哪里？");
        Assert.False(preparer.IsWaiting(workspace));
        app.PreparationTime.Advance(Wait * 2);
        await preparer.SettledAsync();
        Assert.DoesNotContain(app.Client.Requests, FakeChatClient.IsWorkspaceNaming);
    }

    /// <summary>New conversations in <paramref name="workspace"/>, each with its automatic title stored and looked at.</summary>
    private static async Task TitledAsync(DesktopHarness app, WorkspaceId workspace, params string[] questions)
    {
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var preparer = app.Services.GetRequiredService<WorkspaceNamePreparer>();
        foreach (var question in questions)
        {
            var sent = await conversations.StartAsync(workspace, question, CancellationToken.None);
            Assert.True(sent.Accepted, sent.Rejection?.ToString());
            await UiWait.UntilAsync(() => conversations.LoadAsync(sent.Conversation, CancellationToken.None).Result is { Conversation.Title: not null } c
                && c.Messages.All(m => m.IsFinished));
            await UiWait.UntilAsync(() => !app.Services.GetRequiredService<GenerationCoordinator>().HasUnfinishedAnswer(sent.Conversation)
                && app.Services.GetRequiredService<GenerationCoordinator>().UnfinishedCount == 0);
            await preparer.SettledAsync();
        }
    }
}
