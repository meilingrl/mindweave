using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

public class ViewTests
{
    [Fact]
    public void Every_view_model_shown_through_a_content_host_has_a_registered_view()
    {
        // Window DataContext, overlays and parts rendered inline by their owners' own templates.
        var inline = new[]
        {
            typeof(RootViewModel), typeof(WorkspaceNameForm), typeof(ExitCoordinator), typeof(LayoutPromptViewModel), typeof(WorkspaceDeletionViewModel), typeof(ConversationDeletionViewModel),
            typeof(BackupRestoreViewModel), typeof(BackupDeletionViewModel),
            typeof(ViewModels.Conversations.Composer), typeof(ViewModels.Conversations.MessageItemViewModel),
            typeof(ViewModels.Conversations.ConversationItemViewModel), typeof(ViewModels.Conversations.ConversationListViewModel),
            typeof(ViewModels.Conversations.ConversationSummaryPanel), typeof(ViewModels.Conversations.ConversationLifecycleActions),
            typeof(ViewModels.Conversations.RecommendedQuestionViewModel), typeof(ViewModels.Conversations.ModelConnectionNote),
            typeof(ViewModels.Pages.ModelSettingsViewModel), typeof(ViewModels.Pages.ToolSettingsPanel), typeof(ViewModels.Pages.ArchivedItemViewModel),
            typeof(ViewModels.Conversations.AttachmentChip), typeof(ViewModels.Conversations.AttachmentViewer),
            typeof(ViewModels.Conversations.WebSearchSwitch), typeof(ViewModels.Pages.WebSearchSettingsPanel),
            typeof(ViewModels.Pages.McpServersPanel), typeof(ViewModels.Pages.McpServerItem), typeof(ViewModels.Pages.McpToolItem),
        };
        var hosted = typeof(ViewModelBase).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(ViewModelBase).IsAssignableFrom(t))
            .Except(inline);

        Assert.Empty(hosted.Except(ViewLocator.RegisteredViewModels));
    }

    [AvaloniaFact]
    public async Task Onboarding_works_through_the_real_controls()
    {
        await using var app = DesktopHarness.Create();
        var window = new MainWindow { DataContext = app.Root };
        window.Show();
        await app.StartAsync();
        window.UpdateLayout();

        // The guide (2026-11-17) is walked in OnboardingTests; here it is skipped to the workspace step.
        Invoke(window, "SkipButton");
        window.UpdateLayout();
        var name = Find<TextBox>(window, "NameBox");
        name.Focus();
        window.KeyTextInput("火车历史");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await UiWait.UntilAsync(() => app.Root.Current is ShellViewModel);
        await app.Workbench.LastLocationSave;
        window.UpdateLayout();

        Assert.IsType<ShellView>(window.GetVisualDescendants().OfType<ShellView>().Single());
        Assert.IsType<Views.Conversations.NewConversationView>(Find<ContentControl>(window, "PageHost").GetVisualDescendants().OfType<Views.Conversations.NewConversationView>().Single());
        Screenshots.Capture(window, "01-new-exploration");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Menu_and_content_navigation_through_the_real_controls()
    {
        await using var app = DesktopHarness.Create();
        var window = new MainWindow { DataContext = app.Root };
        window.Show();
        await app.StartWithWorkspaceAsync("火车历史");
        window.UpdateLayout();

        Invoke(window, "NavSettings");
        Invoke(window, "StorageDetailsButton");
        Assert.Single(window.GetVisualDescendants().OfType<StorageDetailsView>());
        Assert.True(Find<Button>(window, "BackButton").IsVisible);
        Screenshots.Capture(window, "02-storage-details");

        Invoke(window, "BackButton");
        Assert.Single(window.GetVisualDescendants().OfType<SettingsHomeView>());
        Assert.False(Find<Button>(window, "BackButton").IsVisible);

        Invoke(window, "NavSummaryNetwork");
        Assert.Single(window.GetVisualDescendants().OfType<SummaryNetworkHomeView>());
        Assert.Equal(MainSection.SummaryNetwork, app.Workbench.CurrentSection);
        window.Close();
    }

    /// <summary>Runs the named button's command directly, without a pointer click.</summary>
    private static void Invoke(Window window, string name)
    {
        var button = Find<Button>(window, name);
        Assert.True(button.IsEffectivelyEnabled, $"{name} is disabled");
        button.Command!.Execute(button.CommandParameter);
        window.UpdateLayout();
    }
}
