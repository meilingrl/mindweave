using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Mindweave.Desktop;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Mindweave.Tests.TestAppBuilder))]

namespace Mindweave.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>().UseHeadless(new());
}
public sealed class TestApplication : Avalonia.Application
{
    public override void Initialize()
    {
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        DataTemplates.Add(new ViewLocator());
    }
}

public sealed class DesktopTests
{
    [AvaloniaFact]
    public async Task Product_window_closes_after_draining_store_and_rejects_new_writes()
    {
        using var dir = new TestDirectory();
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        var store = await SqliteWorkspaceStore.OpenAsync(paths);
        var root = new RootViewModel(() => Task.FromResult<Mindweave.Application.Storage.IWorkspaceStore>(store),
            new DesktopActions(paths.DataDirectory, () => { }), paths.DataDirectory);
        await root.InitializeAsync();
        var window = new Mindweave.Desktop.Views.MainWindow { DataContext = root };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(window.IsVisible);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.CreateAsync("不能写入"));
    }

    [AvaloniaFact]
    public async Task Actual_workbench_view_binds_creation_and_navigation_commands()
    {
        using var dir = new TestDirectory();
        await using var store = await SqliteWorkspaceStore.OpenAsync(AppPaths.ForTrack("codex-steady", dir.Path));
        var main = CompositionRoot.CreateMain(store);
        await main.InitializeAsync();
        var view = Assert.IsAssignableFrom<Control>(new ViewLocator().Build(main));
        view.DataContext = main;
        var window = new Window { Content = view, Width = 1100, Height = 720 };
        try
        {
            window.Show();
            var input = view.FindControl<TextBox>("WorkspaceNameInput")!;
            input.Text = "真实绑定的主题";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var create = view.FindControl<Button>("CreateWorkspaceButton")!;
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)create.Command!).ExecuteAsync(null);
            Assert.Equal("真实绑定的主题", main.CurrentWorkspace!.Name);
            var network = view.FindControl<Button>("NetworkButton")!;
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)network.Command!).ExecuteAsync(null);
            Assert.Equal(Mindweave.Application.Navigation.MainArea.Network, main.ActiveContent!.Request.Area);
            Assert.IsAssignableFrom<Control>(new ViewLocator().Build(main.ActiveContent));
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task Corrupt_database_routes_to_recovery_and_retry_preserves_original_file()
    {
        using var dir = new TestDirectory();
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        Directory.CreateDirectory(paths.DataDirectory);
        byte[] original = "corrupt data"u8.ToArray();
        await File.WriteAllBytesAsync(paths.DatabasePath, original);
        var root = new RootViewModel(async () => await SqliteWorkspaceStore.OpenAsync(paths),
            new DesktopActions(paths.DataDirectory, () => { }), paths.DataDirectory);
        await root.InitializeAsync();
        var recovery = Assert.IsType<StartupViewModel>(root.Content);
        Assert.True(recovery.HasError);
        Assert.Equal(original, await File.ReadAllBytesAsync(paths.DatabasePath));
        // A user-controlled repair is represented by preserving the damaged file before retry.
        File.Move(paths.DatabasePath, paths.DatabasePath + ".preserved");
        await recovery.RetryCommand.ExecuteAsync(null);
        Assert.IsType<MainViewModel>(root.Content);
        Assert.Equal(original, await File.ReadAllBytesAsync(paths.DatabasePath + ".preserved"));
        await root.CloseAsync();
    }
}
