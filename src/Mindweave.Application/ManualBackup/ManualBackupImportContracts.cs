using Mindweave.Application.Exchange;
using Mindweave.Application.Import;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.ManualBackup;

/// <summary>
/// A manual backup as it was read (docs/design/sharing-backup.md §3, §4): each workspace's document of kind <c>device</c>,
/// and which originals the package holds with the content their hash names.
/// </summary>
/// <param name="Path">Where the package is: its originals are read from it again when they are written.</param>
/// <param name="Originals">The originals in the package that match their hash.</param>
/// <param name="Mismatched">Originals in the package that do not match their hash: they are treated as missing (§6).</param>
public sealed record ManualBackupFile(string Path, DateTimeOffset MadeAt, string AppVersion, IReadOnlyList<ExchangeDocument> Workspaces, IReadOnlySet<ContentHash> Originals, int Mismatched);

/// <summary>A package as it was read, or why it is refused; a refusal changes nothing.</summary>
public sealed record ManualBackupReading(ManualBackupFile? File, ImportRefusal? Refusal)
{
    public static ManualBackupReading Of(ManualBackupFile file) => new(file, null);

    public static ManualBackupReading Refused(ImportRefusal refusal) => new(null, refusal);
}

/// <summary>Reads a manual backup's package entry by entry, by the known names only (architecture §16.3).</summary>
public interface IManualBackupReader
{
    /// <summary>The package's own extension, without the dot.</summary>
    string Extension { get; }

    Task<ManualBackupReading> ReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>One original from the package, or null when it is not there any more or no longer matches its hash.</summary>
    Task<byte[]?> ReadOriginalAsync(string path, ContentHash hash, CancellationToken cancellationToken);
}

/// <summary>What the store has of one of the package's conversations.</summary>
/// <param name="Messages">Its messages by number, as joining sees them.</param>
/// <param name="HasUnfinished">An answer is being written in it.</param>
/// <param name="HasSource">It is a branch that left from a message.</param>
public sealed record ManualBackupStoredConversation(
    WorkspaceId Workspace,
    bool IsReceived,
    IReadOnlyList<LineEntry> Messages,
    bool HasUnfinished,
    bool HasSource,
    SummaryNodeId? Node)
{
    /// <summary>A summary of it is being written: it was asked for up to a number, which joining would move under it.</summary>
    public bool IsSummarizing { get; init; }
}

/// <summary>
/// What the store has of a package, by identity: what its parts are compared with (docs/design/sharing-backup.md §2). No
/// text is in it.
/// </summary>
/// <param name="Messages">The conversation of each of the package's messages that is stored anywhere.</param>
/// <param name="Versions">The conversation of each of the package's summary versions that is stored anywhere.</param>
/// <param name="Resources">Every stored resource, with the conversation it belongs to.</param>
/// <param name="Nodes">The package's summary nodes that are stored, for whichever conversation.</param>
/// <param name="Linked">The pairs of summary nodes that are linked, in the links' order.</param>
public sealed record ManualBackupStored(
    IReadOnlySet<WorkspaceId> Workspaces,
    IReadOnlyDictionary<ConversationId, ManualBackupStoredConversation> Conversations,
    IReadOnlyDictionary<MessageId, ConversationId> Messages,
    IReadOnlyDictionary<SummaryVersionId, ConversationId> Versions,
    IReadOnlyDictionary<ResourceId, ConversationId?> Resources,
    IReadOnlySet<SummaryNodeId> Nodes,
    IReadOnlySet<(SummaryNodeId, SummaryNodeId)> Linked)
{
    public static ManualBackupStored Nothing { get; } = new(
        new HashSet<WorkspaceId>(),
        new Dictionary<ConversationId, ManualBackupStoredConversation>(),
        new Dictionary<MessageId, ConversationId>(),
        new Dictionary<SummaryVersionId, ConversationId>(),
        new Dictionary<ResourceId, ConversationId?>(),
        new HashSet<SummaryNodeId>(),
        new HashSet<(SummaryNodeId, SummaryNodeId)>());
}

/// <summary>How one of the package's conversations compares with the store.</summary>
public enum ManualBackupFit
{
    /// <summary>It is not here, and is created.</summary>
    New,

    /// <summary>It is here and gains no message.</summary>
    Same,

    /// <summary>It is here; the messages it lacks all come after the end of its line, or at their own numbers if it was received.</summary>
    Appends,

    /// <summary>Both sides hold messages the other lacks: its line is joined by time and numbered anew (V2).</summary>
    Joins,

    /// <summary>What the package says of it does not fit what is stored; nothing of it is written.</summary>
    Unfit,
}

/// <summary>What an import does with one of the package's conversations.</summary>
/// <param name="Workspace">Where it is here, or where it is created.</param>
/// <param name="Arriving">The messages it lacks, in the package's order.</param>
/// <param name="Line">Its line after the import, with every message's number; null when it gains no message.</param>
/// <param name="Versions">The saved summary versions it lacks, in the package's order.</param>
/// <param name="NewSummary">It has no summary here and gains the package's, with its name and its current version.</param>
/// <param name="GainsSource">It is a branch here without the message it left from, which the package names.</param>
/// <param name="Elsewhere">It is here in another workspace than the package says; it stays there.</param>
/// <param name="Running">An answer is being written in it and it would gain messages, or a summary of it is being written and it would be joined.</param>
public sealed record ManualBackupConversationPlan(
    ExchangeConversation Conversation,
    WorkspaceId Workspace,
    ManualBackupFit Fit,
    IReadOnlyList<ExchangeMessage> Arriving,
    JoinedLine? Line,
    IReadOnlyList<ExchangeSummaryVersion> Versions,
    bool NewSummary,
    bool GainsSource,
    bool Elsewhere,
    bool Running)
{
    /// <summary>Anything of it is written.</summary>
    public bool Writes => Fit is ManualBackupFit.New or ManualBackupFit.Appends or ManualBackupFit.Joins || Versions.Count > 0 || GainsSource;
}

/// <summary>What an import does with one workspace of the package.</summary>
/// <param name="Create">The workspace is not here and is created with its name.</param>
/// <param name="Conversations">Each after its parent.</param>
/// <param name="Links">The summary links that are added.</param>
public sealed record ManualBackupWorkspacePlan(ExchangeDocument Document, bool Create, IReadOnlyList<ManualBackupConversationPlan> Conversations, IReadOnlyList<ExchangeLink> Links)
{
    public ExchangeWorkspace Workspace => Document.Workspace!;

    public int New => Conversations.Count(c => c.Fit == ManualBackupFit.New);

    /// <summary>Here, and gains messages after the end of its line.</summary>
    public int GainsMessages => Conversations.Count(c => c.Fit == ManualBackupFit.Appends);

    public int Joined => Conversations.Count(c => c.Fit == ManualBackupFit.Joins);

    /// <summary>Here, and gains only summary versions or the message a branch left from.</summary>
    public int GainsOther => Conversations.Count(c => c.Fit == ManualBackupFit.Same && c.Writes);

    public int AlreadyHere => Conversations.Count(c => c.Fit == ManualBackupFit.Same && !c.Writes);

    public int Elsewhere => Conversations.Count(c => c.Elsewhere);

    public int Unfit => Conversations.Count(c => c.Fit == ManualBackupFit.Unfit);

    public int Running => Conversations.Count(c => c.Running);

    public bool Writes => Create || Links.Count > 0 || Conversations.Any(c => c.Writes);
}

/// <summary>What importing a package would do.</summary>
public sealed record ManualBackupPlan(IReadOnlyList<ManualBackupWorkspacePlan> Workspaces)
{
    /// <summary>Nothing would be written.</summary>
    public bool IsEmpty => !Workspaces.Any(w => w.Writes);

    /// <summary>An answer is being written in a conversation that would gain messages, or a summary in one that would be joined: the import waits until it ends.</summary>
    public bool IsBlocked => Workspaces.Any(w => w.Running > 0);

    /// <summary>The stored material the messages that arrive refer to, which is not here yet.</summary>
    public IEnumerable<ExchangeResource> ArrivingResources(ManualBackupStored stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        foreach (var workspace in Workspaces)
        {
            var resources = workspace.Document.Resources.ToDictionary(r => r.Id);
            foreach (var id in workspace.Conversations.SelectMany(c => c.Arriving).SelectMany(ManualBackupDocument.Referred).Distinct())
            {
                if (!stored.Resources.ContainsKey(id) && resources.TryGetValue(id, out var resource))
                {
                    yield return resource;
                }
            }
        }
    }
}

public enum ManualBackupImportStatus
{
    Imported,

    /// <summary>Everything in the package is here; nothing was written.</summary>
    NothingNew,

    /// <summary>An answer or a summary began meanwhile where the import would change the line; nothing was written.</summary>
    AnswerRunning,
}

/// <summary>What an import did, per workspace, as its dialog names it.</summary>
public sealed record ManualBackupImported(ManualBackupImportStatus Status, IReadOnlyList<ManualBackupWorkspacePlan> Workspaces)
{
    /// <summary>The workspaces that were created, with their names.</summary>
    public IReadOnlyList<Workspace> Created { get; init; } = [];

    /// <summary>Originals that were not written because the package's copy did not match its hash, or could not be read.</summary>
    public int MissingOriginals { get; init; }

    public static ManualBackupImported Nothing(ManualBackupImportStatus status) => new(status, []);
}

/// <summary>Import persistence for manual backups. Every method is one transaction (writes) or one snapshot (reads).</summary>
public interface IManualBackupImportStore
{
    /// <summary>What the store has of the package's contents, in any workspace.</summary>
    Task<ManualBackupStored> StoredAsync(ManualBackupFile file, CancellationToken cancellationToken);

    /// <summary>
    /// Writes what is new, compared again inside the transaction, in one transaction. Only adds, except for the numbers of a
    /// conversation joined by time and what refers to them (docs/design/sharing-backup.md §2). The originals are written
    /// already; a resource whose original is not here arrives as "original missing".
    /// </summary>
    Task<ManualBackupImported> ImportAsync(ManualBackupFile file, DateTimeOffset at, CancellationToken cancellationToken);
}
