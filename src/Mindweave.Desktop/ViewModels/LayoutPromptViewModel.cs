using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Network;

namespace Mindweave.Desktop.ViewModels;

/// <summary>
/// Leaving with an unsaved network layout (requirement §7.4): switching workspace or quitting first asks
/// "save and continue / don't save / cancel". If saving fails, nothing is left. Opening a summary and coming back
/// never asks. When leaving a workspace "don't save" discards at once (nothing cancelable follows); when quitting it is
/// only recorded and handed to the exit coordinator, which discards once the exit is committed (architecture §12.3).
/// </summary>
public sealed partial class LayoutPromptViewModel(NetworkSessions sessions) : ViewModelBase
{
    private List<NetworkSession> _pending = [];
    private Action<IReadOnlyList<NetworkSession>>? _continue;
    private Action? _cancelled;
    private bool _deferDiscard;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAndContinueCommand), nameof(DontSaveCommand), nameof(CancelCommand))]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    public partial bool SaveFailed { get; private set; }

    /// <summary>Before leaving <paramref name="workspace"/>: true when the user must answer first; then <paramref name="continueWith"/> runs after "save" or "don't save".</summary>
    public bool HoldLeaving(WorkspaceId workspace, Action continueWith)
    {
        ArgumentNullException.ThrowIfNull(continueWith);
        return Hold([.. sessions.Dirty.Where(s => s.Workspace == workspace)], _ => continueWith(), null, deferDiscard: false);
    }

    /// <summary>
    /// Before quitting: any workspace's unsaved layout counts. True when the user must answer first; then
    /// <paramref name="continueWith"/> receives the sessions to discard once the exit is committed (none after "save"),
    /// or <paramref name="cancelled"/> runs.
    /// </summary>
    /// <summary>A network layout has changes not yet saved (an exit would ask about them).</summary>
    public bool HasUnsavedLayout => sessions.Dirty.Any();

    public bool HoldExit(Action<IReadOnlyList<NetworkSession>> continueWith, Action cancelled) =>
        Hold([.. sessions.Dirty], continueWith, cancelled, deferDiscard: true);

    private bool Hold(List<NetworkSession> pending, Action<IReadOnlyList<NetworkSession>> continueWith, Action? cancelled, bool deferDiscard)
    {
        if (pending.Count == 0)
        {
            return false;
        }

        _pending = pending;
        _continue = continueWith;
        _cancelled = cancelled;
        _deferDiscard = deferDiscard;
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

        Proceed([]);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void DontSave()
    {
        if (_deferDiscard)
        {
            Proceed(_pending);
            return;
        }

        foreach (var session in _pending)
        {
            session.DiscardChanges();
        }

        Proceed([]);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Cancel()
    {
        IsOpen = false;
        var cancelled = _cancelled;
        _continue = null;
        _cancelled = null;
        cancelled?.Invoke();
    }

    private bool CanAct() => !IsSaving;

    private void Proceed(IReadOnlyList<NetworkSession> discardLater)
    {
        IsOpen = false;
        var next = _continue;
        _continue = null;
        _cancelled = null;
        next?.Invoke(discardLater);
    }
}
