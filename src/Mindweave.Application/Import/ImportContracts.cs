using Mindweave.Application.Exchange;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Import;

/// <summary>Why a file is not imported. A refusal changes nothing (docs/design/sharing-import.md §7).</summary>
public enum ImportRefusal
{
    /// <summary>No Mindweave share at all.</summary>
    NotAShare,

    /// <summary>A share whose contents cannot be true, or cannot be read.</summary>
    Damaged,

    /// <summary>A format version this app does not know.</summary>
    NewerFormat,

    /// <summary>A copy of a workspace for another device, which this app cannot take yet.</summary>
    DeviceCopy,

    /// <summary>A share with conversation content, which this version cannot take yet (increment 2b).</summary>
    WithContent,

    TooLarge,

    /// <summary>The file could not be opened or read.</summary>
    Unreadable,
}

/// <summary>A shared file as it was read: the document, or why it is refused.</summary>
public sealed record SharedFileReading(ExchangeDocument? Document, ImportRefusal? Refusal)
{
    public static SharedFileReading Of(ExchangeDocument document) => new(document, null);

    public static SharedFileReading Refused(ImportRefusal refusal) => new(null, refusal);
}

/// <summary>Reads a shared file's data block; the page in it is never run (architecture §16.4).</summary>
public interface ISharedFileReader
{
    Task<SharedFileReading> ReadAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Sizes a shared file may have. Adjustable constants. The depth bounds the context rows, which are one per ancestor, and the
/// recursion of whatever walks the tree; no path of real questions comes near it.
/// </summary>
public static class ImportLimits
{
    /// <summary>The same as the largest file that can be attached.</summary>
    public const long MaxFileBytes = Resources.ResourceLimits.MaxOriginalBytes;

    public const int MaxConversations = 10_000;

    public const int MaxLinks = 100_000;

    /// <summary>Conversations from a root down to its deepest branch, both included.</summary>
    public const int MaxDepth = 200;

    /// <summary>Characters of a name. The app's own titles are far shorter; a share's title is typed and has no limit of its own.</summary>
    public const int MaxNameLength = 2_000;

    /// <summary>Start times outside these years did not come from the app and would stretch the timeline beyond use.</summary>
    public const int FirstYear = 2000;

    public const int LastYear = 2100;
}

/// <summary>What of the file lives in another workspace, with the branches that therefore cannot be imported either.</summary>
public sealed record ImportSkipped(WorkspaceId Workspace, int Conversations);

/// <summary>What an import into one workspace would do.</summary>
/// <param name="New">The conversations to create, each after its parent.</param>
public sealed record ImportPlan(IReadOnlyList<ExchangeConversation> New, int AlreadyHere, IReadOnlyList<ImportSkipped> Skipped);

/// <summary>Where to import: an existing workspace, or a new one that is created in the same transaction.</summary>
public sealed record ImportTarget(WorkspaceId Id, Workspace? Create)
{
    public static ImportTarget Existing(WorkspaceId id) => new(id, null);

    public static ImportTarget New(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return new(workspace.Id, workspace);
    }
}

/// <summary>Which shared file a conversation came from, and when.</summary>
public sealed record ImportMark(string Title, DateTimeOffset At);

public enum ImportStatus
{
    Imported,

    /// <summary>Nothing of the file is new in the chosen workspace; nothing was written.</summary>
    NothingNew,

    /// <summary>The chosen workspace was deleted meanwhile; nothing was written.</summary>
    WorkspaceMissing,

    /// <summary>What the file hangs off was deleted meanwhile; nothing was written.</summary>
    DoesNotFit,
}

/// <param name="Opens">The conversation to show first: the first new root, or the first new one under what was here.</param>
/// <param name="Parents">The conversations that were here and gained a branch.</param>
public sealed record ImportResult(ImportStatus Status, IReadOnlyList<ConversationId> Created, IReadOnlyList<ConversationId> Parents, ConversationId? Opens)
{
    public static ImportResult Nothing(ImportStatus status) => new(status, [], [], null);
}

/// <summary>Import persistence. Every method is one transaction (writes) or one snapshot (reads).</summary>
public interface IImportStore
{
    /// <summary>The workspace of each of <paramref name="conversations"/> that exists.</summary>
    Task<IReadOnlyDictionary<ConversationId, WorkspaceId>> HoldingsAsync(IReadOnlyCollection<ConversationId> conversations, CancellationToken cancellationToken);

    /// <summary>
    /// Writes what is new, compared again inside the transaction. Only adds: nothing that exists is changed or removed
    /// (decision I2).
    /// </summary>
    Task<ImportResult> ImportAsync(ExchangeDocument document, ImportTarget target, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The shared file a conversation came from; null for the user's own.</summary>
    Task<ImportMark?> MarkOfAsync(ConversationId conversation, CancellationToken cancellationToken);
}
