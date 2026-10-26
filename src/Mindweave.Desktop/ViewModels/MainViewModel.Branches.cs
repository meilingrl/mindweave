using Mindweave.Application.Branches;
using Mindweave.Application.Navigation;
using Mindweave.Core.Branches;
using Mindweave.Core.Chat;

namespace Mindweave.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private IBranchStore? BranchStore => store as IBranchStore;
    private sealed record PendingBranch(Guid ParentId, SourceSelection Selection, Guid OperationId, ConversationReadState ReadState, Guid? ChildId = null);
    private PendingBranch? pendingBranch;
    private bool submittingBranch;
    private readonly Dictionary<Guid, Guid?> branchParents = [];
    private readonly Dictionary<Guid, ConversationReadState> unsavedReads = [];
    private readonly SemaphoreSlim readingWrites = new(1, 1);
    private CancellationTokenSource? readDebounce;

    public async Task CreateBranchAsync(SourceSelection selection)
    {
        // Freeze the caller page before awaiting the main navigation gate. A queued double-click
        // cannot reinterpret its old source as a selection from the newly opened child.
        if (submittingBranch || IsBusy || closing || ActiveContent is not ChatViewModel parent || BranchStore is null || coordinator is null) return;
        submittingBranch = true;
        try
        {
            await RunAsync(async () =>
            {
                RequireMutation();
                if (ActiveContent != parent) throw new ArgumentException("选区所在会话已经切换。");
                await parent.WaitForPendingOperationsAsync();
                await coordinator.FlushPendingAsync(); RequireMutation(); parent.Project();
                var source = parent.Messages.SingleOrDefault(m => m.Id == selection.MessageId);
                if (source?.CanCreateBranch != true || source.Revision != selection.MessageRevision)
                    throw new ArgumentException("请选择已经保存的非空终态 AI 回答。");
                parent.CaptureReadingNow();
                parent.RecordReading(parent.ReadState.Location);
                pendingBranch ??= new(parent.ConversationId, selection, Guid.NewGuid(), parent.ReadState);
                if (pendingBranch.ParentId != parent.ConversationId || pendingBranch.Selection != selection)
                    throw new ArgumentException("上次分支尚未完成，请先重试保存。");
                Conversation child;
                try
                {
                    child = await BranchStore.CreateBranchAsync(parent.ConversationId, selection, pendingBranch.OperationId,
                        pendingBranch.ReadState.Revision > 0 ? pendingBranch.ReadState : null);
                }
                catch (ArgumentException) { pendingBranch = null; throw; }
                pendingBranch = pendingBranch with { ChildId = child.Id };
                await CompletePendingBranchAsync();
            });
        }
        finally { submittingBranch = false; }
    }
    private async Task CompletePendingBranchAsync()
    {
        if (pendingBranch is null || BranchStore is null || chatStore is null) return;
        try
        {
            if (pendingBranch.ChildId is null)
            {
                // A writer can commit before its caller observes success. Replay the exact
                // accepted operation, never mint another identity during save repair.
                var replay = await BranchStore.CreateBranchAsync(pendingBranch.ParentId, pendingBranch.Selection,
                    pendingBranch.OperationId, pendingBranch.ReadState.Revision > 0 ? pendingBranch.ReadState : null);
                pendingBranch = pendingBranch with { ChildId = replay.Id };
            }
            var child = await chatStore.LoadConversationAsync(pendingBranch.ChildId.Value)
                ?? throw new InvalidDataException("已保存分支无法读取。");
            await FlushCurrentAsync();
            // Reading remains available during a pause. If the user moved to another
            // workspace, repair the accepted child without stealing that workspace's page.
            if (child.WorkspaceId == CurrentWorkspace?.Id)
                await OpenRequestAsync(new(MainArea.Explore, ContentKind.Conversation, child.Id), strictSave: true);
            await RefreshConversationsAsync(); pendingBranch = null;
        }
        catch { MarkPersistenceFailure(); throw; }
    }

    public Task ReturnToSourceAsync(Guid childConversationId) => RunAsync(async () =>
    {
        if (BranchStore is null || chatStore is null || CurrentWorkspace is null) return;
        var child = await chatStore.LoadConversationAsync(childConversationId);
        var source = await BranchStore.LoadBranchSourceAsync(childConversationId);
        if (child?.WorkspaceId != CurrentWorkspace.Id || source is null) throw new ArgumentException("当前会话没有可返回的来源。");
        var message = (await chatStore.ReadMessagesAsync(source.ParentConversationId)).SingleOrDefault(m => m.Id == source.MessageId)
            ?? throw new ArgumentException("来源消息不存在。");
        var selection = new SourceSelection(message.Id, message.Revision,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(message.Body))), source.Quote, source.Anchor);
        await FlushForNavigationAsync();
        await OpenRequestAsync(new(MainArea.Explore, ContentKind.Conversation, source.ParentConversationId, NavigationIntent.ReturnToSource,
            new(childConversationId, source.ParentConversationId, selection)));
    });
    private void ReadingChanged(ChatViewModel page, ConversationReadState state)
    {
        if (ActiveContent != page || detached) return;
        unsavedReads[state.ConversationId] = state;
        readDebounce?.Cancel(); readDebounce?.Dispose(); readDebounce = new();
        _ = SaveReadingAfterDelayAsync(readDebounce.Token);
    }
    private async Task SaveReadingAfterDelayAsync(CancellationToken token)
    {
        try { await Task.Delay(400, token); await FlushReadsAsync(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { MarkPersistenceFailure(); ErrorMessage = "阅读位置保存失败，位置仍在内存中，请重试保存。"; }
    }
    private async Task FlushReadsAsync()
    {
        if (BranchStore is null) return;
        await readingWrites.WaitAsync();
        try
        {
            foreach (var (id, state) in unsavedReads.ToArray())
            {
                await BranchStore.SaveReadStateAsync(state);
                if (unsavedReads.GetValueOrDefault(id) == state) unsavedReads.Remove(id);
            }
        }
        finally { readingWrites.Release(); }
    }
    private static NavigationRequest HistoryRequest(NavigationRequest request) => request with { Intent = NavigationIntent.Open, Source = null };
}
