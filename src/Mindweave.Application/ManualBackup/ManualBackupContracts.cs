using Mindweave.Application.Exchange;
using Mindweave.Application.Export;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.ManualBackup;

/// <summary>A workspace the dialog offers, with how much of the file it would take.</summary>
/// <param name="Bytes">An estimate: its originals, counted once by content, and its text.</param>
public sealed record ManualBackupWorkspace(WorkspaceId Id, string Name, int Conversations, long Bytes);

/// <summary>How far a save has got: workspaces written of those chosen.</summary>
public sealed record ManualBackupProgress(int Done, int Total);

/// <summary>What a saved manual backup holds.</summary>
/// <param name="MissingOriginals">Originals that were missing or damaged on this device; the file records them as such.</param>
public sealed record ManualBackupSaved(int Workspaces, int Conversations, int Originals, int MissingOriginals, ExportWriteMode Mode);

/// <summary>Saving a manual backup (docs/design/sharing-backup.md §5), as its dialog and the exit use it.</summary>
public interface IManualBackups
{
    /// <summary>Every workspace, in the order the app lists them.</summary>
    Task<IReadOnlyList<ManualBackupWorkspace>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes one package of the <paramref name="chosen"/> workspaces to the picked file. A workspace deleted meanwhile is left out.
    /// Nothing is at the destination until the whole package is written.
    /// </summary>
    /// <exception cref="ExportWriteException">Writing failed; no file is left.</exception>
    /// <exception cref="OperationCanceledException">Cancelled, or the app is quitting; no file is left.</exception>
    Task<ManualBackupSaved> SaveAsync(IReadOnlyCollection<WorkspaceId> chosen, ExportDestination destination, IProgress<ManualBackupProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Cancels a backup being written and completes when it has ended; never faults (architecture §12.3).</summary>
    Task StopAsync();
}

/// <summary>What the store has for a manual backup that its other stores do not say.</summary>
public interface IManualBackupStore
{
    /// <summary>For each workspace, its conversations and an estimate of its size (see <see cref="ManualBackupWorkspace.Bytes"/>).</summary>
    Task<IReadOnlyDictionary<WorkspaceId, (int Conversations, long Bytes)>> SizesAsync(CancellationToken cancellationToken);

    /// <summary>The name of the model that wrote each answer of the conversation; nothing else of its connection is read.</summary>
    Task<IReadOnlyDictionary<MessageId, string>> AnswerModelsAsync(ConversationId conversation, CancellationToken cancellationToken);
}

/// <summary>One workspace of a package: its document, and the share with content its readable page is drawn from.</summary>
public sealed record ManualBackupPart(ExchangeDocument Document, ExchangeDocument Page);

/// <summary>Writes the package (§4): the file Infrastructure makes, since Application writes no file and no HTML.</summary>
public interface IManualBackupWriter
{
    /// <summary>The package's own extension, without the dot.</summary>
    string Extension { get; }

    /// <summary>
    /// Writes a package of the workspaces in order, reading each one only when it is written, so that one workspace is in
    /// memory at a time. <paramref name="read"/> gives null for a workspace that is gone. A temporary file beside the
    /// destination is moved into place at the end; a cancel or a failure removes it.
    /// </summary>
    /// <exception cref="ExportWriteException">Writing failed.</exception>
    Task<ManualBackupSaved> WriteAsync(
        DateTimeOffset madeAt,
        string appVersion,
        IReadOnlyList<WorkspaceId> workspaces,
        Func<WorkspaceId, CancellationToken, Task<ManualBackupPart?>> read,
        ExportDestination destination,
        IProgress<ManualBackupProgress>? progress,
        CancellationToken cancellationToken);
}
