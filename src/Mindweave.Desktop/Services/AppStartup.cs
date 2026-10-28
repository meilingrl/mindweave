using Mindweave.Application.Startup;
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

    public AppStartup(
        StartupCoordinator coordinator,
        WorkspaceService workspaces,
        IRootNavigator root,
        Workbench workbench,
        ShellViewModel shell,
        ISystemShell system,
        ExitCoordinator exit)
    {
        _coordinator = coordinator;
        _workspaces = workspaces;
        _root = root;
        _workbench = workbench;
        _shell = shell;
        _system = system;
        _exit = exit;
    }

    public async Task RunAsync()
    {
        _root.Show(new StartupViewModel());
        Apply(await _coordinator.StartAsync(CancellationToken.None));
    }

    public Task RetryAsync() => RunAsync();

    public void Quit() => _exit.InterceptClose();

    public async Task RestoreAsync(BackupInfo backup)
    {
        _root.Show(new StartupViewModel());
        Apply(await _coordinator.RestoreAsync(backup, CancellationToken.None));
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
