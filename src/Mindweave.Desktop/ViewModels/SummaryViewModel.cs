using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.ViewModels;

public sealed partial class SummaryViewModel : ContentViewModel
{
    private readonly ISummaryVersionStore store;
    private readonly Func<bool> blocked;
    private readonly Action failed;
    private long loadStamp;
    private bool detached, hasLaterContent, quiescing;
    private readonly HashSet<Task> versionLoads = [];
    public Guid WorkspaceId { get; }
    public Guid NodeId { get; }
    public SummaryDraftSession Draft { get; }
    public SummaryNodeSnapshot? Snapshot { get; private set; }
    public SummaryVersion? Version { get; private set; }
    public ObservableCollection<SummaryVersionHeader> Versions { get; } = [];
    [ObservableProperty] private SummaryVersionHeader? selectedVersion;
    [ObservableProperty] private string savedBody = "";
    [ObservableProperty] private string heading = "";
    [ObservableProperty] private string metadata = "";
    [ObservableProperty] private string sourceQuote = "";
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private bool isEditing;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isSaving;
    public bool CanEditCurrent => !quiescing && !IsConfirmationOpen && !IsVersionOperationBusy && !IsSaving && !IsLoading && Version is not null && Version.Header.Identity.Id == Snapshot?.State.Node.CurrentVersionId && !blocked();
    public bool CanRead => !quiescing && !detached && !IsLoading && !IsEditing && !IsConfirmationOpen && !IsVersionOperationBusy && Version is not null;
    public string VersionIdentity => Version is null ? "" : Version.Header.Identity.Id == Snapshot?.State.Node.CandidateVersionId ? "候选版本 · 只读" : Version.Header.Identity.Id == Snapshot?.State.Node.CurrentVersionId ? "当前版本" : "历史版本 · 只读";
    public bool IsRead => Reading?.State.IsRead == true;
    public bool HasSourceQuote => !string.IsNullOrEmpty(SourceQuote);
    public bool CanSetRead => Version is not null && !quiescing && !IsConfirmationOpen && !IsVersionOperationBusy && !blocked();
    public string ReadStatus => IsRead ? "已读" : "未读";
    public IAsyncRelayCommand EditCommand { get; }
    public IRelayCommand ReadCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand MarkReadCommand { get; }
    public IAsyncRelayCommand MarkUnreadCommand { get; }
    public IAsyncRelayCommand ReturnToConversationCommand { get; }
    public IAsyncRelayCommand<SummaryVersionHeader> ViewVersionCommand { get; }
    public event Action<Guid>? VersionSelected;
    public event Func<Task>? CaptureReadingRequested;
    public SummaryViewModel(Guid workspaceId, Guid nodeId, ISummaryVersionStore store, SummaryDraftSession draft,
        Func<bool> blocked, Action failed, Func<Guid, Task> source, Func<Guid, Task>? currentChanged = null)
    {
        WorkspaceId = workspaceId; NodeId = nodeId; this.store = store; Draft = draft; this.blocked = blocked; this.failed = failed; Draft.ConfigureCurrentChanged(currentChanged); InitializeVersionOperations(currentChanged);
        EditCommand = new AsyncRelayCommand(async () =>
        {
            if (!CanEditCurrent) return;
            try { await Draft.BeginAsync(); IsEditing = true; } catch { ErrorMessage = "编辑草稿读取失败。"; failed(); }
        });
        ReadCommand = new RelayCommand(() => IsEditing = false);
        SaveCommand = new AsyncRelayCommand(CommitAsync);
        MarkReadCommand = new AsyncRelayCommand(() => SetReadAsync(true));
        MarkUnreadCommand = new AsyncRelayCommand(() => SetReadAsync(false));
        ReturnToConversationCommand = new AsyncRelayCommand(() => Snapshot is null ? Task.CompletedTask : source(Snapshot.State.Node.ConversationId));
        ViewVersionCommand = new AsyncRelayCommand<SummaryVersionHeader>(header => header is null ? Task.CompletedTask : SelectVersionAsync(header.Identity.Id));
    }
    public Task LoadAsync(Guid? versionId = null) => SelectVersionAsync(versionId);
    public Task SelectVersionAsync(Guid? versionId)
    {
        if (quiescing || detached) return Task.CompletedTask;
        var task = SelectVersionCoreAsync(versionId); versionLoads.Add(task); _ = ObserveLoadAsync(task); return task;
    }
    private async Task ObserveLoadAsync(Task task)
    {
        try { await task; } catch { /* The caller/close path still observes the operation's result. */ }
        finally { versionLoads.Remove(task); }
    }
    private async Task SelectVersionCoreAsync(Guid? versionId)
    {
        var stamp = ++loadStamp;
        await CaptureReadingAsync();
        if (stamp != loadStamp || detached) return;
        IsLoading = true; Version = null; SavedBody = ""; Heading = ""; Metadata = ""; SourceQuote = ""; IsEditing = false; Reading = null; RefreshAvailability();
        try
        {
            try { await Draft.FlushAsync(); }
            catch { /* Memory and shared failure gate retain the draft; saved versions remain readable. */ }
            await FlushReadingAsync();
            var node = await store.LoadSummaryNodeAsync(NodeId);
            if (node?.State.Node.WorkspaceId != WorkspaceId) throw new ArgumentException("总结不存在或不属于当前主题。");
            var id = versionId ?? node.State.Node.CurrentVersionId;
            if (!node.State.Versions.Any(h => h.Identity.Id == id)) throw new ArgumentException("该版本已删除或不属于此总结。");
            var version = await store.LoadSummaryVersionAsync(id);
            if (version?.Header.Identity.NodeId != NodeId) throw new ArgumentException("该版本已删除。");
            var reading = await store.LoadSummaryReadingAsync(id);
            var later = store is Mindweave.Application.Chat.IChatStore chat &&
                (await chat.ReadMessagesAsync(node.State.Node.ConversationId)).Any(m => m.Sequence > version.Coverage.OwnHighWaterSequence &&
                    m.Status is not (Mindweave.Core.Chat.GenerationStatus.Queued or Mindweave.Core.Chat.GenerationStatus.Running));
            if (stamp != loadStamp || detached) return;
            Snapshot = node; Version = version; Reading = reading;
            Versions.Clear(); foreach (var header in node.State.Versions.OrderByDescending(v => v.Identity.Ordinal)) Versions.Add(header);
            SelectedVersion = version.Header; SavedBody = version.Markdown; Heading = version.Header.Title;
            SourceQuote = version.Coverage.DirectQuote?.Source.Quote ?? "";
            hasLaterContent = later; Metadata = DescribeVersion(version, later);
            ErrorMessage = Draft.Error; VersionSelected?.Invoke(id);
        }
        catch (ArgumentException error) { if (stamp == loadStamp) ErrorMessage = error.Message; }
        catch { if (stamp == loadStamp) { ErrorMessage = "版本读取或草稿保存失败；原编辑输入仍在内存。"; failed(); } }
        finally { if (stamp == loadStamp) { IsLoading = false; RefreshAvailability(); } }
    }
    private long metadataStamp;
    public async Task RefreshMetadataAsync()
    {
        var stamp = ++metadataStamp; var bodyStamp = loadStamp; var version = Version;
        if (detached || version is null) return;
        var node = await store.LoadSummaryNodeAsync(NodeId);
        if (stamp != metadataStamp || bodyStamp != loadStamp || detached) return;
        if (node?.State.Node.WorkspaceId != WorkspaceId || !node.State.Versions.Any(h => h.Identity.Id == version.Header.Identity.Id))
        {
            Version = null; SavedBody = ""; Heading = ""; Metadata = ""; SourceQuote = ""; Reading = null;
            ErrorMessage = "该总结版本已删除，请返回列表。"; RefreshAvailability(); return;
        }
        Snapshot = node; hasLaterContent |= node.HasLaterContent; Metadata = DescribeVersion(version, hasLaterContent);
        // Preserve selected item/control identity so a new candidate does not navigate or discard an editor.
        foreach (var removed in Versions.Where(h => !node.State.Versions.Any(v => v.Identity.Id == h.Identity.Id)).ToArray()) Versions.Remove(removed);
        foreach (var header in node.State.Versions.OrderByDescending(v => v.Identity.Ordinal))
            if (!Versions.Any(h => h.Identity.Id == header.Identity.Id)) Versions.Insert(0, header);
        RefreshAvailability();
    }
    private static string DescribeVersion(SummaryVersion version, bool later) =>
        $"版本保存于 {version.Header.Identity.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · 范围在发起时固定，包含 {version.Coverage.OwnMessages.Length} 条本会话消息、{version.Coverage.FrozenAncestors.Length} 条来源消息。" +
        (later ? " 此后有新内容，未纳入本次总结。" : " 当前没有后续内容标记。") +
        (version.Header.Identity.Origin == SummaryVersionOrigin.Manual ? " 手动编辑版本，沿用原覆盖范围。" : $" 请求模型：{version.Header.RequestedModelId ?? "未记录"}；实际模型：{version.Header.ActualModelId ?? "未报告"}。");

    private async Task CommitAsync()
    {
        if (!CanEditCurrent || !IsEditing) return;
        IsSaving = true; RefreshAvailability();
        try
        {
            var result = await Draft.CommitAsync();
            if (result.Kind is not (SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange))
            { ErrorMessage = "无法保存此编辑：标题和正文须完整有效，或版本已发生变化。草稿已保留。"; return; }
            var id = result.Value!.Header.Identity.Id;
            await SelectVersionAsync(id);
        }
        catch { ErrorMessage = "保存失败，编辑输入保留在窗口中，请重试保存。"; failed(); }
        finally { IsSaving = false; RefreshAvailability(); }
    }
    partial void OnSourceQuoteChanged(string value) => OnPropertyChanged(nameof(HasSourceQuote));
    partial void OnIsEditingChanged(bool value) => RefreshAvailability();
    public void RefreshAvailability()
    { RefreshVersionAvailability(); OnPropertyChanged(nameof(CanEditCurrent)); OnPropertyChanged(nameof(CanRead)); OnPropertyChanged(nameof(VersionIdentity)); OnPropertyChanged(nameof(IsRead)); OnPropertyChanged(nameof(ReadStatus)); OnPropertyChanged(nameof(CanSetRead)); }
    private async Task CaptureReadingAsync()
    {
        if (CaptureReadingRequested is { } handlers)
            foreach (Func<Task> capture in handlers.GetInvocationList()) await capture();
    }
    public async Task FlushAsync(bool repairVersionOperation = false)
    {
        if (HasRemovedNodeInput) throw new SummaryInputResolutionRequiredException();
        if (repairVersionOperation) await RepairVersionOperationAsync();
        await CaptureReadingAsync();
        // Navigation remains readable while paused; only explicit repair/close can
        // reconcile a mutation whose acknowledgement was lost.
        if (!HasPendingVersionOperation) await Draft.FlushAsync();
        await FlushReadingAsync();
    }
    public override async Task WaitForPendingOperationsAsync()
    {
        await Task.WhenAll(new[] { EditCommand.ExecutionTask, SaveCommand.ExecutionTask, MarkReadCommand.ExecutionTask, MarkUnreadCommand.ExecutionTask, ViewVersionCommand.ExecutionTask }.OfType<Task>());
        while (versionOperations.Count > 0)
        {
            await Task.WhenAll(versionOperations.ToArray());
            versionOperations.RemoveWhere(task => task.IsCompleted);
        }
        while (versionLoads.Count > 0)
        {
            await Task.WhenAll(versionLoads.ToArray());
            versionLoads.RemoveWhere(task => task.IsCompleted);
        }
    }
    public void QuiesceForClose() { quiescing = true; InterruptReading(); Draft.StopDebounce(); RefreshAvailability(); }
    public void ResumeAfterClose() { quiescing = false; ResumeVersionProjection(); RefreshAvailability(); }
    public void Detach()
    {
        detached = true; loadStamp++; Draft.StopDebounce();
        // Backstack retains route identity only; no hidden full-version cache survives navigation/deletion.
        Version = null; Snapshot = null; SavedBody = ""; Heading = ""; SourceQuote = ""; Metadata = ""; SelectedVersion = null; Versions.Clear(); Draft.ReleaseCleanMemory(); RefreshAvailability();
    }
}
