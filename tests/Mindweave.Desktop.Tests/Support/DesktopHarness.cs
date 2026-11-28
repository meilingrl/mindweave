using Avalonia.Styling;
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
using Mindweave.Desktop.Views;
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
            services.AddSingleton<Application.Tools.ISearchProvider>(Search);
            services.AddSingleton<Application.Tools.IWebPageFetcher>(Pages);
            services.AddSingleton(new GenerationOptions { CheckpointInterval = TimeSpan.FromMilliseconds(50) });
            services.AddSingleton(new Application.Storage.StorageHealthOptions { RetryInterval = TimeSpan.FromMilliseconds(50) });

            // Tests share one Avalonia application: an appearance choice is recorded, never applied to it.
            services.AddSingleton<IThemeApplier>(Theme);

            // Never the real system beep in tests: the alerts are counted.
            services.AddSingleton<Application.Notifications.IAlertSound>(Sound);

            // The background names' 30 s wait (2026-11-11) stands still unless a test moves this clock.
            services.AddSingleton(new Application.Workspaces.WorkspaceNamePreparationOptions { Time = PreparationTime });

            // Recommendations prepared on opening 新探索 (2026-11-12) wait on the same standing clock.
            services.AddSingleton(new Application.Recommendations.RecommendationOptions { Time = PreparationTime });
            overrides?.Invoke(services);
        };
        _ownsDirectory = ownsDirectory;
        Session = NewSession();
    }

    public FakeChatClient Client { get; } = new();

    /// <summary>The clock the background names wait on: standing still, so no test sends them unless it moves it.</summary>
    public ManualTime PreparationTime { get; } = new();

    public MemoryCredentialStore Credentials { get; } = new();

    /// <summary>The search service (phase 3): scripted, no network.</summary>
    public FakeSearchProvider Search { get; } = new();

    /// <summary>The page reader (phase 3): a table of pages, no network.</summary>
    public FakePageFetcher Pages { get; } = new();

    public RecordingThemeApplier Theme { get; } = new();

    public RecordingAlertSound Sound { get; } = new();

    public async Task ConfigureModelAsync() =>
        await Services.GetRequiredService<ModelSettingsService>().SaveAsync(new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test"), CancellationToken.None);

    public AppPaths Paths { get; }

    public RecordingShell Shell { get; } = new();

    /// <summary>The application session: an in-app restore (2026-11-12) replaces its services, and with them <see cref="Root"/>.</summary>
    internal AppSession Session { get; private set; }

    /// <summary>The current view provider; it holds every root service too, as the root's own instance.</summary>
    public ServiceProvider Services => Session.View;

    public RootViewModel Root => Services.GetRequiredService<RootViewModel>();

    public Workbench Workbench => Services.GetRequiredService<Workbench>();

    public ShellViewModel ShellPage => Services.GetRequiredService<ShellViewModel>();

    /// <summary>Opens the delete dialog for the workspace shown, as its row on 设置 › 工作区 does.</summary>
    public void AskToDeleteWorkspace() => Services.GetRequiredService<WorkspaceDeletionRequests>().Open(Workbench.CurrentWorkspace!);

    public static DesktopHarness Create(Action<IServiceCollection>? overrides = null, string? directory = null) =>
        new(directory ?? Path.Combine(Path.GetTempPath(), "mindweave-desktop-tests", Guid.NewGuid().ToString("N")), overrides, directory is null);

    public Task StartAsync() => Services.GetRequiredService<AppStartup>().RunAsync();

    /// <summary>
    /// Shows the main window over <see cref="Root"/> at the given size, optionally in <paramref name="theme"/> (set on
    /// the window only: the tests share one application).
    /// </summary>
    public MainWindow ShowWindow(double width = 1000, double height = 700, ThemeVariant? theme = null)
    {
        var window = new MainWindow { DataContext = Root, Width = width, Height = height };
        window.Follow(Session);
        if (theme is not null)
        {
            window.RequestedThemeVariant = theme;
        }

        window.Show();
        return window;
    }

    /// <summary>
    /// Starts, creates a first workspace through onboarding, and lands in the workbench on the workspace's home. The guide
    /// itself lands in 新探索 (2026-11-17, covered by OnboardingTests); most tests start from the home, so this steps back.
    /// </summary>
    public async Task<Workspace> StartWithWorkspaceAsync(string name)
    {
        await StartAsync();
        var onboarding = Assert.IsType<OnboardingViewModel>(Root.Current);
        onboarding.Form.Name = name;
        await onboarding.Form.SubmitCommand.ExecuteAsync(null);
        Assert.IsType<ShellViewModel>(Root.Current);
        Assert.IsType<ViewModels.Conversations.NewConversationViewModel>(Workbench.CurrentPage);
        ShellPage.GoBackCommand.Execute(null);
        Assert.IsType<ViewModels.Pages.ExploreHomeViewModel>(Workbench.CurrentPage);
        return Workbench.CurrentWorkspace!;
    }

    /// <summary>Simulates quitting and launching again on the same data directory.</summary>
    public async Task RestartAsync()
    {
        await Root.Exit.Running;
        await Workbench.LastLocationSave;
        await EndSessionAsync();
        Session = NewSession();
        await StartAsync();
    }

    /// <summary>Hands the services to the real <see cref="AppHost"/>, which then tears them down instead of the harness.</summary>
    internal AppHost HandOverToHost(SingleInstance instance)
    {
        _handedOver = true;
        return new AppHost(instance, Session);
    }

    private AppSession NewSession() =>
        new(
            extra => CompositionRoot.BuildRoot(Paths, Shell, NullLoggerProvider.Instance, services =>
            {
                _overrides!(services);
                extra(services);
            }),
            root => CompositionRoot.BuildView(root, _overrides));

    /// <summary>Ends the session as the application does: generation stops (no time limit here), everything is saved by the exit's list, the database closes.</summary>
    private Task EndSessionAsync() => AppSession.EndAsync(Session.Root, Session.View, Timeout.InfiniteTimeSpan);

    public async ValueTask DisposeAsync()
    {
        if (!_handedOver)
        {
            // A test that closed its window started the exit sequence; let it finish before tearing down under it.
            await Root.Exit.Running;
            await EndSessionAsync();
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

    /// <summary>Counts the alert sounds the application played (2026-11-10).</summary>
    public sealed class RecordingAlertSound : Application.Notifications.IAlertSound
    {
        private int _played;

        public int Played => Volatile.Read(ref _played);

        public void Play() => Interlocked.Increment(ref _played);
    }

    /// <summary>The appearances the application asked for, in order.</summary>
    public sealed class RecordingThemeApplier : IThemeApplier
    {
        public List<Application.Preferences.Appearance> Applied { get; } = [];

        public void Apply(Application.Preferences.Appearance appearance) => Applied.Add(appearance);

        /// <summary>The bold colours the application asked for, in order.</summary>
        public List<Application.Preferences.BoldColour> BoldColours { get; } = [];

        public void ApplyBoldColour(Application.Preferences.BoldColour colour) => BoldColours.Add(colour);
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

        /// <summary>Suggested file names the save dialog of a share was opened with.</summary>
        public List<string> ShareRequests { get; } = [];

        /// <summary>What that dialog returns for a suggested name; null (the default) is "cancelled".</summary>
        public Func<string, ExportDestination?> NextShare { get; set; } = _ => null;

        public Task<ExportDestination?> PickShareFileAsync(string suggestedFileName, CancellationToken cancellationToken)
        {
            ShareRequests.Add(suggestedFileName);
            return Task.FromResult(NextShare(suggestedFileName));
        }

        /// <summary>Suggested file names the save dialog of a manual backup was opened with (without the extension).</summary>
        public List<string> BackupRequests { get; } = [];

        /// <summary>What that dialog returns for a suggested name; null (the default) is "cancelled".</summary>
        public Func<string, ExportDestination?> NextBackup { get; set; } = _ => null;

        public Task<ExportDestination?> PickBackupFileAsync(string suggestedFileName, CancellationToken cancellationToken)
        {
            BackupRequests.Add(suggestedFileName);
            return Task.FromResult(NextBackup(suggestedFileName));
        }

        /// <summary>Suggested file names the save dialog of a diagnostic package was opened with (without the extension).</summary>
        public List<string> DiagnosticRequests { get; } = [];

        /// <summary>What that dialog returns for a suggested name; null (the default) is "cancelled".</summary>
        public Func<string, ExportDestination?> NextDiagnostic { get; set; } = _ => null;

        public Task<ExportDestination?> PickDiagnosticFileAsync(string suggestedFileName, CancellationToken cancellationToken)
        {
            DiagnosticRequests.Add(suggestedFileName);
            return Task.FromResult(NextDiagnostic(suggestedFileName));
        }

        /// <summary>The path the open dialog for a shared file returns; null (the default) is "cancelled".</summary>
        public Func<string?> NextSharedFile { get; set; } = () => null;

        public Task<string?> PickSharedFileAsync(CancellationToken cancellationToken) => Task.FromResult(NextSharedFile());
    }
}
