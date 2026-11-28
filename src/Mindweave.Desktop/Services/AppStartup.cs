using Microsoft.Extensions.Logging;
using Mindweave.Application.Conversations;
using Mindweave.Application.Preferences;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Services;

/// <summary>Drives root navigation from the startup outcome: recovery page, onboarding, or the workbench.</summary>
public sealed partial class AppStartup : IDisposable
{
    private readonly StartupCoordinator _coordinator;
    private readonly WorkspaceService _workspaces;
    private readonly IRootNavigator _root;
    private readonly Workbench _workbench;
    private readonly ShellViewModel _shell;
    private readonly ISystemShell _system;
    private readonly ExitCoordinator _exit;
    private readonly AppearanceService _appearance;
    private readonly IThemeApplier _theme;
    private readonly StorageHealth _health;
    private readonly ConversationMarksService _marks;
    private readonly WorkspaceDeletionViewModel _workspaceDeletion;
    private readonly Infrastructure.Hosting.AppPaths _paths;
    private readonly IOnboardingProgress _onboarding;
    private readonly Application.Models.IModelSettings _models;
    private readonly LanguageSwitch _language;
    private readonly SummaryNetwork.SummaryNetworkDetails _summaryNetworkDetails;
    private readonly TextWidth _textWidth;
    private readonly Application.AppEnvironment _environment;
    private readonly DiagnosticPackageViewModel _diagnostics;
    private readonly ILogger<AppStartup> _logger;

    public AppStartup(
        StartupCoordinator coordinator,
        WorkspaceService workspaces,
        IRootNavigator root,
        Workbench workbench,
        ShellViewModel shell,
        ISystemShell system,
        ExitCoordinator exit,
        AppearanceService appearance,
        IThemeApplier theme,
        StorageHealth health,
        WorkspaceDeletionViewModel workspaceDeletion,
        ConversationMarksService marks,
        Application.Workspaces.WorkspaceNamePreparer namePreparer,
        Infrastructure.Hosting.AppPaths paths,
        IOnboardingProgress onboarding,
        Application.Models.IModelSettings models,
        LanguageSwitch language,
        SummaryNetwork.SummaryNetworkDetails summaryNetworkDetails,
        TextWidth textWidth,
        Application.AppEnvironment environment,
        DiagnosticPackageViewModel diagnostics,
        ILogger<AppStartup> logger)
    {
        _environment = environment;
        _diagnostics = diagnostics;
        _language = language;
        _summaryNetworkDetails = summaryNetworkDetails;
        _textWidth = textWidth;
        _paths = paths;
        _onboarding = onboarding;
        _models = models;
        _logger = logger;
        // Composed here so it listens for titles from the start (2026-11-11); it acts on its own from then on.
        ArgumentNullException.ThrowIfNull(namePreparer);

        _coordinator = coordinator;
        _workspaces = workspaces;
        _root = root;
        _workbench = workbench;
        _shell = shell;
        _system = system;
        _exit = exit;
        _appearance = appearance;
        _theme = theme;
        _health = health;
        _marks = marks;

        ArgumentNullException.ThrowIfNull(workspaceDeletion);
        _workspaceDeletion = workspaceDeletion;
        workspaceDeletion.NoWorkspaceLeft += OnNoWorkspaceLeft;
    }

    /// <summary>Stops listening to the delete dialog; the view it belongs to is being replaced.</summary>
    public void Dispose() => _workspaceDeletion.NoWorkspaceLeft -= OnNoWorkspaceLeft;

    /// <summary>
    /// The last workspace was deleted: a new one is asked for, as on a fresh start. A workspace existed, so the guide was
    /// finished (or predates it) and is not shown again.
    /// </summary>
    private void OnNoWorkspaceLeft(object? sender, EventArgs e) => _root.Show(new OnboardingViewModel(CreateFirstWorkspaceAsync));

    /// <summary>
    /// The guide from <paramref name="step"/>, or with <see cref="OnboardingStep.Done"/> only the workspace step. The
    /// guide's first step offers the language choice.
    /// </summary>
    private OnboardingViewModel Onboarding(OnboardingStep step) =>
        new(CreateFirstWorkspaceAsync, step, _onboarding, new ModelSettingsViewModel(_models), _language);

    /// <summary>
    /// Where the user is, for a language switch (bilingual-ui.md §6.4): what the window shows, the workbench's pages
    /// and the sidebar. <see cref="Resume"/> on the next view's startup replays it.
    /// </summary>
    public ViewSnapshot Snapshot()
    {
        RootPage root = _root.Current switch
        {
            ShellViewModel => new RootPage.Shell(),
            OnboardingViewModel guide => new RootPage.Onboarding(guide.IsGuided ? guide.Step : OnboardingStep.Done, guide.Form.Name, guide.Model?.CaptureViewState(), guide.LanguageChosen),
            RecoveryViewModel recovery => new RootPage.Recovery(recovery.Result, [.. recovery.Backups]),
            _ => new RootPage.Starting(),
        };
        return _workbench.Snapshot() with { Root = root, Sidebar = _shell.Sidebar };
    }

    /// <summary>
    /// The new view's start after a language switch: it shows what <paramref name="snapshot"/> showed, with the pages,
    /// input and sidebar it holds. Nothing is read from storage, nothing is marked interrupted: the services, running
    /// answers among them, are the ones that ran before, and the appearance, unread marks and storage health are theirs.
    /// </summary>
    public void Resume(ViewSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Sidebar is { } sidebar)
        {
            _shell.UseSidebar(sidebar);
        }

        switch (snapshot.Root)
        {
            case RootPage.Shell when snapshot.Workspaces.Count > 0:
                _workbench.Restore(snapshot);
                _root.Show(_shell);
                break;
            case RootPage.Onboarding guide:
                var page = Onboarding(guide.Step);
                if (guide.Model is { } input)
                {
                    page.Model?.RestoreViewState(input);
                }

                page.Form.Name = guide.WorkspaceName;
                page.FocusesLanguage = guide.LanguageChosen && page.CanChooseLanguage && page.IsWelcomeStep;
                _root.Show(page);
                break;
            case RootPage.Recovery recovery:
                _root.Show(new RecoveryViewModel(recovery.Result, recovery.Backups, this, _system));
                break;
            default:
                // Not reached: a switch waits until startup has shown its outcome.
                _root.Show(new StartupViewModel());
                break;
        }
    }

    public async Task RunAsync()
    {
        // Every owner of kept writes was composed with this class (through the exit coordinator and the shell's pages),
        // so retry registration ends here, before any work is admitted.
        _health.Start();
        var installation = Application.Diagnostics.Installation.Describe(_environment);
        LogStarting(_logger, installation);
        var page = new StartupViewModel();
        _root.Show(page);
        await GuardAsync(async () =>
        {
            var outcome = await _coordinator.StartAsync(new Progress<DataUpgradeStarting>(page.Upgrading), CancellationToken.None);
            await ApplyPreferencesAsync(outcome);
            await ApplyAsync(outcome);
            ShowUpgradeNotice(outcome);
        });
    }

    /// <summary>The one notice after an upgrade (gap-schema-4, D-042): to which version, and where the backup is.</summary>
    private void ShowUpgradeNotice(StartupOutcome outcome)
    {
        if (outcome is StartupOutcome.ReadyToWork { Upgrade: { } upgrade })
        {
            _shell.ShowRestoreNotice(UiText.UpgradedNotice(upgrade), isError: false);
        }
    }

    /// <summary>
    /// Start-up's last line (health check 2026-11-12 review): anything a step throws lands on the recovery page with
    /// what went wrong, never on a spinner that turns forever (the UI guard would keep the process alive but cannot show
    /// a notice before the shell is up).
    /// </summary>
    private async Task GuardAsync(Func<Task> start)
    {
        try
        {
            await start();
        }
#pragma warning disable CA1031 // Whatever failed, the recovery page explains it and offers retry and backups.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogStartFailed(_logger, ex);
            var result = new DatabaseOpenResult(DatabaseOpenStatus.IoError, _paths.DataDirectory, _paths.DatabasePath, null, 0, ErrorDetail: ex.Message);
            IReadOnlyList<BackupInfo> backups;
            try
            {
                backups = _coordinator.ListBackups();
            }
#pragma warning disable CA1031 // The list is a convenience on a page that is already about a failure.
            catch (Exception swallowed)
#pragma warning restore CA1031
            {
                DesktopLog.Swallowed(swallowed);
                backups = [];
            }

            _root.Show(new RecoveryViewModel(result, backups, this, _system));
        }
    }

    /// <summary>The log's start line (low finding observability-5): which build, channel, system and schema wrote what follows.</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "Starting {Installation}")]
    private static partial void LogStarting(ILogger logger, string installation);

    [LoggerMessage(Level = LogLevel.Error, Message = "Start-up failed; showing the recovery page")]
    private static partial void LogStartFailed(ILogger logger, Exception exception);

    public Task RetryAsync() => RunAsync();

    /// <summary>The recovery page's diagnostic package (D2 §2.2): the summary and the logs, the data not being open.</summary>
    public void ExportDiagnostics() => _diagnostics.Open(dataAvailable: false);

    /// <summary>Quits through the one exit sequence (it has nothing to ask or save here, but owns the final shutdown).</summary>
    public void Quit() => _exit.InterceptClose();

    public async Task RestoreAsync(BackupInfo backup)
    {
        var page = new StartupViewModel();
        _root.Show(page);
        await GuardAsync(async () =>
        {
            var outcome = await _coordinator.RestoreAsync(backup, new Progress<DataUpgradeStarting>(page.Upgrading), CancellationToken.None);
            await ApplyPreferencesAsync(outcome);
            await ApplyAsync(outcome);
            ShowUpgradeNotice(outcome);
        });
    }

    /// <summary>
    /// The first thing a session started for an in-app restore does (2026-11-12): put the backup in place (or, after a
    /// failure, the previous data back) and continue as a start does, to the last valid page; then say what happened.
    /// When even the previous data cannot be opened, the recovery page shows, never an empty database.
    /// </summary>
    public async Task RestoreInPlaceAsync(BackupInfo backup, string keptCopy)
    {
        ArgumentNullException.ThrowIfNull(backup);
        _health.Start();
        var page = new StartupViewModel();
        _root.Show(page);
        await GuardAsync(async () =>
        {
            var restore = await _coordinator.RestoreInPlaceAsync(backup, keptCopy, new Progress<DataUpgradeStarting>(page.Upgrading), CancellationToken.None);
            await ApplyPreferencesAsync(restore.Outcome);
            await ApplyAsync(restore.Outcome);
            if (restore.Result.Restored)
            {
                var upgraded = restore.Result.Open.Upgrade is { } upgrade ? UiText.RestoredAndUpgraded(upgrade.To) : string.Empty;
                _shell.ShowRestoreNotice(UiText.RestoredNotice(backup.CreatedAt) + upgraded, isError: false);
            }
            else if (restore.Result.Open.IsReady)
            {
                _shell.ShowRestoreNotice(UiText.RestoreFailedNotice(backup.CreatedAt, restore.Result.Failure), isError: true);
            }
        });
    }

    /// <summary>
    /// The stored appearance, sidebar layout and unread marks take effect as soon as the database is open; until then (and on the
    /// recovery page) the application follows the system. A failed read is not an error worth a page: the defaults stay.
    /// </summary>
    private async Task ApplyPreferencesAsync(StartupOutcome outcome)
    {
        if (outcome is StartupOutcome.NeedsRecovery)
        {
            return;
        }

        await _shell.RestoreSidebarAsync();
        await _summaryNetworkDetails.RestoreAsync();
        await _textWidth.RestoreAsync();
        await _marks.LoadAsync(CancellationToken.None);

        try
        {
            _theme.Apply(await _appearance.GetAsync(CancellationToken.None));
        }
#pragma warning disable CA1031 // Appearance is cosmetic; any storage failure here is reported by the pages that need storage.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            _theme.Apply(Appearance.FollowSystem);
        }

        try
        {
            _theme.ApplyBoldColour(await _appearance.GetBoldColourAsync(CancellationToken.None));
        }
#pragma warning disable CA1031 // As for the theme: a failed read keeps the default bold colour.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            _theme.ApplyBoldColour(BoldColour.Brown);
        }
    }

    private async Task ApplyAsync(StartupOutcome outcome)
    {
        switch (outcome)
        {
            case StartupOutcome.NeedsRecovery recovery:
                _root.Show(new RecoveryViewModel(recovery.Result, recovery.Backups, this, _system));
                break;
            case StartupOutcome.NeedsFirstWorkspace:
                _root.Show(Onboarding(await ReadOnboardingAsync()));
                break;
            case StartupOutcome.ReadyToWork ready:
                _workbench.Initialize(ready.Workspaces, ready.LastLocation);
                _root.Show(_shell);

                // A workspace exists, so the guide is behind the user (or predates it): never shown again, even once
                // the last workspace is deleted. Written in the background: a slow store never holds up the workbench.
                OnboardingFinished = FinishOnboardingAsync();
                break;
            default:
                throw new InvalidOperationException($"Unhandled startup outcome {outcome}.");
        }
    }

    /// <summary>The step the guide reached; when it cannot be read the guide starts from its beginning.</summary>
    private async Task<OnboardingStep> ReadOnboardingAsync()
    {
        try
        {
            return await _onboarding.GetAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // The guide's progress is a convenience: starting it again loses nothing.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            return OnboardingStep.Welcome;
        }
    }

    /// <summary>The latest write of the guide's end (tests).</summary>
    internal Task OnboardingFinished { get; private set; } = Task.CompletedTask;

    private async Task FinishOnboardingAsync()
    {
        try
        {
            if (await _onboarding.GetAsync(CancellationToken.None) != OnboardingStep.Done)
            {
                await _onboarding.SetAsync(OnboardingStep.Done, CancellationToken.None);
            }
        }
#pragma warning disable CA1031 // Held in memory for this run; the next start with a workspace tries again.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
        }
    }

    /// <summary>
    /// The guide's last step (2026-11-17): the workbench opens on 新探索 of the new workspace, its input focused, ready
    /// for the first question; back leads to the workspace's home.
    /// </summary>
    private async Task CreateFirstWorkspaceAsync(WorkspaceName name)
    {
        var workspace = await _workspaces.CreateAsync(name, CancellationToken.None);
        _workbench.Initialize([workspace], last: null);
        _workbench.Navigate(new NavigationRequest(MainSection.Explore, NewConversationRoute.Instance));
        _root.Show(_shell);
        OnboardingFinished = FinishOnboardingAsync();
    }
}
