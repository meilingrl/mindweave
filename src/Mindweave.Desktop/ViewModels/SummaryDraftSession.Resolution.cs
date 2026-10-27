using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.ViewModels;

public sealed class SummaryInputResolutionRequiredException() : InvalidOperationException("总结节点已删除，但删除确认后到达的未保存输入仍在窗口中。请返回工作台复制输入，并明确确认丢弃这份输入后再退出或离开。");

public sealed record SummaryDraftResolution(SummaryDraftToken? Token, long LocalRevision, string Title, string Markdown);

public sealed partial class SummaryDraftSession
{
    private SummaryDraftResolution? pendingDiscard;
    public bool HasRemovedNodeInput { get; private set; }
    private bool operationPaused, nodeRemoved;
    public event Action? RemovedNodeInputChanged;
    // Retained for this shared session's lifetime, including detached editor callbacks.
    // Deleted storage is never reacquired as a lease for late memory-only input.
    public void MarkNodeRemoved()
    {
        nodeRemoved = true; StopDebounce(); saved = null;
        if (!HasMemoryChanges)
        {
            projecting = true; try { Title = Markdown = ""; } finally { projecting = false; }
        }
        HasRemovedNodeInput = HasMemoryChanges;
        RemovedNodeInputChanged?.Invoke(); OnPropertyChanged(nameof(IsDirty));
    }
    public void PauseForVersionConfirmation(bool paused)
    {
        operationPaused = paused;
        if (paused) StopDebounce();
        else if (HasMemoryChanges && !HasRemovedNodeInput)
        {
            debounce?.Cancel(); debounce?.Dispose(); debounce = new(); _ = SaveDelayedAsync(debounce.Token);
        }
    }
    public SummaryDraftResolution CaptureRemovedInput() => new(saved?.Token, revision, Title, Markdown);
    public bool DiscardRemovedInput(SummaryDraftResolution expected)
    {
        if (!HasRemovedNodeInput || !MatchesResolution(expected)) return false;
        StopDebounce(); saved = null; pendingSave = null; HasRemovedNodeInput = false; revision = persisted = 0;
        projecting = true; try { Title = Markdown = ""; } finally { projecting = false; }
        Error = null; OnPropertyChanged(nameof(IsDirty)); RemovedNodeInputChanged?.Invoke(); return true;
    }
    public bool MatchesResolution(SummaryDraftResolution expected) => expected.Token == saved?.Token && expected.LocalRevision == revision && expected.Title == Title && expected.Markdown == Markdown;
    public async Task<SummaryDraftResolution> CaptureResolutionAsync()
    {
        await writes.WaitAsync();
        try
        {
            if (saved is null && !HasMemoryChanges)
            {
                saved = await store.LoadSummaryDraftAsync(nodeId);
                if (saved?.Title is { } text)
                {
                    projecting = true;
                    try { Title = text; Markdown = saved.Markdown!; } finally { projecting = false; }
                }
            }
            return new(saved?.Token, revision, Title, Markdown);
        }
        finally { writes.Release(); }
    }
    public async Task<bool> DiscardAsync(SummaryDraftResolution expected)
    {
        StopDebounce(); await writes.WaitAsync();
        try
        {
            if (!MatchesResolution(expected) || pendingSave is not null || pendingCommit is not null || expected.Token is null) return false;
            pendingDiscard = expected;
            var result = await store.DiscardSummaryDraftAsync(expected.Token);
            if (result.Kind is not (SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange)) { pendingDiscard = null; return false; }
            await FinishDiscardAsync(result.Value!); return true;
        }
        catch { Error = "丢弃草稿的保存结果尚未确认，原输入保留，请重试保存。"; failed(); throw; }
        finally { writes.Release(); }
    }
    private async Task RepairDiscardCoreAsync()
    {
        var pending = pendingDiscard!;
        var actual = await store.LoadSummaryDraftAsync(nodeId);
        if (actual?.Token == pending.Token)
        {
            var result = await store.DiscardSummaryDraftAsync(pending.Token!);
            if (result.Kind is not (SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange)) throw new InvalidOperationException("草稿丢弃状态发生变化。");
            actual = result.Value;
        }
        else if (actual?.Title is not null || actual?.Token.Epoch != pending.Token!.Epoch + 1 || actual.Token.Revision != pending.Token.Revision + 1)
            throw new InvalidOperationException("尚不能确认原草稿丢弃结果；输入仍在内存。");
        await FinishDiscardAsync(actual!);
    }
    private async Task FinishDiscardAsync(SummaryEditDraft tombstone)
    {
        var pending = pendingDiscard!;
        saved = tombstone;
        if (revision != pending.LocalRevision)
        {
            // A delayed input event was not part of the explicit discard. Keep it under
            // a new lease so the old token cannot resurrect the discarded text.
            var node = await store.LoadSummaryNodeAsync(nodeId) ?? throw new InvalidOperationException("总结已删除，新输入仍在内存。");
            var begin = await store.BeginSummaryEditAsync(node.State.Token);
            if (begin.Kind is not (SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange)) throw new InvalidOperationException("新草稿无法取得编辑许可。");
            saved = begin.Value; persisted = pending.LocalRevision;
        }
        else
        {
            saved = null; revision = persisted = 0; projecting = true;
            try { Title = Markdown = ""; } finally { projecting = false; }
        }
        pendingDiscard = null; Error = null; OnPropertyChanged(nameof(IsDirty));
    }
    public async Task<bool> RebaseAfterCurrentChangeAsync()
    {
        await writes.WaitAsync();
        try
        {
            if (pendingCommit is not null || pendingSave is not null || pendingDiscard is not null) throw new InvalidOperationException("草稿操作尚未完成。");
            if (HasMemoryChanges)
            {
                var node = await store.LoadSummaryNodeAsync(nodeId);
                if (node is null)
                {
                    // Only later user input remains. Never keep the deleted saved-body cache.
                    MarkNodeRemoved(); return false;
                }
                var begin = await store.BeginSummaryEditAsync(node.State.Token);
                if (begin.Kind is not (SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange)) throw new InvalidOperationException("新草稿无法取得编辑许可。");
                saved = begin.Value; // Keep local text and revision. A later Flush persists it.
            }
            else
            {
                saved = null; projecting = true;
                try { Title = Markdown = ""; } finally { projecting = false; }
            }
            OnPropertyChanged(nameof(IsDirty)); return true;
        }
        finally { writes.Release(); }
    }
}
