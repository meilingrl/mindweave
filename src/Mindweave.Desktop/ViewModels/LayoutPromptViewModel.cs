using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Network;

namespace Mindweave.Desktop.ViewModels;

/// <summary>
/// Leaving with an unsaved network layout (requirement §7.4): switching workspace or quitting first asks
/// "save and continue / don't save / cancel". If saving fails, nothing is left. Opening a summary and coming back
/// never asks.
/// </summary>
public sealed partial class LayoutPromptViewModel(NetworkSessions sessions) : ViewModelBase
{
    private List<NetworkSession> _pending = [];
    private Action? _continue;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAndContinueCommand), nameof(DontSaveCommand), nameof(CancelCommand))]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    public partial bool SaveFailed { get; private set; }

    /// <summary>Before leaving <paramref name="workspace"/>: true when the user must answer first; then <paramref name="continueWith"/> runs after "save" or "don't save".</summary>
    public bool HoldLeaving(WorkspaceId workspace, Action continueWith) => Hold([.. sessions.Dirty.Where(s => s.Workspace == workspace)], continueWith);

    /// <summary>Before quitting: any workspace's unsaved layout counts.</summary>
    public bool HoldExit(Action continueWith) => Hold([.. sessions.Dirty], continueWith);

    private bool Hold(List<NetworkSession> pending, Action continueWith)
    {
        if (pending.Count == 0)
        {
            return false;
        }

        _pending = pending;
        _continue = continueWith;
        SaveFailed = false;
        IsOpen = true;
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task SaveAndContinueAsync()
    {
        IsSaving = true;
        try
        {
            foreach (var session in _pending)
            {
                if (!await session.SaveAsync(CancellationToken.None))
                {
                    SaveFailed = true;
                    return;
                }
            }
        }
        finally
        {
            IsSaving = false;
        }

        Proceed();
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void DontSave()
    {
        foreach (var session in _pending)
        {
            session.DiscardChanges();
        }

        Proceed();
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Cancel()
    {
        IsOpen = false;
        _continue = null;
    }

    private bool CanAct() => !IsSaving;

    private void Proceed()
    {
        IsOpen = false;
        var next = _continue;
        _continue = null;
        next?.Invoke();
    }
}
