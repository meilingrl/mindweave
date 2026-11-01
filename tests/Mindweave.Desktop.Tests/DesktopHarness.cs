using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Credentials;
using Mindweave.Application.Export;
using Mindweave.Application.Models;
using Mindweave.Application.Reading;
using Mindweave.Core.Workspaces;
using Mindweave.Testing;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Desktop.Tests;

/// <summary>The real composition root over a temporary data directory, with a recording system shell.</summary>
public sealed class DesktopHarness : IAsyncDisposable
{
    private readonly Action<IServiceCollection>? _overrides;
    private readonly bool _ownsDirectory;
    private bool _handedOver;

    private DesktopHarness(string directory, Action<IServiceCollection>? overrides, bool ownsDirectory)
    {
        Paths = new AppPaths("test", directory);
        _overrides = services =>
        {
            // No network and no real keychain in UI tests; fast checkpoints.
            services.AddSingleton<IChatClient>(Client);
            services.AddSingleton<ICredentialStore>(Credentials);
            services.AddSingleton(new GenerationOptions { CheckpointInterval = TimeSpan.FromMilliseconds(50) });
            services.AddSingleton(new Application.Storage.StorageHealthOptions { RetryInterval = TimeSpan.FromMilliseconds(50) });

            // Tests share one Avalonia application: an appearance choice is recorded, never applied to it.
            services.AddSingleton<IThemeApplier>(Theme);
            overrides?.Invoke(services);
        };
        _ownsDirectory = ownsDirectory;
        Services = CompositionRoot.Build(Paths, Shell, NullLoggerProvider.Instance, _overrides);
    }

    public FakeChatClient Client { get; } = new();

    public MemoryCredentialStore Credentials { get; } = new();

    public RecordingThemeApplier Theme { get; } = new();

    public async Task ConfigureModelAsync() =>
        await Services.GetRequiredService<ModelSettingsService>().SaveAsync(new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test"), CancellationToken.None);

    public AppPaths Paths { get; }

    public RecordingShell Shell { get; } = new();

    public ServiceProvider Services { get; private set; }

    public RootViewModel Root => Services.GetRequiredService<RootViewModel>();

    public Workbench Workbench => Services.GetRequiredService<Workbench>();

    public ShellViewModel ShellPage => Services.GetRequiredService<ShellViewModel>();

    public static DesktopHarness Create(Action<IServiceCollection>? overrides = null, string? directory = null) =>
        new(directory ?? Path.Combine(Path.GetTempPath(), "mindweave-desktop-tests", Guid.NewGuid().ToString("N")), overrides, directory is null);

    public Task StartAsync() => Services.GetRequiredService<AppStartup>().RunAsync();

    /// <summary>Starts, creates a first workspace through onboarding, and lands in the workbench.</summary>
    public async Task<Workspace> StartWithWorkspaceAsync(string name)
    {
        await StartAsync();
        var onboarding = Assert.IsType<OnboardingViewModel>(Root.Current);
        onboarding.Form.Name = name;
        await onboarding.Form.SubmitCommand.ExecuteAsync(null);
        Assert.IsType<ShellViewModel>(Root.Current);
        return Workbench.CurrentWorkspace!;
    }

    /// <summary>Simulates quitting and launching again on the same data directory.</summary>
    public async Task RestartAsync()
    {
        await Root.Exit.Running;
        await Workbench.LastLocationSave;
        await Services.GetRequiredService<GenerationCoordinator>().ShutdownAsync();
        await Services.GetRequiredService<ReadingStateService>().FlushAsync();
        await Services.GetRequiredService<SqliteDatabase>().CloseAsync();
        await Services.DisposeAsync();
        Services = CompositionRoot.Build(Paths, Shell, NullLoggerProvider.Instance, _overrides);
        await StartAsync();
    }

    /// <summary>Hands the services to the real <see cref="AppHost"/>, which then tears them down instead of the harness.</summary>
    internal AppHost HandOverToHost(SingleInstance instance)
    {
        _handedOver = true;
        return new AppHost(instance, Services);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_handedOver)
        {
            // A test that closed its window started the exit sequence; let it finish before tearing down under it.
            await Root.Exit.Running;
            await Services.GetRequiredService<GenerationCoordinator>().ShutdownAsync();
            await Services.GetRequiredService<ReadingStateService>().FlushAsync();
            await Services.GetRequiredService<SqliteDatabase>().CloseAsync();
            await Services.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        if (_ownsDirectory)
        {
            try
            {
                Directory.Delete(Paths.DataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The appearances the application asked for, in order.</summary>
    public sealed class RecordingThemeApplier : IThemeApplier
    {
        public List<Application.Preferences.Appearance> Applied { get; } = [];

        public void Apply(Application.Preferences.Appearance appearance) => Applied.Add(appearance);
    }

    public sealed class RecordingShell : ISystemShell
    {
        public List<string> OpenedFolders { get; } = [];

        public int ExitRequests { get; private set; }

        public void OpenFolder(string path) => OpenedFolders.Add(path);

        public void ExitApplication() => ExitRequests++;

        /// <summary>Suggested file names the save dialog was opened with.</summary>
        public List<string> ExportRequests { get; } = [];

        /// <summary>What the save dialog returns for a suggested name; null (the default) is "cancelled".</summary>
        public Func<string, ExportDestination?> NextExport { get; set; } = _ => null;

        public Task<ExportDestination?> PickExportFileAsync(string suggestedFileName, CancellationToken cancellationToken)
        {
            ExportRequests.Add(suggestedFileName);
            return Task.FromResult(NextExport(suggestedFileName));
        }
    }
}
