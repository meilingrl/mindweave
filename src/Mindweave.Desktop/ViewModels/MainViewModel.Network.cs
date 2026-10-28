using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Navigation;
using Mindweave.Application.Network;
using Mindweave.Desktop.Network;

namespace Mindweave.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private Task<LayoutLeavePermit?>? layoutConfirmation;
    private TaskCompletionSource<LayoutLeaveChoice>? layoutChoice;
    private LayoutLeavePermit? issuedLayoutPermit;
    private bool layoutDrainComplete;
    [ObservableProperty] private bool isLayoutLeaveConfirmationVisible;
    [ObservableProperty] private string? layoutLeaveError;
    public IRelayCommand SaveLayoutAndContinueCommand { get; private set; } = null!;
    public IRelayCommand DiscardLayoutAndContinueCommand { get; private set; } = null!;
    public IRelayCommand CancelLayoutLeaveCommand { get; private set; } = null!;
    private void InitializeNetworkCommands()
    {
        // These callbacks must never queue behind a navigation holding changes.
        SaveLayoutAndContinueCommand = new RelayCommand(() => ResolveLayoutChoice(LayoutLeaveChoice.Save));
        DiscardLayoutAndContinueCommand = new RelayCommand(() => ResolveLayoutChoice(LayoutLeaveChoice.Discard));
        CancelLayoutLeaveCommand = new RelayCommand(CancelLayoutLeave);
    }
    private void ResolveLayoutChoice(LayoutLeaveChoice choice) => layoutChoice?.TrySetResult(choice);
    public void CancelLayoutLeave()
    {
        issuedLayoutPermit = null; layoutDrainComplete = false;
        ResolveLayoutChoice(LayoutLeaveChoice.Cancel);
    }
    public Task<LayoutLeavePermit?> ConfirmLayoutLeaveAsync()
    {
        if (layoutConfirmation is { IsCompleted: false }) return layoutConfirmation;
        return layoutConfirmation = ConfirmLayoutLeaveCoreAsync();
    }
    private async Task<LayoutLeavePermit?> ConfirmLayoutLeaveCoreAsync()
    {
        issuedLayoutPermit = null; layoutDrainComplete = false; LayoutLeaveError = null;
        var workspace = CurrentWorkspace?.Id ?? Guid.Empty;
        while (true)
        {
            if ((CurrentWorkspace?.Id ?? Guid.Empty) != workspace) return null;
            var session = networkSessions.GetValueOrDefault(workspace);
            if (session?.IsDirty != true)
                return issuedLayoutPermit = new(workspace, session?.Revision ?? 0, false);
            layoutChoice = new(TaskCreationOptions.RunContinuationsAsynchronously);
            IsLayoutLeaveConfirmationVisible = true; NotifyAvailability();
            LayoutLeaveChoice choice;
            try { choice = await layoutChoice.Task; }
            finally { layoutChoice = null; IsLayoutLeaveConfirmationVisible = false; NotifyAvailability(); }
            if (choice == LayoutLeaveChoice.Cancel) return null;
            if ((CurrentWorkspace?.Id ?? Guid.Empty) != workspace) return null;
            if (choice == LayoutLeaveChoice.Discard)
                return issuedLayoutPermit = new(workspace, session.Revision, true);
            if (!await session.SaveAsync()) { LayoutLeaveError = session.ErrorMessage; return null; }
            // A successful snapshot can still leave a newer Arrange dirty.
        }
    }
    internal bool IsLayoutLeavePermitCurrent(LayoutLeavePermit permit)
    {
        var session = networkSessions.GetValueOrDefault(permit.WorkspaceId);
        return ReferenceEquals(issuedLayoutPermit, permit) && (CurrentWorkspace?.Id ?? Guid.Empty) == permit.WorkspaceId &&
            (session?.Revision ?? 0) == permit.Revision && (permit.Discard || session?.IsDirty != true);
    }
    public bool TryCommitLayoutLeave(LayoutLeavePermit permit)
    {
        if (!IsLayoutLeavePermitCurrent(permit)) return false;
        var session = networkSessions.GetValueOrDefault(permit.WorkspaceId);
        if (session is not null && (!session.IsQuiesced || !layoutDrainComplete)) return false;
        issuedLayoutPermit = null;
        if (permit.Discard) session?.Discard();
        return true;
    }
    private async Task<bool> LeaveWorkspaceLayoutAsync()
    {
        while (await ConfirmLayoutLeaveAsync() is { } permit)
        {
            var session = networkSessions.GetValueOrDefault(permit.WorkspaceId);
            session?.Quiesce();
            if (session is not null) await session.WaitForPendingOperationsAsync();
            layoutDrainComplete = true;
            if (TryCommitLayoutLeave(permit)) return true;
            session?.Resume();
        }
        return false;
    }
    internal async Task QuiesceNetworkForCloseAsync()
    {
        closing = true; NotifyAvailability();
        await changes.WaitAsync(); changes.Release();
        foreach (var session in networkSessions.Values) session.Quiesce();
        await Task.WhenAll(networkSessions.Values.Select(session => session.WaitForPendingOperationsAsync()));
        layoutDrainComplete = true;
    }
    private void ResumeCurrentNetwork()
    { layoutDrainComplete = false; networkSessions.GetValueOrDefault(CurrentWorkspace?.Id ?? Guid.Empty)?.Resume(); }
    private enum LayoutLeaveChoice { Save, Discard, Cancel }
    private readonly Dictionary<Guid, NetworkSession> networkSessions = [];
    private async Task<NetworkViewModel> CreateNetworkAsync(bool fitAll)
    {
        var id = CurrentWorkspace!.Id;
        if (!networkSessions.TryGetValue(id, out var session))
            networkSessions[id] = session = new(id, (INetworkStore)store);
        session.Resume();
        var page = new NetworkViewModel(session, node => NavigateAsync(new(MainArea.Network, ContentKind.Summary, node)));
        await page.ReloadAsync(fitAll); return page;
    }
}
