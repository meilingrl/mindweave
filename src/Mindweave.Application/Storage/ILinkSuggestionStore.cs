using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Storage;

/// <summary>What accepting a suggestion did: the link made, or why none (its workspace is known either way when it existed).</summary>
public sealed record LinkSuggestionAcceptance(SummaryLink? Link, SummaryLinkRefusal? Refused, WorkspaceId? Workspace);

/// <summary>
/// AI-suggested links kept per workspace (requirements §10.3.4, migration v13's link_suggestion). Every method is one
/// transaction that checks what it needs inside it. Suggestions go with either summary and with the workspace by cascade,
/// so nothing here deletes them for those; a deleted link's pair is recorded as ignored by <see cref="ISummaryLinkStore"/>.
/// </summary>
public interface ILinkSuggestionStore
{
    /// <summary>The workspace's pending suggestions with both summaries' shown names, oldest batch first, in batch order.</summary>
    Task<IReadOnlyList<LinkSuggestion>> ListPendingAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    /// <summary>
    /// The pairs a new request must not suggest, in the links' order: every link of the workspace (made by the user or
    /// confirmed) and every ignored suggestion (ignored, or a link the user deleted). Pending ones are not among them: a new
    /// request replaces them and may suggest the same pair again.
    /// </summary>
    Task<IReadOnlyList<(SummaryNodeId A, SummaryNodeId B)>> ListAvoidedAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a batch as pending (a pair already pending takes the new note and reason). Nothing is stored when the workspace
    /// no longer exists (a late result: null). A pair is left out when either summary is gone or not in the workspace, the
    /// pair is linked or ignored meanwhile, or it repeats one before it. Only when at least one pair was stored do the
    /// workspace's other pending suggestions go (simply removed, not remembered); otherwise they stay. Returns how many.
    /// </summary>
    Task<int?> AddBatchAsync(WorkspaceId workspace, LinkSuggestionBatch batch, CancellationToken cancellationToken);

    /// <summary>
    /// Accepts a pending suggestion with <paramref name="note"/> (already checked): it becomes a link with origin
    /// <see cref="SummaryLinkOrigin.Confirmed"/> and the suggestion row goes, in one transaction. Refused with
    /// <see cref="SummaryLinkRefusal.Missing"/> when it is no longer pending (nothing written), and with
    /// <see cref="SummaryLinkRefusal.Duplicate"/> when the pair was linked meanwhile (then the stale suggestion is removed).
    /// </summary>
    Task<LinkSuggestionAcceptance> AcceptAsync(LinkSuggestionId suggestion, string? note, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>A pending suggestion becomes ignored, kept without its reason; false when it was not pending (or is gone).</summary>
    Task<bool> IgnoreAsync(LinkSuggestionId suggestion, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Every pending suggestion of the workspace becomes ignored (全部忽略); returns how many.</summary>
    Task<int> IgnoreAllAsync(WorkspaceId workspace, DateTimeOffset at, CancellationToken cancellationToken);
}
