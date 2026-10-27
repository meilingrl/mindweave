using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.ViewModels;

public sealed partial class SummaryViewModel
{
    private enum VersionOperation { Adopt, DiscardCandidate, Restore, Delete, Rename }
    // Once attempted, this exact intent survives unknown acknowledgements. Repair never
    // substitutes a newer token or a different candidate/selection for the user's consent.
    private sealed record VersionIntent(VersionOperation Kind, SummaryNodeToken Token, Guid? Target,
        ImmutableArray<Guid> Selection, SummaryReplacementConfirmation? Replacement = null,
        SummaryDeletionPreview? Deletion = null, long NameRevision = 0, string? Name = null);
    private VersionIntent? previewIntent, pendingVersionIntent;
    private bool pendingDurable, pendingNotified;
    private Guid? pendingCurrent;
    private bool pendingNodeDeleted;
    private SummaryDraftResolution? draftResolution;
    private bool resumeProjectionPending;
    private Guid? resumeVersionId;
    private ImmutableArray<Guid> displayedActiveTasks = [];
    private Func<Guid, Task>? notifyCurrent;
    private Func<Guid, Task>? stopSummary;
    private readonly HashSet<Task> versionOperations = [];
    [ObservableProperty] private bool hasRemovedNodeInput;
    private SummaryDraftResolution? removedInput;
    [ObservableProperty] private bool isVersionOperationBusy;
    [ObservableProperty] private bool isConfirmationOpen;
    [ObservableProperty] private string confirmationMessage = "";
    [ObservableProperty] private bool isSelectingDeletion;
    [ObservableProperty] private bool isRenamingNode;
    [ObservableProperty] private string displayNameInput = "";
    [ObservableProperty] private bool needsDraftResolution;
    [ObservableProperty] private bool needsActiveSummaryResolution;
    [ObservableProperty] private bool needsCandidateResolution;
    private bool previewAllowed;
    public ObservableCollection<SummaryVersionHeader> DeletableVersions { get; } = [];
    public ObservableCollection<SummaryVersionHeader> DeletionSelection { get; } = [];
    public bool HasPendingVersionOperation => pendingVersionIntent is not null;
    public bool HasUncertainVersionOperation => pendingVersionIntent is not null && !pendingDurable;
    public string NodeDisplayName => Snapshot?.State.Node.DisplayName ?? "";
    public bool CanOperateVersions => !quiescing && !detached && !IsLoading && !IsSaving && !IsVersionOperationBusy && !blocked() && !HasPendingVersionOperation;
    public bool HasCandidate => Snapshot?.State.Node.CandidateVersionId is not null;
    public bool CanRestoreVersion => Version is not null && Version.Header.Identity.Id != Snapshot?.State.Node.CurrentVersionId && Version.Header.Identity.Id != Snapshot?.State.Node.CandidateVersionId;
    public bool CanConfirmVersionOperation => CanOperateVersions && previewAllowed && !NeedsDraftResolution && !NeedsActiveSummaryResolution && !NeedsCandidateResolution;
    public bool CanDiscardRemovedInput => HasRemovedNodeInput && !quiescing && !IsVersionOperationBusy;
    public bool CanCancelVersionOperation => !IsVersionOperationBusy && !HasPendingVersionOperation;
    public IAsyncRelayCommand AdoptCandidateCommand { get; private set; } = null!;
    public IAsyncRelayCommand DiscardCandidateCommand { get; private set; } = null!;
    public IAsyncRelayCommand RestoreVersionCommand { get; private set; } = null!;
    public IAsyncRelayCommand DeleteVersionsCommand { get; private set; } = null!;
    public IAsyncRelayCommand PreviewDeletionCommand { get; private set; } = null!;
    public IAsyncRelayCommand RenameNodeCommand { get; private set; } = null!;
    public IAsyncRelayCommand ConfirmVersionOperationCommand { get; private set; } = null!;
    public IRelayCommand CancelVersionOperationCommand { get; private set; } = null!;
    public IAsyncRelayCommand SaveDraftBeforeOperationCommand { get; private set; } = null!;
    public IAsyncRelayCommand DiscardDraftBeforeOperationCommand { get; private set; } = null!;
    public IAsyncRelayCommand StopSummaryBeforeOperationCommand { get; private set; } = null!;
    public IAsyncRelayCommand DiscardRemovedInputCommand { get; private set; } = null!;
    public event Action? NodeDeleted;
    public void ConfigureSummaryStop(Func<Guid, Task> stop) => stopSummary = stop;

    private void InitializeVersionOperations(Func<Guid, Task>? currentChanged)
    {
        notifyCurrent = currentChanged;
        DiscardRemovedInputCommand = new AsyncRelayCommand(DiscardRemovedInputAsync);
        AdoptCandidateCommand = new AsyncRelayCommand(() => RunVersionOperationAsync(() => PreviewAsync(VersionOperation.Adopt)));
        DiscardCandidateCommand = new AsyncRelayCommand(() => RunVersionOperationAsync(() => PreviewAsync(VersionOperation.DiscardCandidate)));
        RestoreVersionCommand = new AsyncRelayCommand(() => RunVersionOperationAsync(() => PreviewAsync(VersionOperation.Restore, Version?.Header.Identity.Id)));
        DeleteVersionsCommand = new AsyncRelayCommand(() => RunVersionOperationAsync(OpenDeletionAsync));
        PreviewDeletionCommand = new AsyncRelayCommand(() => RunVersionOperationAsync(() => PreviewAsync(VersionOperation.Delete,
            selection: DeletionSelection.Select(v => v.Identity.Id).ToImmutableArray())));
        RenameNodeCommand = new AsyncRelayCommand(() => RunVersionOperationAsync(() => PreviewAsync(VersionOperation.Rename)));
        ConfirmVersionOperationCommand = new AsyncRelayCommand(() => CanConfirmVersionOperation ? RunVersionOperationAsync(ConfirmVersionOperationAsync) : Task.CompletedTask);
        CancelVersionOperationCommand = new RelayCommand(() => { if (CanCancelVersionOperation) CloseConfirmation(); });
        SaveDraftBeforeOperationCommand = new AsyncRelayCommand(() => RunVersionOperationAsync(() => ResolveDraftAsync(false)));
        DiscardDraftBeforeOperationCommand = new AsyncRelayCommand(() => RunVersionOperationAsync(() => ResolveDraftAsync(true)));
        StopSummaryBeforeOperationCommand = new AsyncRelayCommand(() => RunVersionOperationAsync(StopAffectedSummaryAsync));
    }
    private Task RunVersionOperationAsync(Func<Task> action)
    {
        if (!CanOperateVersions) return Task.CompletedTask;
        var task = RunVersionOperationCoreAsync(action); versionOperations.Add(task); _ = ObserveVersionOperationAsync(task); return task;
    }
    private async Task ObserveVersionOperationAsync(Task task) { try { await task; } finally { versionOperations.Remove(task); } }
    private async Task RunVersionOperationCoreAsync(Func<Task> action)
    {
        IsVersionOperationBusy = true; RefreshAvailability();
        try { await action(); }
        catch
        {
            // A lost acknowledgement may already have removed this body. Stop exposing
            // the cached copy until the exact attempted operation is reconciled.
            if (pendingVersionIntent is { } pending && Version is { } shown &&
                (pending.Deletion?.VersionIds.Contains(shown.Header.Identity.Id) == true ||
                 pending.Kind == VersionOperation.DiscardCandidate && pending.Target == shown.Header.Identity.Id)) ClearRemovedBody();
            ErrorMessage = "操作保存或读取失败。确认目标和输入仍保留；请使用窗口的“重试保存”核对原操作，不会自动改为新的目标。";
            failed();
        }
        finally { IsVersionOperationBusy = false; RefreshAvailability(); }
    }
    private async Task ResolveDraftAsync(bool discard)
    {
        if (!NeedsDraftResolution || previewIntent is not { } intent || draftResolution is null) return;
        if (!Draft.MatchesResolution(draftResolution)) { await PreviewAsync(intent.Kind, intent.Target, intent.Selection, true); return; }
        bool success;
        if (discard) success = await Draft.DiscardAsync(draftResolution);
        else
        {
            var result = await Draft.CommitAsync(draftResolution);
            success = result.Kind is SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange;
        }
        if (!success) { ErrorMessage = "草稿未解决：标题和正文须有效，或草稿已变化；原输入保留。"; return; }
        IsEditing = false;
        await PreviewAsync(intent.Kind, intent.Target, intent.Selection, true);
    }
    private async Task StopAffectedSummaryAsync()
    {
        if (!NeedsActiveSummaryResolution || previewIntent is not { } intent || stopSummary is null) return;
        // Stop only the IDs shown by this confirmation. Newer work needs a new preview.
        foreach (var task in displayedActiveTasks) await stopSummary(task);
        await PreviewAsync(intent.Kind, intent.Target, intent.Selection, true);
    }
    private async Task ConfirmVersionOperationAsync()
    {
        if (previewIntent is not { } intent) return;
        if (intent.Kind == VersionOperation.Rename)
        {
            try { intent = intent with { Name = KnowledgeSummaryProtocol.ParseComplete(KnowledgeSummaryProtocol.Serialize(new(DisplayNameInput, "name"))).Title }; }
            catch (SummaryProtocolException) { ErrorMessage = "显示名须为不超过 160 字的非空单行文字；输入保留。"; return; }
        }
        // A queued editor event can arrive despite disabled controls. Never cross a pointer
        // change using a token which did not represent that input.
        if (AffectsCurrent(intent) && (Draft.IsDirty || Draft.HasPendingCommit || Draft.HasMemoryChanges))
        { await PreviewAsync(intent.Kind, intent.Target, intent.Selection, true); return; }
        pendingVersionIntent = intent; pendingDurable = pendingNotified = false; pendingCurrent = null; pendingNodeDeleted = false;
        OnPropertyChanged(nameof(HasPendingVersionOperation));
        await ExecuteVersionIntentAsync(intent);
    }
    private static bool AffectsCurrent(VersionIntent intent) => intent.Kind is VersionOperation.Adopt or VersionOperation.Restore || intent.Deletion?.Impact.ChangesCurrent == true;
    private async Task ExecuteVersionIntentAsync(VersionIntent intent)
    {
        SummaryOutcomeKind kind; Guid? current = null; string? code;
        switch (intent.Kind)
        {
            case VersionOperation.Adopt:
                var adopted = await store.AdoptSummaryCandidateAsync(intent.Token, intent.Replacement);
                kind = adopted.Kind; code = adopted.Code; current = adopted.Value?.State.Node.CurrentVersionId; break;
            case VersionOperation.DiscardCandidate:
                var discarded = await store.DiscardSummaryCandidateAsync(intent.Token);
                kind = discarded.Kind; code = discarded.Code; break;
            case VersionOperation.Restore:
                var restored = await store.SetCurrentSummaryVersionAsync(intent.Token, intent.Target!.Value);
                kind = restored.Kind; code = restored.Code; current = restored.Value?.State.Node.CurrentVersionId; break;
            case VersionOperation.Delete:
                var deleted = await store.DeleteSummaryVersionsAsync(intent.Deletion!);
                kind = deleted.Kind; code = deleted.Code;
                if (deleted.Value?.Impact.ChangesCurrent == true) current = deleted.Value.FallbackVersionId;
                break;
            default:
                var renamed = await store.RenameSummaryNodeAsync(NodeId, intent.NameRevision, intent.Name!);
                kind = renamed.Kind; code = renamed.Code; break;
        }
        if (kind is SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange)
        {
            pendingDurable = true; pendingCurrent = kind == SummaryOutcomeKind.Applied && current != intent.Token.CurrentVersionId ? current : null;
            pendingNodeDeleted = intent.Deletion?.DeletesNode == true;
            await FinishVersionIntentAsync();
        }
        else
        {
            pendingVersionIntent = null;
            var name = intent.Name;
            await PreviewAsync(intent.Kind, intent.Target, intent.Selection, true);
            if (intent.Kind == VersionOperation.Rename && name is not null) DisplayNameInput = name;
            ErrorMessage = kind == SummaryOutcomeKind.Rejected ? "输入无效，请核对显示名或所选版本。" : $"原操作未执行（{code ?? kind.ToString()}）；请核对当前预览。";
        }
    }
    // Called by Main's existing shared explicit repair / close drain, including detached pages.
    public async Task RepairVersionOperationAsync()
    {
        if (HasRemovedNodeInput) throw new SummaryInputResolutionRequiredException();
        if (pendingVersionIntent is not { } intent) return;
        if (!pendingDurable)
        {
            var node = await store.LoadSummaryNodeAsync(NodeId);
            bool applied = intent.Kind switch
            {
                VersionOperation.Adopt => node is not null && node.State.Node.CurrentVersionId == intent.Target && node.State.Node.CandidateVersionId is null,
                VersionOperation.Restore => node?.State.Node.CurrentVersionId == intent.Target,
                VersionOperation.DiscardCandidate => await store.LoadSummaryVersionAsync(intent.Target!.Value) is null,
                VersionOperation.Delete => await AllDeletedAsync(intent.Selection),
                VersionOperation.Rename => node is not null && node.State.Node.DisplayName == intent.Name && node.State.Node.NameIsManual && node.State.Node.NameRevision == intent.NameRevision + 1,
                _ => false
            };
            if (applied)
            {
                pendingDurable = true;
                pendingNodeDeleted = intent.Deletion?.DeletesNode == true;
                var expectedCurrent = intent.Kind is VersionOperation.Adopt or VersionOperation.Restore ? intent.Target : intent.Deletion?.Impact.ChangesCurrent == true ? intent.Deletion.FallbackVersionId : null;
                pendingCurrent = expectedCurrent != intent.Token.CurrentVersionId && expectedCurrent == node?.State.Node.CurrentVersionId ? expectedCurrent : null;
            }
            else if (node?.State.Token == intent.Token && (intent.Kind != VersionOperation.Rename || node.State.Node.NameRevision == intent.NameRevision))
            {
                if (AffectsCurrent(intent) && (Draft.IsDirty || Draft.HasMemoryChanges || Draft.HasPendingCommit))
                {
                    pendingVersionIntent = null;
                    await PreviewAsync(intent.Kind, intent.Target, intent.Selection, true); return;
                }
                await ExecuteVersionIntentAsync(intent); return;
            }
            else
            {
                // Authoritative divergence is a business conflict, not permission to replay.
                pendingVersionIntent = null;
                await PreviewAsync(intent.Kind, intent.Target, intent.Selection, true);
                if (intent.Kind == VersionOperation.Rename) DisplayNameInput = intent.Name!;
                ErrorMessage = "已核对原操作；状态已变化，未使用新令牌重试。请检查新预览后再决定。";
                return;
            }
        }
        await FinishVersionIntentAsync();
    }
    private async Task<bool> AllDeletedAsync(ImmutableArray<Guid> ids)
    { foreach (var id in ids) if (await store.LoadSummaryVersionAsync(id) is not null) return false; return true; }
    private async Task FinishVersionIntentAsync()
    {
        var intent = pendingVersionIntent!;
        // Clear removed body before naming or any later load can yield/fail.
        if (pendingNodeDeleted || (Version is { } shown && (intent.Deletion?.VersionIds.Contains(shown.Header.Identity.Id) == true ||
            intent.Kind == VersionOperation.DiscardCandidate && shown.Header.Identity.Id == intent.Target))) ClearRemovedBody();
        if (!pendingNotified && pendingCurrent is { } current && notifyCurrent is not null)
        { await notifyCurrent(current); pendingNotified = true; }
        if (AffectsCurrent(intent) && !await Draft.RebaseAfterCurrentChangeAsync())
        {
            ShowRemovedInput(); return;
        }
        if (quiescing)
        {
            resumeProjectionPending = true;
            resumeVersionId = AffectsCurrent(intent) || Version is null ? null : Version.Header.Identity.Id;
        }
        if (!detached && !quiescing)
        {
            var node = await store.LoadSummaryNodeAsync(NodeId);
            if (node is not null)
            {
                if (AffectsCurrent(intent) || Version is null) await SelectVersionAsync(node.State.Node.CurrentVersionId);
                else await RefreshMetadataAsync();
            }
        }
        pendingVersionIntent = null; CloseConfirmation(); ErrorMessage = null;
        if (pendingNodeDeleted) NodeDeleted?.Invoke();
        RefreshAvailability();
    }
    private void ShowRemovedInput()
    {
        HasRemovedNodeInput = true; removedInput = Draft.CaptureRemovedInput(); draftResolution = removedInput;
        NeedsDraftResolution = NeedsActiveSummaryResolution = NeedsCandidateResolution = false;
        IsSelectingDeletion = IsRenamingNode = false; previewAllowed = false;
        IsConfirmationOpen = true;
        ConfirmationMessage = "总结节点和所选版本已经删除，已保存的正文及阅读记录不可恢复。\n删除确认后又到达了以下未保存输入；这不是历史版本，不会写回或复活节点。请先复制需要的文字，再明确确认丢弃这份内存输入。取消退出可继续阅读和复制此处文字。";
        ErrorMessage = null; RefreshAvailability();
    }
    private Task DiscardRemovedInputAsync()
    {
        if (!CanDiscardRemovedInput || removedInput is null) return Task.CompletedTask;
        if (!Draft.DiscardRemovedInput(removedInput)) { ShowRemovedInput(); return Task.CompletedTask; }
        HasRemovedNodeInput = false; removedInput = null; pendingVersionIntent = null;
        CloseConfirmation(); NodeDeleted?.Invoke(); return Task.CompletedTask;
    }
    private void ResumeVersionProjection()
    {
        if (HasRemovedNodeInput || !resumeProjectionPending) return;
        resumeProjectionPending = false;
        if (pendingNodeDeleted) NodeDeleted?.Invoke();
        else _ = SelectVersionAsync(resumeVersionId);
    }
    private void ClearRemovedBody()
    {
        ++loadStamp; InterruptReading(); Version = null; Reading = null; SavedBody = Heading = Metadata = SourceQuote = ""; IsEditing = false; SelectedVersion = null;
        if (pendingNodeDeleted) { Snapshot = null; Versions.Clear(); }
        RefreshAvailability();
    }
    private void RefreshVersionAvailability()
    {
        foreach (var name in new[] { nameof(CanDiscardRemovedInput), nameof(CanOperateVersions), nameof(CanConfirmVersionOperation), nameof(CanCancelVersionOperation), nameof(HasCandidate), nameof(CanRestoreVersion), nameof(NodeDisplayName), nameof(HasPendingVersionOperation), nameof(HasUncertainVersionOperation), nameof(ResolutionDraftTitle), nameof(ResolutionDraftBody) }) OnPropertyChanged(name);
    }
    partial void OnIsConfirmationOpenChanged(bool value) { Draft.PauseForVersionConfirmation(value); if (value) InterruptReading(); RefreshAvailability(); }
}
