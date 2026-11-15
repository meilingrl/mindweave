using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Storage;

/// <summary>
/// Content links between summaries (migration v13). Every write is one transaction that checks what it needs inside it
/// and throws <see cref="SummaryLinkRefusedException"/> when it refuses, so nothing is written in part. Links go with
/// either summary and with the workspace by cascade; nothing here deletes them for those.
/// </summary>
public interface ISummaryLinkStore
{
    /// <summary>Every link of the workspace, oldest first.</summary>
    Task<IReadOnlyList<SummaryLink>> ListAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    /// <summary>The links of one summary with the other summary's shown name, oldest first; empty when it is gone.</summary>
    Task<IReadOnlyList<SummaryLinkEntry>> ListForNodeAsync(SummaryNodeId node, CancellationToken cancellationToken);

    /// <summary>Every other summary of the node's workspace, most recently updated first, each marked when already linked to it.</summary>
    Task<IReadOnlyList<SummaryLinkCandidate>> CandidatesAsync(SummaryNodeId node, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a link between two summaries in their normalized order. Refused when either is gone
    /// (<see cref="SummaryLinkRefusal.Missing"/>), they are in different workspaces, or the pair is linked already.
    /// </summary>
    Task<SummaryLink> AddAsync(SummaryNodeId first, SummaryNodeId second, string? note, SummaryLinkOrigin origin, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Replaces the note (null for none); refused with <see cref="SummaryLinkRefusal.Missing"/> when the link is gone.</summary>
    Task<SummaryLink> SetNoteAsync(SummaryLinkId link, string? note, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the link and, in the same transaction, records its pair as an ignored suggestion (requirements §10.3.4:
    /// a pair whose link the user deleted is not suggested again). Returns the link deleted, or null when it was gone.
    /// </summary>
    Task<SummaryLink?> DeleteAsync(SummaryLinkId link, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>How many links touch any of <paramref name="nodes"/> (each counted once): what deleting them takes along.</summary>
    Task<int> CountTouchingAsync(IReadOnlyCollection<SummaryNodeId> nodes, CancellationToken cancellationToken);
}
