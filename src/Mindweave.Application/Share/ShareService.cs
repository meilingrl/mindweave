using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Export;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Share;

/// <summary>Sharing a thinking path, as the share dialog and the exit use it.</summary>
public interface IShares
{
    /// <summary>What of the workspace can be shared, read afresh.</summary>
    Task<ShareSource> ReadAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    /// <summary>Writes the share to the picked file.</summary>
    /// <exception cref="ExportWriteException">Writing failed; the target is as it was.</exception>
    Task SaveAsync(ShareDocument document, ExportDestination destination, CancellationToken cancellationToken);

    /// <summary>Completes when every share being written now has ended, successfully or not; never faults (architecture §12.3).</summary>
    Task WhenIdleAsync();
}

/// <summary>
/// Reads what a share may hold and writes the file (docs/design/sharing.md). Reading drops everything a structure-only
/// share leaves out, so no message text, quote or summary text goes further than this class. Nothing is stored: the app
/// keeps no record of shares (S10).
/// </summary>
public sealed partial class ShareService(
    IConversationStore conversations,
    ISummaryStore summaries,
    ISummaryNetworkStore network,
    ISummaryLinkStore links,
    IExportFileWriter writer,
    ILogger<ShareService> logger) : IShares
{
    private readonly ConcurrentDictionary<Task, byte> _running = new();

    public Task WhenIdleAsync() => Task.WhenAll(_running.Keys);

    public async Task<ShareSource> ReadAsync(WorkspaceId workspace, CancellationToken cancellationToken)
    {
        var listed = await conversations.ListAsync(workspace, cancellationToken).ConfigureAwait(false);
        var summarized = (await summaries.ListAsync(workspace, cancellationToken).ConfigureAwait(false)).ToDictionary(e => e.Node.Conversation);
        var questions = await network.QuestionCountsAsync(workspace, cancellationToken).ConfigureAwait(false);
        var linked = await links.ListAsync(workspace, cancellationToken).ConfigureAwait(false);

        var offered = new List<ShareableConversation>(listed.Count);
        foreach (var row in listed)
        {
            var id = row.Conversation.Id;
            var answering = row.LastAnswerState is MessageState.Pending or MessageState.Streaming;
            offered.Add(new ShareableConversation(
                id,
                row.Parent,
                NameOf(row.Conversation.Title, summarized.GetValueOrDefault(id)),
                row.Conversation.CreatedAt,
                answering ? await AnsweredQuestionsAsync(id, cancellationToken).ConfigureAwait(false) : questions.GetValueOrDefault(id),
                summarized.ContainsKey(id),
                answering));
        }

        var conversationOf = summarized.Values.ToDictionary(e => e.Node.Id, e => e.Node.Conversation);
        return new ShareSource(
            offered,
            [.. linked.Where(l => conversationOf.ContainsKey(l.NodeA) && conversationOf.ContainsKey(l.NodeB))
                .Select(l => new ShareLink(conversationOf[l.NodeA], conversationOf[l.NodeB]))]);
    }

    public async Task SaveAsync(ShareDocument document, ExportDestination destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(destination);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _running.TryAdd(done.Task, 0);
        try
        {
            var mode = await writer.WriteAsync(destination, SharePage.Encode(document), cancellationToken).ConfigureAwait(false);
            LogShared(logger, document.Conversations.Count, mode);
        }
        finally
        {
            _running.TryRemove(done.Task, out _);
            done.SetResult();
        }
    }

    /// <summary>
    /// The name that leaves: the summary's, as the Mindweave page shows it, else the title. Never the quote or the first
    /// question the app shows for a conversation without a title: both are message text (§6.6, points 1 and 2).
    /// </summary>
    private static string? NameOf(string? title, SummaryEntry? summary) =>
        summary is not null ? SummaryRules.ShortTitleOf(summary.Current.Title, summary.Current.ShortTitle) : title;

    /// <summary>
    /// Questions with a finished answer, where the last answer is still being generated (§9: up to the last finished
    /// answer). Only their number is kept.
    /// </summary>
    private async Task<int> AnsweredQuestionsAsync(ConversationId id, CancellationToken cancellationToken)
    {
        var messages = await conversations.GetMessagesAsync(id, cancellationToken).ConfigureAwait(false);
        var lastFinished = messages.LastOrDefault(m => m.Role == MessageRole.Assistant && m.IsFinished);
        return lastFinished is null ? 0 : messages.Count(m => m.Role == MessageRole.User && m.Seq < lastFinished.Seq);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved a structure-only share of {Conversations} conversations ({Mode})")]
    private static partial void LogShared(ILogger logger, int conversations, ExportWriteMode mode);
}
