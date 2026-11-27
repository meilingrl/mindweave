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

    /// <summary>Characters of a conversation's name. The app's own are far shorter; a share's title is typed and has no limit.</summary>
    public const int MaxNameLength = 2_000;

    /// <summary>Messages in one file.</summary>
    public const int MaxMessages = 200_000;

    /// <summary>Characters of an answer or of a summary's text; far beyond what any model writes at once.</summary>
    public const int MaxTextLength = 2_000_000;

    /// <summary>A message's number in its conversation, and how often an answer used one kind of tool.</summary>
    public const int MaxNumber = 1_000_000;

    /// <summary>Citations are written [n] with one to three digits.</summary>
    public const int MaxCitation = 999;

    public const int MaxAddressLength = 16_000;

    /// <summary>Start times outside these years did not come from the app and would stretch the timeline beyond use.</summary>
    public const int FirstYear = 2000;

    public const int LastYear = 2100;
}

/// <summary>What of the file lives in another workspace, with the branches that therefore cannot be imported either.</summary>
public sealed record ImportSkipped(WorkspaceId Workspace, int Conversations);

/// <summary>What an import into one workspace would do.</summary>
/// <param name="New">The conversations to create, each after its parent.</param>
/// <param name="AlreadyHere">How many are here and stay as they are.</param>
public sealed record ImportPlan(IReadOnlyList<ExchangeConversation> New, int AlreadyHere, IReadOnlyList<ImportSkipped> Skipped)
{
    /// <summary>The conversations that are here and gain something, each after its parent.</summary>
    public IReadOnlyList<ImportGain> Gains { get; init; } = [];

    /// <summary>
    /// How many are skipped because what the file says does not fit what is stored: a number held by another message, or an
    /// identity that another conversation's message or summary has. The new branches of a new one are skipped with it.
    /// </summary>
    public int Unfit { get; init; }

    /// <summary>How many are the user's own and are skipped, because the file has messages for them that are not here.</summary>
    public int Own { get; init; }

    /// <summary>The summary links that are added.</summary>
    public IReadOnlyList<ExchangeLink> Links { get; init; } = [];

    /// <summary>Nothing would be written.</summary>
    public bool IsEmpty => New.Count == 0 && Gains.Count == 0 && Links.Count == 0;
}

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

    /// <summary>What the file hangs off was deleted meanwhile, or its path would be too deep there; nothing was written.</summary>
    DoesNotFit,
}

/// <param name="Opens">The conversation to show first: the first new root, the first new one under what was here, or the first that gained something.</param>
/// <param name="Parents">The conversations that were here and gained a branch.</param>
public sealed record ImportResult(ImportStatus Status, IReadOnlyList<ConversationId> Created, IReadOnlyList<ConversationId> Parents, ConversationId? Opens)
{
    /// <summary>The conversations that were here and gained messages, a source or a summary.</summary>
    public IReadOnlyList<ConversationId> Gained { get; init; } = [];

    /// <summary>The conversations, new or not, that gained a summary or a further version of one.</summary>
    public IReadOnlyList<ConversationId> Summarized { get; init; } = [];

    /// <summary>The workspace in which summary links were added; null when none was.</summary>
    public WorkspaceId? Linked { get; init; }

    public static ImportResult Nothing(ImportStatus status) => new(status, [], [], null);
}

/// <summary>Import persistence. Every method is one transaction (writes) or one snapshot (reads).</summary>
public interface IImportStore
{
    /// <summary>The workspace of each of <paramref name="conversations"/> that exists.</summary>
    Task<IReadOnlyDictionary<ConversationId, WorkspaceId>> HoldingsAsync(IReadOnlyCollection<ConversationId> conversations, CancellationToken cancellationToken);

    /// <summary>What of the file's contents the store has, in any workspace.</summary>
    Task<StoredContents> ContentsAsync(ExchangeDocument document, CancellationToken cancellationToken);

    /// <summary>
    /// Writes what is new, compared again inside the transaction. Only adds: nothing that exists is changed or removed
    /// (decision I2).
    /// </summary>
    Task<ImportResult> ImportAsync(ExchangeDocument document, ImportTarget target, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The shared file each received conversation of the workspace came from; the user's own are not in it.</summary>
    Task<IReadOnlyDictionary<ConversationId, ImportMark>> MarksAsync(WorkspaceId workspace, CancellationToken cancellationToken);
}
