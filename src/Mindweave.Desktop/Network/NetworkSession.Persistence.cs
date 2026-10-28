using System.Collections.Immutable;
using Mindweave.Application.Network;
using Mindweave.Core.Network;

namespace Mindweave.Desktop.Network;

public sealed partial class NetworkSession
{
    private readonly HashSet<Task> pending = [];
    // Serialize inserts and full snapshots, including their observable completion, within this session.
    private readonly SemaphoreSlim writer = new(1, 1);

    private async Task SaveInitialAsync(Guid id, NodePosition position, long stamp, long epoch)
    {
        var refresh = false;
        await writer.WaitAsync();
        try
        {
            // Queuing is not an attempt: a superseded reader must leave the latest reader free to insert.
            if (!CurrentRead(stamp, epoch) || SavedPositions.ContainsKey(id) || !attemptedInitial.Add(id)) return;
            ++persistenceVersion;
            var result = await store.TrySaveInitialNodePositionAsync(WorkspaceId, id, position);
            if (!Graph.Nodes.Any(n => n.Metadata.Id == id)) return;
            if (result.Status is LayoutWriteStatus.Applied or LayoutWriteStatus.AlreadyExists)
            {
                var committed = result.Status == LayoutWriteStatus.AlreadyExists ? result.ExistingPosition!.Value : position;
                SavedPositions = SavedPositions.SetItem(id, committed);
                if (!IsDirty && Positions.GetValueOrDefault(id) != committed) { Positions = Positions.SetItem(id, committed); ++Revision; }
                NotifyGraphState();
            }
            else refresh = result.Status == LayoutWriteStatus.NodeMissing;
        }
        catch { /* Initial placement is best effort; a genuine next entry retries it. */ }
        finally { ++persistenceVersion; writer.Release(); }
        // New missing nodes may need the writer too; refresh only after releasing it.
        if (refresh && CurrentRead(stamp, epoch)) await ReloadAsync();
    }

    /// <returns>True only if this captured snapshot committed (newer edits may still be dirty).</returns>
    public Task<bool> SaveAsync()
    {
        if (IsQuiesced || IsSaving) return Task.FromResult(false);
        if (!IsDirty) return Task.FromResult(true);
        var positions = Positions; var ids = Graph.Nodes.Select(n => n.Metadata.Id).ToImmutableArray(); var revision = Revision; var epoch = lifetime;
        return TrackAsync(async () =>
        {
            IsSaving = true;
            var membershipChanged = false;
            try
            {
                await writer.WaitAsync();
                try
                {
                    ++persistenceVersion;
                    var result = await store.SaveLayoutAsync(WorkspaceId, positions, ids);
                    if (result.Status != LayoutWriteStatus.Applied)
                    {
                        membershipChanged = result.Status == LayoutWriteStatus.MembershipChanged;
                        saveError = "布局保存失败，改动仍保留在当前窗口。";
                        return false;
                    }
                    var valid = Graph.Nodes.Select(n => n.Metadata.Id).ToHashSet();
                    SavedPositions = SavedPositions.SetItems(positions.Where(p => valid.Contains(p.Key)));
                    if (Revision == revision) IsDirty = false;
                    saveError = null;
                    return true;
                }
                finally { ++persistenceVersion; writer.Release(); }
            }
            catch { saveError = "布局保存失败，改动仍保留在当前窗口。"; return false; }
            finally
            {
                NotifyGraphState(); IsSaving = false;
                if (membershipChanged && !IsQuiesced && epoch == lifetime) await ReloadAsync();
            }
        });
    }

    /// <summary>Reject new work and invalidate late reads/calculations. Accepted writes still finish.</summary>
    public void Quiesce()
    {
        if (IsQuiesced) return;
        IsQuiesced = true; ++lifetime; ++reloadVersion; ++arrangeVersion; IsLoading = false;
    }

    /// <summary>Cancel a close attempt; permits new work without reviving pre-quiesce reads/calculations.</summary>
    public void Resume()
    {
        if (!IsQuiesced) return;
        ++lifetime; IsQuiesced = false;
    }

    /// <summary>Await all accepted reads, calculations and writes. Quiesce first for a disposal barrier.</summary>
    public async Task WaitForPendingOperationsAsync()
    {
        while (pending.Count > 0) await Task.WhenAll(pending.ToArray());
    }

    private async Task TrackAsync(Func<Task> operation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Add(completion.Task);
        try { await operation(); }
        finally { pending.Remove(completion.Task); completion.SetResult(); }
    }

    private async Task<T> TrackAsync<T>(Func<Task<T>> operation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Add(completion.Task);
        try { return await operation(); }
        finally { pending.Remove(completion.Task); completion.SetResult(); }
    }
}
