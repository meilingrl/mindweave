using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.ViewModels;

/// <summary>Main owns one draft memory per node across pages. Invalid in-progress text is retained verbatim.</summary>
public sealed partial class SummaryDraftSession(ISummaryVersionStore store, Guid nodeId, Action failed) : ObservableObject
{
    private readonly SemaphoreSlim writes = new(1, 1);
    private SummaryEditDraft? saved;
    private bool projecting;
    private sealed record PendingCommit(SummaryNodeToken Node, SummaryDraftToken Draft, SummaryContent Content, long LocalRevision);
    private PendingCommit? pendingCommit;
    private (SummaryDraftToken Token, SummaryContent Content, long Revision)? pendingSave;
    private Func<Guid, Task>? currentChanged;
    public void ConfigureCurrentChanged(Func<Guid, Task>? handler) { currentChanged ??= handler; }
    private long revision, persisted;
    private CancellationTokenSource? debounce;
    [ObservableProperty] private string title = "";
    [ObservableProperty] private string markdown = "";
    public bool HasMemoryChanges => revision != persisted;
    public bool HasPendingCommit => pendingCommit is not null || pendingDiscard is not null;
    public bool IsDirty => HasMemoryChanges || saved?.IsDirty == true;
    public SummaryDraftToken? Token => saved?.Token;
    public string? Error { get; private set; }
    partial void OnTitleChanged(string value) => Changed();
    partial void OnMarkdownChanged(string value) => Changed();
    private void Changed()
    {
        if (projecting) return;
        revision++; OnPropertyChanged(nameof(IsDirty));
        if (operationPaused || HasRemovedNodeInput) return;
        debounce?.Cancel(); debounce?.Dispose(); debounce = new(); _ = SaveDelayedAsync(debounce.Token);
    }
    private async Task SaveDelayedAsync(CancellationToken cancellation)
    {
        try { await Task.Delay(300, cancellation); await FlushAsync(); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch { /* Flush reports failure and retains exact text. Main owns repair. */ }
    }
    public async Task BeginAsync()
    {
        await writes.WaitAsync();
        try
        {
            if (saved?.Title is not null) return;
            var node = await store.LoadSummaryNodeAsync(nodeId) ?? throw new InvalidOperationException("总结已删除。");
            var result = await store.BeginSummaryEditAsync(node.State.Token);
            if (result.Kind is not (SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange) || result.Value?.Title is null)
                throw new InvalidOperationException("当前版本已经变化，请重新打开总结。");
            saved = result.Value;
            projecting = true; try { Title = saved.Title; Markdown = saved.Markdown!; revision = persisted = 0; } finally { projecting = false; }
        }
        finally { writes.Release(); }
    }
    public async Task FlushAsync()
    {
        if (HasRemovedNodeInput) throw new SummaryInputResolutionRequiredException();
        debounce?.Cancel(); await writes.WaitAsync();
        try
        {
            if (pendingDiscard is not null) await RepairDiscardCoreAsync();
            if (pendingCommit is not null) await RepairCommitCoreAsync();
            while (pendingSave is not null || (HasMemoryChanges && saved is not null))
            {
                pendingSave ??= (saved!.Token, new(Title, Markdown), revision);
                var pending = pendingSave.Value;
                var result = await store.SaveSummaryDraftAsync(pending.Token, pending.Content);
                if (result.Kind is not (SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange))
                {
                    // Reconcile only the exact attempted write before advancing to any newer queued input.
                    var actual = await store.LoadSummaryDraftAsync(nodeId);
                    if (actual?.Token.Epoch != pending.Token.Epoch || actual.Token.BaseVersionId != pending.Token.BaseVersionId ||
                        actual.Token.Revision != pending.Token.Revision + 1 || actual.Title != pending.Content.Title || actual.Markdown != pending.Content.Markdown)
                        throw new InvalidOperationException("编辑版本已变化；输入仍在内存，请先解决草稿。");
                    saved = actual;
                }
                else saved = result.Value!;
                persisted = pending.Revision; pendingSave = null;
            }
            Error = null; OnPropertyChanged(nameof(IsDirty));
        }
        catch { Error = "草稿保存失败，原输入仍保留在窗口中。"; failed(); throw; }
        finally { writes.Release(); }
    }
    public async Task<SummaryOutcome<SummaryVersion>> CommitAsync(SummaryDraftResolution? expected = null)
    {
        await FlushAsync(); await writes.WaitAsync();
        try
        {
            if (expected is not null && (expected.LocalRevision != revision || expected.Title != Title || expected.Markdown != Markdown))
                return new(SummaryOutcomeKind.Conflict, Code: "DraftChangedSinceConfirmation");
            // Freeze the payload and local revision belonging to the durable token before
            // the node read yields. Live editor properties can change during that await.
            var submitted = saved; var submittedRevision = persisted;
            if (submitted?.Title is null || submitted.Markdown is null) return new(SummaryOutcomeKind.NotFound);
            var content = new SummaryContent(submitted.Title, submitted.Markdown);
            var node = await store.LoadSummaryNodeAsync(nodeId);
            if (node is null) return new(SummaryOutcomeKind.NotFound);
            pendingCommit = new(node.State.Token, submitted.Token, content, submittedRevision);
            var result = await store.CommitSummaryDraftAsync(node.State.Token, submitted.Token);
            if (result.Kind is SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange)
                await FinishCommitAsync(result);
            else pendingCommit = null;
            return result;
        }
        catch { failed(); throw; }
        finally { writes.Release(); }
    }
    private async Task RepairCommitCoreAsync()
    {
        var pending = pendingCommit!;
        var node = await store.LoadSummaryNodeAsync(nodeId) ?? throw new InvalidOperationException("总结已变化，尚不能确认手动保存。");
        var draft = await store.LoadSummaryDraftAsync(nodeId);
        if (node.State.Node.CurrentVersionId == pending.Node.CurrentVersionId && draft?.Token == pending.Draft)
        {
            // No durable transition occurred. Replay only the original tokens and intent.
            var replay = await store.CommitSummaryDraftAsync(pending.Node, pending.Draft);
            if (replay.Kind is not (SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange)) throw new InvalidOperationException("手动保存冲突，草稿仍保留。");
            await FinishCommitAsync(replay); return;
        }
        var current = await store.LoadSummaryVersionAsync(node.State.Node.CurrentVersionId);
        var content = KnowledgeSummaryProtocol.ParseComplete(KnowledgeSummaryProtocol.Serialize(pending.Content));
        if (current is null || current.Header.Title != content.Title || current.Markdown != content.Markdown ||
            (current.Header.Identity.Id != pending.Node.CurrentVersionId &&
                (current.Header.Identity.Origin != SummaryVersionOrigin.Manual || current.Header.BaselineVersionId != pending.Node.CurrentVersionId)))
            throw new InvalidOperationException("手动保存结果已经变化，输入仍保留，请先解决版本冲突。");
        await FinishCommitAsync(new(current.Header.Identity.Id == pending.Node.CurrentVersionId ? SummaryOutcomeKind.NoChange : SummaryOutcomeKind.Applied, current));
    }
    private async Task FinishCommitAsync(SummaryOutcome<SummaryVersion> result)
    {
        var pending = pendingCommit!;
        // Naming can yield too; decide whether newer input needs a lease only after it returns.
        if (result.Kind == SummaryOutcomeKind.Applied && currentChanged is not null) await currentChanged(result.Value!.Header.Identity.Id);
        if (revision != pending.LocalRevision)
        {
            // A queued input event can arrive after the Save button disabled its editor.
            // Renew against the new current version, preserving that newer local text verbatim.
            var node = await store.LoadSummaryNodeAsync(nodeId) ?? throw new InvalidOperationException("总结已删除。");
            var begun = await store.BeginSummaryEditAsync(node.State.Token);
            if (begun.Kind is not (SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange)) throw new InvalidOperationException("新草稿无法保存，输入仍在内存。");
            saved = begun.Value; persisted = pending.LocalRevision;
        }
        else { saved = null; revision = persisted = 0; }
        pendingCommit = null; Error = null; OnPropertyChanged(nameof(IsDirty));
    }
    public void StopDebounce() { debounce?.Cancel(); }
    public void ReleaseCleanMemory()
    {
        if (IsDirty || HasRemovedNodeInput || pendingCommit is not null || pendingSave is not null || pendingDiscard is not null) return;
        saved = null; projecting = true; try { Title = Markdown = ""; } finally { projecting = false; }
    }
}
