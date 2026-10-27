using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;
namespace Mindweave.Desktop.ViewModels;

public sealed partial class ChatViewModel : ContentViewModel
{
    private readonly IChatStore store;
    private readonly GenerationCoordinator coordinator;
    private readonly Func<bool> blocked;
    private readonly Action persistenceFailed;
    private readonly SemaphoreSlim drafts = new(1, 1);
    private CancellationTokenSource? debounce;
    private long draftRevision;
    private long savedRevision;
    private bool loading, needsAcceptedReload;
    private string storedTitle = "";
    public Guid ConversationId { get; }
    public ObservableCollection<MessageViewModel> Messages { get; } = [];
    [ObservableProperty] private string inputText = "";
    [ObservableProperty] private string titleText = "";
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private bool isAccepting;
    public bool CanEdit => !IsAccepting && !blocked();
    public bool CanSend => CanEdit && !HasActiveReply;
    public bool HasActiveReply => coordinator.Snapshots.Any(s => s.ConversationId == ConversationId && s.Kind == GenerationKind.Reply && s.Status is GenerationStatus.Queued or GenerationStatus.Running);
    public bool CanRetry => CanSend && Messages.LastOrDefault() is { } last && last.RoleLabel.StartsWith("AI", StringComparison.Ordinal);
    public IAsyncRelayCommand SendCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand RenameCommand { get; }
    public ChatViewModel(Guid conversationId, IChatStore store, GenerationCoordinator coordinator, Func<bool> blocked, Action persistenceFailed)
    {
        ConversationId = conversationId; this.store = store; this.coordinator = coordinator; this.blocked = blocked; this.persistenceFailed = persistenceFailed;
        SendCommand = new AsyncRelayCommand(() => SubmitAsync(false));
        RetryCommand = new AsyncRelayCommand(() => SubmitAsync(true));
        StopCommand = new AsyncRelayCommand(async () => { try { await coordinator.StopAsync(ConversationId); Project(); } catch { ErrorMessage = "停止后的内容保存失败，请重试保存。"; persistenceFailed(); } });
        RenameCommand = new AsyncRelayCommand(async () =>
        {
            if (blocked()) return;
            var submittedTitle = TitleText;
            try { await store.RenameConversationAsync(ConversationId, submittedTitle); storedTitle = submittedTitle; ErrorMessage = null; }
            catch (ArgumentException error) { ErrorMessage = error.Message; }
            catch { ErrorMessage = "标题保存失败，输入已保留。"; persistenceFailed(); }
        });
    }
    public async Task InitializeAsync()
    {
        var conversation = await store.LoadConversationAsync(ConversationId) ?? throw new ArgumentException("会话不存在。");
        TitleText = storedTitle = conversation.Title;
        foreach (var message in await store.ReadMessagesAsync(ConversationId)) Messages.Add(new(message));
        await LoadContextUsageAsync();
        await ReloadDraftAsync(); Project();
    }
    private async Task ReloadDraftAsync()
    {
        var draft = await store.LoadDraftAsync(ConversationId);
        loading = true; try { InputText = draft.Text; draftRevision = savedRevision = draft.Revision; } finally { loading = false; }
    }
    partial void OnInputTextChanged(string value)
    {
        if (loading) return;
        draftRevision = checked(draftRevision + 1);
        debounce?.Cancel(); debounce?.Dispose(); debounce = new(); _ = SaveAfterDelayAsync(debounce.Token);
    }
    private async Task SaveAfterDelayAsync(CancellationToken cancellation)
    {
        try { await Task.Delay(300, cancellation); await FlushDraftAsync(); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch { ErrorMessage = "草稿保存失败，输入仍保留在当前窗口。"; persistenceFailed(); }
    }
    public async Task FlushDraftAsync()
    {
        debounce?.Cancel();
        await drafts.WaitAsync();
        try
        {
            // An input notification already queued before navigation froze the control can
            // arrive while the first SQLite write awaits. Drain through the latest revision.
            while (draftRevision > savedRevision)
            {
                var revision = draftRevision; var text = InputText;
                await store.SaveDraftAsync(ConversationId, text, revision); savedRevision = revision;
            }
        }
        finally { drafts.Release(); }
    }
    private async Task SubmitAsync(bool retry)
    {
        if (!CanSend || (!retry && string.IsNullOrWhiteSpace(InputText))) return;
        IsAccepting = true; ErrorMessage = null;
        var accepted = false;
        try
        {
            await FlushDraftAsync();
            if (retry) await coordinator.RetryAsync(ConversationId); else await coordinator.SendAsync(ConversationId, InputText);
            accepted = needsAcceptedReload = true;
            if (!retry)
            {
                // Acceptance is already durable and may have dispatched a request. Do not
                // leave submitted text looking unsent if the following display reads fail.
                loading = true; try { InputText = ""; } finally { loading = false; }
            }
            await SynchronizeAcceptedAsync();
        }
        catch (Exception error) when (!accepted && (error is ArgumentException or InvalidOperationException)) { ErrorMessage = error.Message; }
        catch
        {
            ErrorMessage = accepted ? "消息已发送，显示更新失败；请重试保存以重新加载，请勿重复发送。"
                : "发送前保存失败，输入已保留；请重试保存后再发送。";
            persistenceFailed();
        }
        finally { IsAccepting = false; Project(); }
    }
    public async Task SynchronizeAcceptedAsync()
    {
        if (!needsAcceptedReload) return;
        // Read both before committing the projection. A second read failure must leave the
        // accepted/reload-needed state intact and must not release the persistence pause.
        var messages = await store.ReadMessagesAsync(ConversationId);
        var draft = await store.LoadDraftAsync(ConversationId);
        foreach (var message in messages)
            if (!Messages.Any(m => m.Id == message.Id)) Messages.Add(new(message));
        loading = true;
        try { InputText = draft.Text; draftRevision = savedRevision = draft.Revision; }
        finally { loading = false; }
        await LoadContextUsageAsync();
        needsAcceptedReload = false; ErrorMessage = null; Project();
    }
    private async Task LoadContextUsageAsync()
    {
        foreach (var batch in Messages.Where(m => m.Role == MessageRole.Assistant).Chunk(256))
        {
            try
            {
                var usage = await store.LoadContextUsageAsync(ConversationId, batch.Select(m => m.Id).ToArray());
                foreach (var message in batch) message.ProjectContextUsage(usage.FirstOrDefault(u => u.AnswerMessageId == message.Id));
            }
            catch { foreach (var message in batch) message.ProjectContextUsage(null, unavailable: true); }
        }
    }
    public void RefreshTitle(string title)
    {
        if (TitleText == storedTitle) TitleText = title;
        storedTitle = title;
    }
    public void Project()
    {
        foreach (var snapshot in coordinator.Snapshots.Where(s => s.ConversationId == ConversationId && s.Kind == GenerationKind.Reply && s.AnswerMessageId is not null))
            Messages.FirstOrDefault(m => m.Id == snapshot.AnswerMessageId)?.Project(snapshot);
        OnPropertyChanged(nameof(HasActiveReply)); OnPropertyChanged(nameof(CanSend)); OnPropertyChanged(nameof(CanRetry)); OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanBranch)); OnPropertyChanged(nameof(CanNavigate)); RefreshSummaryAvailability();
    }
    partial void OnIsAcceptingChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanSend)); OnPropertyChanged(nameof(CanBranch)); OnPropertyChanged(nameof(CanNavigate)); RefreshSummaryAvailability(); }
    public ConversationDraft CurrentDraft => new(InputText, draftRevision);
    public void RestoreDraft(ConversationDraft draft)
    {
        if (draft.Revision <= draftRevision) return;
        loading = true; try { InputText = draft.Text; draftRevision = draft.Revision; } finally { loading = false; }
    }
    public override Task WaitForPendingOperationsAsync() => Task.WhenAll(new[]
        { SendCommand.ExecutionTask, StopCommand.ExecutionTask, RetryCommand.ExecutionTask, RenameCommand.ExecutionTask, GenerateSummaryCommand.ExecutionTask }.OfType<Task>());
    public void Detach() { debounce?.Cancel(); debounce?.Dispose(); debounce = null; }
}
