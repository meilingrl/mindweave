using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.SummaryLinks;

/// <summary>
/// User-made content links between two summaries (requirements §10.3.3), as the network and the summary page use them.
/// Links never change exploration sources, the arrangement, conversation context, summary bodies, read states or the
/// summary count; they only connect two existing summaries of one workspace.
/// </summary>
public interface ISummaryLinks
{
    /// <summary>A link of the workspace was made, changed or deleted, or 显示关联 changed. Raised on worker threads.</summary>
    event EventHandler<WorkspaceId>? Changed;

    Task<IReadOnlyList<SummaryLink>> ListAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    Task<IReadOnlyList<SummaryLinkEntry>> ListForNodeAsync(SummaryNodeId node, CancellationToken cancellationToken);

    /// <summary>What "关联到…" offers from <paramref name="node"/>: every other summary of its workspace, linked ones marked.</summary>
    Task<IReadOnlyList<SummaryLinkCandidate>> CandidatesAsync(SummaryNodeId node, CancellationToken cancellationToken);

    /// <summary>
    /// Links two summaries with an optional note. Refused (<see cref="SummaryLinkRefusedException"/>, nothing written) for the
    /// same summary, summaries of two workspaces, a pair already linked in either order, a note over 16 characters once
    /// trimmed, or a summary that is gone; and while storage is paused (<see cref="StorageWriteException"/>).
    /// </summary>
    Task<SummaryLink> CreateAsync(SummaryNodeId first, SummaryNodeId second, string? note, CancellationToken cancellationToken);

    /// <summary>Changes the note (empty for none); refused as for <see cref="CreateAsync"/>.</summary>
    Task<SummaryLink> EditNoteAsync(SummaryLinkId link, string? note, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes only this link (confirmed once, no backup); the pair is then never suggested again. False when it was gone.
    /// </summary>
    Task<bool> DeleteAsync(SummaryLinkId link, CancellationToken cancellationToken);

    /// <summary>How many links deleting these summaries would remove (for the deletion confirmations, requirements §8.2).</summary>
    Task<int> CountTouchingAsync(IReadOnlyCollection<SummaryNodeId> nodes, CancellationToken cancellationToken);

    /// <summary>Whether the network hides the link edges of the workspace (显示关联 off); remembered across restarts.</summary>
    Task<bool> AreHiddenAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    /// <summary>显示关联 / 隐藏关联: display only, takes effect for this run even when it cannot be remembered.</summary>
    Task SetHiddenAsync(WorkspaceId workspace, bool hidden, CancellationToken cancellationToken);
}

public sealed class SummaryLinkService(ISummaryLinkStore store, IPreferenceStore preferences, StorageHealth health, IClock clock) : ISummaryLinks
{
    /// <summary>Preference key (with the workspace appended) holding "1" while the network hides links; absent when shown.</summary>
    public const string HiddenKeyPrefix = "summary.links.hidden:";

    private readonly Lock _lock = new();
    private readonly Dictionary<WorkspaceId, bool> _hidden = [];
    private readonly HashSet<WorkspaceId> _forgotten = [];

    public event EventHandler<WorkspaceId>? Changed;

    public Task<IReadOnlyList<SummaryLink>> ListAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        store.ListAsync(workspace, cancellationToken);

    public Task<IReadOnlyList<SummaryLinkEntry>> ListForNodeAsync(SummaryNodeId node, CancellationToken cancellationToken) =>
        store.ListForNodeAsync(node, cancellationToken);

    public Task<IReadOnlyList<SummaryLinkCandidate>> CandidatesAsync(SummaryNodeId node, CancellationToken cancellationToken) =>
        store.CandidatesAsync(node, cancellationToken);

    public async Task<SummaryLink> CreateAsync(SummaryNodeId first, SummaryNodeId second, string? note, CancellationToken cancellationToken)
    {
        if (first == second)
        {
            throw new SummaryLinkRefusedException(SummaryLinkRefusal.Self);
        }

        var text = CheckNote(note);
        var link = await CommitAsync(() => store.AddAsync(first, second, text, SummaryLinkOrigin.User, clock.UtcNow, cancellationToken)).ConfigureAwait(false);
        Changed?.Invoke(this, link.Workspace);
        return link;
    }

    public async Task<SummaryLink> EditNoteAsync(SummaryLinkId link, string? note, CancellationToken cancellationToken)
    {
        var text = CheckNote(note);
        var changed = await CommitAsync(() => store.SetNoteAsync(link, text, cancellationToken)).ConfigureAwait(false);
        Changed?.Invoke(this, changed.Workspace);
        return changed;
    }

    public async Task<bool> DeleteAsync(SummaryLinkId link, CancellationToken cancellationToken)
    {
        var deleted = await CommitAsync(() => store.DeleteAsync(link, clock.UtcNow, cancellationToken)).ConfigureAwait(false);
        if (deleted is null)
        {
            return false;
        }

        Changed?.Invoke(this, deleted.Workspace);
        return true;
    }

    public Task<int> CountTouchingAsync(IReadOnlyCollection<SummaryNodeId> nodes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        return nodes.Count == 0 ? Task.FromResult(0) : store.CountTouchingAsync(nodes, cancellationToken);
    }

    public async Task<bool> AreHiddenAsync(WorkspaceId workspace, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_hidden.TryGetValue(workspace, out var known))
            {
                return known;
            }
        }

        var hidden = await preferences.GetAsync(HiddenKeyPrefix + workspace, cancellationToken).ConfigureAwait(false) == "1";
        lock (_lock)
        {
            // A choice made while this read ran wins over what was stored before it.
            return _hidden.TryAdd(workspace, hidden) ? hidden : _hidden[workspace];
        }
    }

    public async Task SetHiddenAsync(WorkspaceId workspace, bool hidden, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _hidden[workspace] = hidden;
            _forgotten.Remove(workspace);
        }

        Changed?.Invoke(this, workspace);
        var key = HiddenKeyPrefix + workspace;
        if (hidden)
        {
            await preferences.SetAsync(key, "1", cancellationToken).ConfigureAwait(false);
            bool forgotten;
            lock (_lock)
            {
                forgotten = _forgotten.Contains(workspace);
            }

            // The workspace was deleted while this was written: the late key goes too.
            if (forgotten)
            {
                await preferences.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await preferences.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A link of the workspace was made elsewhere (a suggestion accepted, <see cref="LinkSuggestionService"/>).</summary>
    internal void RaiseChanged(WorkspaceId workspace) => Changed?.Invoke(this, workspace);

    /// <summary>The workspace was deleted: its links went with the commit (v13 cascade); its remembered 显示关联 goes too.</summary>
    public async Task ForgetAsync(WorkspaceId workspace)
    {
        lock (_lock)
        {
            _hidden.Remove(workspace);
            _forgotten.Add(workspace);
        }

        await preferences.RemoveAsync(HiddenKeyPrefix + workspace, CancellationToken.None).ConfigureAwait(false);
    }

    private static string? CheckNote(string? note)
    {
        if (SummaryLinkNote.IsTooLong(note))
        {
            throw new SummaryLinkRefusedException(SummaryLinkRefusal.NoteTooLong);
        }

        return SummaryLinkNote.Normalize(note);
    }

    /// <summary>Like the other non-AI commits: refused while storage is paused, and a storage failure pauses it.</summary>
    private async Task<T> CommitAsync<T>(Func<Task<T>> commit)
    {
        health.ThrowIfBlocked();
        try
        {
            return await commit().ConfigureAwait(false);
        }
        catch (StorageWriteException ex)
        {
            health.Report(ex);
            throw;
        }
    }
}
