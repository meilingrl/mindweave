using Mindweave.Application.Conversations;
using Mindweave.Application.Preferences;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Services;

/// <summary>Drives root navigation from the startup outcome: recovery page, onboarding, or the workbench.</summary>
public sealed class AppStartup : IRecoveryActions
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
        ConversationMarksService marks)
    {
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
        // The last workspace was deleted: back to the first-run flow, as on a fresh start.
        workspaceDeletion.NoWorkspaceLeft += (_, _) => _root.Show(new OnboardingViewModel(CreateFirstWorkspaceAsync));
    }

    public async Task RunAsync()
    {
        // Every owner of kept writes was composed with this class (through the exit coordinator and the shell's pages),
        // so retry registration ends here, before any work is admitted.
        _health.Start();
        _root.Show(new StartupViewModel());
        var outcome = await _coordinator.StartAsync(CancellationToken.None);
        await ApplyPreferencesAsync(outcome);
        Apply(outcome);
    }

    public Task RetryAsync() => RunAsync();

    public void Quit() => _exit.InterceptClose();

    public async Task RestoreAsync(BackupInfo backup)
    {
        _root.Show(new StartupViewModel());
        var outcome = await _coordinator.RestoreAsync(backup, CancellationToken.None);
        await ApplyPreferencesAsync(outcome);
        Apply(outcome);
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
        await _marks.LoadAsync(CancellationToken.None);

        try
        {
            _theme.Apply(await _appearance.GetAsync(CancellationToken.None));
        }
#pragma warning disable CA1031 // Appearance is cosmetic; any storage failure here is reported by the pages that need storage.
        catch (Exception)
#pragma warning restore CA1031
        {
            _theme.Apply(Appearance.FollowSystem);
        }
    }

    private void Apply(StartupOutcome outcome)
    {
        switch (outcome)
        {
            case StartupOutcome.NeedsRecovery recovery:
                _root.Show(new RecoveryViewModel(recovery.Result, recovery.Backups, this, _system));
                break;
            case StartupOutcome.NeedsFirstWorkspace:
                _root.Show(new OnboardingViewModel(CreateFirstWorkspaceAsync));
                break;
            case StartupOutcome.ReadyToWork ready:
                _workbench.Initialize(ready.Workspaces, ready.LastLocation);
                _root.Show(_shell);
                break;
            default:
                throw new InvalidOperationException($"Unhandled startup outcome {outcome}.");
        }
    }

    private async Task CreateFirstWorkspaceAsync(WorkspaceName name)
    {
        var workspace = await _workspaces.CreateAsync(name, CancellationToken.None);
        _workbench.Initialize([workspace], last: null);
        _root.Show(_shell);
    }
}
