using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Storage;

/// <summary>One conversation on the way from the root to a branch, with the quote it was branched on.</summary>
public sealed record LineageEntry(Conversation Conversation, BranchSource? Source);

/// <summary>Branch persistence. Every method is one transaction (writes) or one snapshot (reads).</summary>
public interface IBranchStore
{
    /// <summary>
    /// Creates the child conversation, its source record and its frozen ancestor ranges together, after checking
    /// inside the transaction that the source is a finished answer of <paramref name="parent"/> with saved text.
    /// The child takes the parent's workspace. Throws <see cref="BranchRejectedException"/> otherwise.
    /// </summary>
    Task<Conversation> CreateAsync(ConversationId parent, ConversationId child, BranchSelection selection, DateTimeOffset at, CancellationToken cancellationToken);

    Task<BranchSource?> GetSourceAsync(ConversationId child, CancellationToken cancellationToken);

    /// <summary>Branches created from <paramref name="parent"/>, oldest first: the entries shown under its answers.</summary>
    Task<IReadOnlyList<BranchSource>> ListChildrenAsync(ConversationId parent, CancellationToken cancellationToken);

    /// <summary>The ancestors' frozen context, root first, each cut to its range; empty for a root conversation.</summary>
    Task<IReadOnlyList<ContextSegment>> GetAncestorContextAsync(ConversationId conversation, CancellationToken cancellationToken);

    /// <summary>The conversation and its ancestors, root first, for showing where a branch sits.</summary>
    Task<IReadOnlyList<LineageEntry>> GetLineageAsync(ConversationId conversation, CancellationToken cancellationToken);
}

public sealed class BranchRejectedException : Exception
{
    public BranchRejectedException()
        : this(BranchRejection.SourceNotFound)
    {
    }

    public BranchRejectedException(BranchRejection reason)
        : base($"The branch cannot be created: {reason}.") => Reason = reason;

    public BranchRejectedException(string message)
        : base(message)
    {
    }

    public BranchRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public BranchRejection Reason { get; }
}
