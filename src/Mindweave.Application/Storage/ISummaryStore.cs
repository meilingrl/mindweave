using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Storage;

/// <summary>A node with its current version and what its markers need, for lists and the conversation header.</summary>
public sealed record SummaryEntry(SummaryNode Node, SummaryVersion Current, SummaryMarkers Markers);

/// <summary>Everything a summary page shows for one node.</summary>
public sealed record SummaryDetail(
    SummaryNode Node,
    IReadOnlyList<SummaryVersion> Versions,
    IReadOnlyDictionary<SummaryVersionId, SummaryReadState> ReadStates,
    SummaryEditDraft? Draft,
    int? LastFinishedSeq,
    bool HasUnfinishedTask)
{
    public SummaryVersion Current => Versions.Single(v => v.Id == Node.Current);

    public SummaryVersion? Pending => Node.Pending is { } id ? Versions.Single(v => v.Id == id) : null;

    /// <summary>Saved versions in creation order; the candidate is not a saved version.</summary>
    public IReadOnlyList<SummaryVersion> Saved => [.. Versions.Where(v => v.Id != Node.Pending).OrderBy(v => v.Seq)];

    public SummaryReadState ReadState(SummaryVersionId version) => ReadStates.GetValueOrDefault(version) ?? SummaryReadState.Unread(version);

    public SummaryMarkers Markers => SummaryRules.Markers(Node, Current, ReadStates.GetValueOrDefault(Node.Current), LastFinishedSeq);
}

/// <summary>Where a successful summary went: a new node (first version) or a candidate of an existing node.</summary>
public sealed record SummarySaved(SummaryNodeId Node, SummaryVersionId Version, bool IsCandidate);

/// <summary>Why a version operation was refused; nothing was changed.</summary>
public enum SummaryRefusal
{
    /// <summary>The node, or a version it was asked about, no longer is what the caller saw.</summary>
    Changed,

    /// <summary>An unsaved edit exists and must be saved or discarded first.</summary>
    HasDraft,

    /// <summary>A summary task for this conversation has not finished.</summary>
    TaskRunning,

    /// <summary>Deleting every saved version while a candidate waits; it must be adopted or discarded first.</summary>
    HasCandidate,

    /// <summary>The candidate cannot be deleted as a version; it is discarded instead.</summary>
    IsCandidate,
}

public sealed class SummaryRefusedException(SummaryRefusal reason) : Exception(reason.ToString())
{
    public SummaryRefusal Reason { get; } = reason;
}

/// <summary>Result of deleting versions: the new current version, or null when the node itself was deleted.</summary>
public sealed record VersionsDeleted(SummaryVersionId? NewCurrent);

/// <summary>Expected state of one node when committing a naming result; any difference discards that node's name.</summary>
public sealed record NamingExpectation(SummaryNodeId Node, SummaryVersionId Current, string DisplayName);

/// <summary>
/// Summary persistence (architecture §8.2). Every method is one transaction; operations re-check their
/// preconditions inside it and throw <see cref="SummaryRefusedException"/> without changing anything.
/// </summary>
public interface ISummaryStore
{
    Task<IReadOnlyList<SummaryEntry>> ListAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    Task<SummaryEntry?> ForConversationAsync(ConversationId conversation, CancellationToken cancellationToken);

    Task<SummaryDetail?> GetAsync(SummaryNodeId node, CancellationToken cancellationToken);

    /// <summary>
    /// Saves a successful summary and completes its task together (MW-C06): the first version of a new node, or a
    /// candidate of the existing one. Null when the task is no longer running (stopped or interrupted meanwhile).
    /// </summary>
    Task<SummarySaved?> SaveGeneratedAsync(GenerationTaskId task, ConversationId conversation, string title, string body, int toSeq, SummaryVersionId? baseline, string model, DateTimeOffset at, CancellationToken cancellationToken);

    Task AdoptAsync(SummaryNodeId node, SummaryVersionId candidate, DateTimeOffset at, CancellationToken cancellationToken);

    Task DiscardAsync(SummaryNodeId node, SummaryVersionId candidate, DateTimeOffset at, CancellationToken cancellationToken);

    Task SetCurrentAsync(SummaryNodeId node, SummaryVersionId version, DateTimeOffset at, CancellationToken cancellationToken);

    Task<VersionsDeleted> DeleteVersionsAsync(SummaryNodeId node, IReadOnlySet<SummaryVersionId> versions, DateTimeOffset at, CancellationToken cancellationToken);

    Task SaveDraftAsync(SummaryEditDraft draft, CancellationToken cancellationToken);

    Task DeleteDraftAsync(SummaryNodeId node, CancellationToken cancellationToken);

    /// <summary>Saves an edit of <paramref name="baseline"/> as the new current version, read; removes the draft. Null when nothing changed.</summary>
    Task<SummaryVersionId?> SaveEditAsync(SummaryNodeId node, SummaryVersionId baseline, string title, string body, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Sets or clears the read mark of a version; clearing keeps the reached progress.</summary>
    Task SetReadAsync(SummaryVersionId version, ReadSource? source, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Records reading progress and position; never lowers the maximum, never clears a read mark.</summary>
    Task RecordProgressAsync(SummaryVersionId version, double progress, string? locator, bool markRead, DateTimeOffset at, CancellationToken cancellationToken);

    Task RenameAsync(SummaryNodeId node, string displayName, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// Applies automatic names to nodes whose state still matches; manual names are never changed. Returns the
    /// nodes that were renamed.
    /// </summary>
    Task<IReadOnlyList<SummaryNodeId>> ApplyNamesAsync(IReadOnlyList<(NamingExpectation Expected, string DisplayName)> names, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Resets the base title and (unless manual) the display name after the current version changed.</summary>
    Task UpdateBaseTitleAsync(SummaryNodeId node, SummaryVersionId current, string baseTitle, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The latest summary task of a conversation, to show progress or the last failure.</summary>
    Task<SummaryTaskStatus?> LatestTaskAsync(ConversationId conversation, CancellationToken cancellationToken);
}

public sealed record SummaryTaskStatus(GenerationTaskId Id, GenerationTaskState State, GenerationErrorKind? Error, string? Detail, DateTimeOffset CreatedAt);
