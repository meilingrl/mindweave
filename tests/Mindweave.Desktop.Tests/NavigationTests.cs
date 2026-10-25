using Avalonia.Headless.XUnit;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>The course navigation path: Root → Menu → Content, and per-workspace runtime history.</summary>
public class NavigationTests
{
    [AvaloniaFact]
    public async Task First_run_asks_for_a_workspace_then_opens_its_explore_home()
    {
        await using var app = DesktopHarness.Create();
        await app.StartAsync();

        var onboarding = Assert.IsType<OnboardingViewModel>(app.Root.Current);
        Assert.False(onboarding.Form.SubmitCommand.CanExecute(null));
        onboarding.Form.Name = "   ";
        Assert.False(onboarding.Form.SubmitCommand.CanExecute(null));
        onboarding.Form.Name = "  火车   历史 ";
        await onboarding.Form.SubmitCommand.ExecuteAsync(null);

        Assert.Same(app.ShellPage, app.Root.Current);
        var home = Assert.IsType<ExploreHomeViewModel>(app.ShellPage.CurrentPage);
        Assert.Equal("火车 历史", home.Title);
        Assert.False(app.ShellPage.GoBackCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Menu_switches_keep_each_sections_own_content_history()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("A");
        var shell = app.ShellPage;

        shell.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(shell.CurrentPage);
        settings.ShowStorageDetailsCommand.Execute(null);
        Assert.IsType<StorageDetailsViewModel>(shell.CurrentPage);
        Assert.True(shell.GoBackCommand.CanExecute(null));

        shell.ShowSectionCommand.Execute(MainSection.Explore);
        Assert.IsType<ExploreHomeViewModel>(shell.CurrentPage);
        Assert.False(shell.GoBackCommand.CanExecute(null));

        shell.ShowSectionCommand.Execute(MainSection.Settings);
        Assert.IsType<StorageDetailsViewModel>(shell.CurrentPage);

        shell.GoBackCommand.Execute(null);
        Assert.Same(settings, shell.CurrentPage);
        Assert.False(shell.GoBackCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Pushing_the_page_already_on_top_does_not_grow_history()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("A");

        app.Workbench.Navigate(new NavigationRequest(MainSection.Settings, StorageDetailsRoute.Instance));
        app.Workbench.Push(StorageDetailsRoute.Instance);
        app.Workbench.GoBack();

        Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        Assert.False(app.Workbench.CanGoBack);
    }

    [AvaloniaFact]
    public async Task Each_workspace_keeps_its_own_section_and_history_while_switching()
    {
        await using var app = DesktopHarness.Create();
        var first = await app.StartWithWorkspaceAsync("A");
        var shell = app.ShellPage;
        shell.ShowSectionCommand.Execute(MainSection.Settings);
        app.Workbench.Push(StorageDetailsRoute.Instance);

        shell.ToggleCreateWorkspaceCommand.Execute(null);
        shell.NewWorkspace.Name = "B";
        await shell.NewWorkspace.SubmitCommand.ExecuteAsync(null);
        var second = app.Workbench.CurrentWorkspace!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.False(shell.IsCreatingWorkspace);
        Assert.True(shell.IsExplore);
        shell.ShowSectionCommand.Execute(MainSection.Network);

        shell.SelectedWorkspace = first;
        Assert.True(shell.IsSettings);
        Assert.IsType<StorageDetailsViewModel>(shell.CurrentPage);

        shell.SelectedWorkspace = second;
        Assert.True(shell.IsNetwork);
        Assert.Equal(second, Assert.IsType<NetworkHomeViewModel>(shell.CurrentPage).Workspace);
    }

    [AvaloniaFact]
    public async Task Restart_restores_the_last_workspace_and_section_but_not_back_history()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("A");
        var shell = app.ShellPage;
        shell.ToggleCreateWorkspaceCommand.Execute(null);
        shell.NewWorkspace.Name = "B";
        await shell.NewWorkspace.SubmitCommand.ExecuteAsync(null);
        shell.ShowSectionCommand.Execute(MainSection.Settings);
        app.Workbench.Push(StorageDetailsRoute.Instance);

        await app.RestartAsync();

        Assert.IsType<ShellViewModel>(app.Root.Current);
        Assert.Equal(["A", "B"], app.Workbench.Workspaces.Select(w => w.Name.Value));
        Assert.Equal("B", app.Workbench.CurrentWorkspace!.Name.Value);
        Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        Assert.False(app.Workbench.CanGoBack);
    }

    [AvaloniaFact]
    public async Task Invalid_new_workspace_name_is_reported_and_kept()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("A");
        var form = app.ShellPage.NewWorkspace;

        form.Name = new string('长', WorkspaceName.MaxLength + 1);
        await form.SubmitCommand.ExecuteAsync(null);

        Assert.NotNull(form.Error);
        Assert.Equal(WorkspaceName.MaxLength + 1, form.Name.Length);
        Assert.Single(app.Workbench.Workspaces);
    }
}
