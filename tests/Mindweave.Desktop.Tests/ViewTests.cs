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
            typeof(ViewModels.Conversations.Composer), typeof(ViewModels.Conversations.MessageItemViewModel),
            typeof(ViewModels.Conversations.ConversationItemViewModel), typeof(ViewModels.Conversations.ConversationListViewModel),
            typeof(ViewModels.Pages.ModelSettingsViewModel), typeof(ViewModels.Pages.ArchivedItemViewModel),
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

        var name = Find<TextBox>(window, "NameBox");
        name.Focus();
        window.KeyTextInput("火车历史");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await UiWait.UntilAsync(() => app.Root.Current is ShellViewModel);
        await app.Workbench.LastLocationSave;
        window.UpdateLayout();

        Assert.IsType<ShellView>(window.GetVisualDescendants().OfType<ShellView>().Single());
        Assert.IsType<ExploreHomeView>(Find<ContentControl>(window, "PageHost").GetVisualDescendants().OfType<ExploreHomeView>().Single());
        Capture(window, "01-explore-home");
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
        Capture(window, "02-storage-details");

        Invoke(window, "BackButton");
        Assert.Single(window.GetVisualDescendants().OfType<SettingsHomeView>());
        Assert.False(Find<Button>(window, "BackButton").IsVisible);

        Invoke(window, "NavNetwork");
        Assert.Single(window.GetVisualDescendants().OfType<NetworkHomeView>());
        Assert.Equal(MainSection.Network, app.Workbench.CurrentSection);
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

    /// <summary>Writes a rendered frame when MINDWEAVE_SCREENSHOT_DIR is set, for visual review.</summary>
    internal static void Capture(Window window, string name)
    {
        var target = Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        Directory.CreateDirectory(target);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(target, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
