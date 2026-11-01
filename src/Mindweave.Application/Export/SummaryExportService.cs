using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.Export;

/// <summary>Why a version cannot be exported; nothing was written.</summary>
public enum ExportRefusal
{
    /// <summary>The version, or its whole summary, was deleted after the page showed it.</summary>
    VersionMissing,

    /// <summary>A candidate is not a saved version; it is adopted first (§8.1).</summary>
    IsCandidate,
}

public sealed class ExportRefusedException(ExportRefusal reason) : Exception(reason.ToString())
{
    public ExportRefusal Reason { get; } = reason;
}

/// <summary>
/// Exports one saved summary version as Markdown (§8.3, architecture §8.1). Every step reads the version afresh by
/// ID, so what is written is what is stored at that moment, not what a page loaded earlier; a version deleted while
/// the save dialog was open is refused and no file is touched. Exporting writes nothing to the store: no version, no
/// adoption, no read state.
/// </summary>
public sealed partial class SummaryExportService : ISummaryExport
{
    private readonly ISummaryStore _summaries;
    private readonly IConversationStore _conversations;
    private readonly IExportFileWriter _writer;
    private readonly ILogger<SummaryExportService> _logger;
    private readonly ConcurrentDictionary<Task, byte> _running = new();

    public SummaryExportService(ISummaryStore summaries, IConversationStore conversations, IExportFileWriter writer, ILogger<SummaryExportService> logger)
    {
        _summaries = summaries;
        _conversations = conversations;
        _writer = writer;
        _logger = logger;
    }

    /// <summary>Exports being read or written right now.</summary>
    public int InProgress => _running.Count;

    /// <summary>
    /// Completes when every export running now has finished, successfully or not; never faults. Exit awaits it before
    /// closing the store (architecture §12.3), so a file being written is not cut off. Only the read-and-write step
    /// counts: a save dialog left open is the user's to close, not something exit waits for.
    /// </summary>
    public Task WhenIdleAsync() => Task.WhenAll(_running.Keys);

    /// <summary>The save dialog's suggested name (without extension) for a saved version.</summary>
    /// <exception cref="ExportRefusedException">The version is gone or is the candidate.</exception>
    public async Task<string> SuggestFileNameAsync(SummaryNodeId node, SummaryVersionId version, CancellationToken cancellationToken)
    {
        var (_, saved) = await SavedVersionAsync(node, version, cancellationToken).ConfigureAwait(false);
        return ExportFileName.Suggest(saved.Title, saved.Seq);
    }

    /// <summary>What the file will contain, read afresh.</summary>
    /// <exception cref="ExportRefusedException">The version is gone or is the candidate.</exception>
    /// <param name="sourceName">Names the source conversation as the app does everywhere else; null leaves the source note out.</param>
    public async Task<SummaryExportDocument> ReadAsync(SummaryNodeId node, SummaryVersionId version, Func<ConversationSummary, string>? sourceName, CancellationToken cancellationToken)
    {
        var (detail, saved) = await SavedVersionAsync(node, version, cancellationToken).ConfigureAwait(false);
        ExportSource? source = null;
        if (sourceName is not null)
        {
            var name = await ConversationNameAsync(detail.Node, sourceName, cancellationToken).ConfigureAwait(false);
            source = new ExportSource(name, saved.ToSeq + 1, saved.Seq, saved.Origin, saved.CreatedAt.ToLocalTime());
        }

        return new SummaryExportDocument(saved.Title, saved.Body, source);
    }

    /// <summary>Re-reads the version and writes it to the picked file.</summary>
    /// <exception cref="ExportRefusedException">The version is gone or is the candidate; nothing was written.</exception>
    /// <exception cref="ExportWriteException">Writing failed; the target is as it was.</exception>
    public async Task ExportAsync(SummaryNodeId node, SummaryVersionId version, Func<ConversationSummary, string>? sourceName, ExportDestination destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _running.TryAdd(done.Task, 0);
        try
        {
            var document = await ReadAsync(node, version, sourceName, cancellationToken).ConfigureAwait(false);
            var mode = await _writer.WriteAsync(destination, SummaryMarkdown.Encode(document), cancellationToken).ConfigureAwait(false);
            LogExported(_logger, version, mode);
        }
        finally
        {
            _running.TryRemove(done.Task, out _);
            done.SetResult();
        }
    }

    private async Task<(SummaryDetail Detail, SummaryVersion Version)> SavedVersionAsync(SummaryNodeId node, SummaryVersionId version, CancellationToken cancellationToken)
    {
        var detail = await _summaries.GetAsync(node, cancellationToken).ConfigureAwait(false);
        if (detail?.Versions.FirstOrDefault(v => v.Id == version) is not { } found)
        {
            throw new ExportRefusedException(ExportRefusal.VersionMissing);
        }

        if (detail.Node.Pending == version)
        {
            throw new ExportRefusedException(ExportRefusal.IsCandidate);
        }

        return (detail, found);
    }

    /// <summary>
    /// The source conversation's name, by the caller's naming rule so the file says what the sidebar says. A summary
    /// always has its conversation (a conversation with a summary cannot be deleted), so a missing one means the
    /// summary itself went meanwhile.
    /// </summary>
    private async Task<string> ConversationNameAsync(SummaryNode node, Func<ConversationSummary, string> sourceName, CancellationToken cancellationToken)
    {
        var rows = await _conversations.ListAsync(node.Workspace, cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault(r => r.Conversation.Id == node.Conversation) is { } row
            ? sourceName(row)
            : throw new ExportRefusedException(ExportRefusal.VersionMissing);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Exported summary version {Version} ({Mode})")]
    private static partial void LogExported(ILogger logger, SummaryVersionId version, ExportWriteMode mode);
}
