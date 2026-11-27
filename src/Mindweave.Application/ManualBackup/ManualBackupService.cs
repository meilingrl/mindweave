using Microsoft.Extensions.Logging;
using Mindweave.Application.Exchange;
using Mindweave.Application.Export;
using Mindweave.Application.Import;
using Mindweave.Application.Share;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.ManualBackup;

/// <summary>
/// Saves a manual backup (docs/design/sharing-backup.md): reads one workspace at a time, builds its document of kind
/// <c>device</c> and the share with content its page is drawn from, and hands both to the package writer. Nothing is
/// stored: the app keeps no record of manual backups.
/// </summary>
public sealed partial class ManualBackupService(
    IWorkspaceStore workspaces,
    IConversationStore conversations,
    IBranchStore branches,
    ISummaryStore summaries,
    ISummaryLinkStore links,
    IResourceStore resources,
    IRunStore runs,
    IImportStore imports,
    IManualBackupStore store,
    IShares shares,
    IShareContents shareContents,
    IManualBackupWriter writer,
    AppEnvironment environment,
    TimeProvider time,
    ILogger<ManualBackupService> logger) : IManualBackups
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _running;
    private Task _saving = Task.CompletedTask;

    public async Task<IReadOnlyList<ManualBackupWorkspace>> ListAsync(CancellationToken cancellationToken)
    {
        var listed = await workspaces.ListAsync(cancellationToken).ConfigureAwait(false);
        var sizes = await store.SizesAsync(cancellationToken).ConfigureAwait(false);
        return [.. listed.Select(w => sizes.TryGetValue(w.Id, out var size)
            ? new ManualBackupWorkspace(w.Id, w.Name.Value, size.Conversations, size.Bytes)
            : new ManualBackupWorkspace(w.Id, w.Name.Value, 0, 0))];
    }

    public async Task<ManualBackupSaved> SaveAsync(IReadOnlyCollection<WorkspaceId> chosen, ExportDestination destination, IProgress<ManualBackupProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(destination);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_running is not null)
            {
                throw new InvalidOperationException("A manual backup is being written already.");
            }

            _running = linked;
            _saving = done.Task;
        }

        try
        {
            var madeAt = time.GetUtcNow();
            var listed = await workspaces.ListAsync(linked.Token).ConfigureAwait(false);
            var order = listed.Where(w => chosen.Contains(w.Id)).ToDictionary(w => w.Id);
            var saved = await writer.WriteAsync(
                madeAt,
                environment.Version,
                [.. order.Keys],
                (id, ct) => ReadAsync(id, madeAt, ct),
                destination,
                progress,
                linked.Token).ConfigureAwait(false);
            LogSaved(logger, saved.Workspaces, saved.Conversations, saved.Originals, saved.MissingOriginals, saved.Mode);
            return saved;
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
            }

            done.SetResult();
        }
    }

    public async Task StopAsync()
    {
        Task saving;
        lock (_gate)
        {
            saving = _saving;
            try
            {
                _running?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It ended meanwhile.
            }
        }

        await saving.ConfigureAwait(false);
    }

    /// <summary>One workspace as of now; null when it was deleted meanwhile.</summary>
    private async Task<ManualBackupPart?> ReadAsync(WorkspaceId id, DateTimeOffset madeAt, CancellationToken cancellationToken)
    {
        if ((await workspaces.ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(w => w.Id == id) is not { } workspace)
        {
            return null;
        }

        var contents = await ContentsAsync(workspace, cancellationToken).ConfigureAwait(false);
        var document = ManualBackupDocument.Build(contents, madeAt, environment.Version);

        // The readable page is drawn as a share with content of every conversation: what such a share shows, nothing more.
        var source = await shares.ReadAsync(id, cancellationToken).ConfigureAwait(false);
        var page = await shareContents.BuildAsync(workspace.Name.Value, id, source, ShareSelection.All(source.Parents), madeAt, environment.Version, cancellationToken).ConfigureAwait(false);
        return new ManualBackupPart(document, page);
    }

    private async Task<ManualBackupContents> ContentsAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        var listed = await conversations.ListAsync(workspace.Id, cancellationToken).ConfigureAwait(false);
        var received = await imports.MarksAsync(workspace.Id, cancellationToken).ConfigureAwait(false);
        var read = new List<ManualBackupConversation>(listed.Count);
        foreach (var row in listed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = row.Conversation.Id;
            if (await conversations.GetAsync(id, cancellationToken).ConfigureAwait(false) is not { } conversation)
            {
                continue; // Deleted meanwhile.
            }

            var entry = await summaries.ForConversationAsync(id, cancellationToken).ConfigureAwait(false);
            read.Add(new ManualBackupConversation(
                row with { Conversation = conversation },
                await conversations.GetMessagesAsync(id, cancellationToken).ConfigureAwait(false),
                await branches.GetSourceAsync(id, cancellationToken).ConfigureAwait(false),
                entry is null ? null : await summaries.GetAsync(entry.Node.Id, cancellationToken).ConfigureAwait(false),
                await runs.RunsAsync(id, cancellationToken).ConfigureAwait(false),
                await store.AnswerModelsAsync(id, cancellationToken).ConfigureAwait(false),
                received.GetValueOrDefault(id)));
        }

        var referred = read.SelectMany(c => c.Messages).SelectMany(Referred)
            .Concat(read.SelectMany(c => c.Runs.Values).SelectMany(r => r.Steps).Select(s => s.Result).OfType<ResourceId>())
            .ToHashSet();
        var material = new Dictionary<ResourceId, StoredResource>();
        foreach (var resource in referred)
        {
            if (await resources.GetAsync(resource, cancellationToken).ConfigureAwait(false) is { } stored)
            {
                material[resource] = stored;
            }
        }

        var texts = await resources.GetDerivativesAsync(material.Keys, Core.Resources.DerivativeKind.Text, null, cancellationToken).ConfigureAwait(false);
        var linked = await links.ListAsync(workspace.Id, cancellationToken).ConfigureAwait(false);
        return new ManualBackupContents(workspace, read, linked, material, texts);
    }

    private static IEnumerable<ResourceId> Referred(Message message) => message.Blocks.Select(b => b switch
    {
        AttachmentBlock a => a.Resource,
        ToolResultBlock { Resource: { } result } => result,
        CitationBlock { Resource: { } cited } => cited,
        _ => (ResourceId?)null,
    }).OfType<ResourceId>();

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved a manual backup of {Workspaces} workspaces, {Conversations} conversations, {Originals} originals ({Missing} missing) ({Mode})")]
    private static partial void LogSaved(ILogger logger, int workspaces, int conversations, int originals, int missing, ExportWriteMode mode);
}
