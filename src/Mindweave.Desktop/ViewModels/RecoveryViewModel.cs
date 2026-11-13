using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Startup;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels;

public interface IRecoveryActions
{
    Task RetryAsync();

    Task RestoreAsync(BackupInfo backup);

    /// <summary>Quits through the one exit sequence (it has nothing to ask or save here, but owns the final shutdown).</summary>
    void Quit();
}

/// <summary>
/// Shown when the database cannot be opened for writing. Never repairs or restores on its own:
/// restoring a backup takes an explicit choice followed by a confirmation that states the consequence.
/// </summary>
public sealed partial class RecoveryViewModel : ViewModelBase
{
    private readonly IRecoveryActions _actions;
    private readonly ISystemShell _shell;

    public RecoveryViewModel(DatabaseOpenResult result, IReadOnlyList<BackupInfo> backups, IRecoveryActions actions, ISystemShell shell)
    {
        ArgumentNullException.ThrowIfNull(result);
        _actions = actions;
        _shell = shell;
        Result = result;
        Backups = new ObservableCollection<BackupInfo>(backups);
    }

    public DatabaseOpenResult Result { get; }

    public string Heading => UiText.RecoveryHeading(Result.Status);

    public string Explanation => UiText.RecoveryExplanation(Result);

    public string? Detail => Result.ErrorDetail;

    public string DataDirectory => Result.DataDirectory;

    public ObservableCollection<BackupInfo> Backups { get; }

    /// <summary>
    /// Backups are offered only when restoring one can help: not when the file is held by another program or the disk
    /// refuses access, where the data itself is fine (health check 2026-11-12, errors-4).
    /// </summary>
    public bool HasBackups => Backups.Count > 0 && Result.Status is not (DatabaseOpenStatus.InUse or DatabaseOpenStatus.IoError);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingRestore), nameof(RestoreConsequence))]
    public partial BackupInfo? PendingRestore { get; private set; }

    public bool IsConfirmingRestore => PendingRestore is not null;

    public string? RestoreConsequence => PendingRestore is { } backup ? UiText.RestoreConsequence(backup) : null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand), nameof(ConfirmRestoreCommand))]
    public partial bool IsBusy { get; private set; }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task RetryAsync()
    {
        IsBusy = true;
        try
        {
            await _actions.RetryAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenFolder(DataDirectory);

    [RelayCommand]
    private void Exit() => _actions.Quit();

    [RelayCommand]
    private void ChooseRestore(BackupInfo backup) => PendingRestore = backup;

    [RelayCommand]
    private void CancelRestore() => PendingRestore = null;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ConfirmRestoreAsync()
    {
        if (PendingRestore is not { } backup)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _actions.RestoreAsync(backup);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool IsIdle() => !IsBusy;
}
