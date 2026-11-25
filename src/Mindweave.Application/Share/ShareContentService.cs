using Mindweave.Application.Exchange;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Share;

/// <summary>Builds the exchange document of a share with content.</summary>
public interface IShareContents
{
    /// <summary>The document of what is ticked, read afresh. A conversation deleted since the list was read is not in it.</summary>
    Task<ExchangeDocument> BuildAsync(string title, WorkspaceId workspace, ShareSource source, ShareSelection selection, DateTimeOffset madeAt, string appVersion, CancellationToken cancellationToken);
}

/// <summary>
/// Reads the ticked conversations and builds the document (docs/design/sharing.md §6.1). What is stored goes no further
/// than this: only the document, already filtered, is handed on.
/// </summary>
public sealed class ShareContentService(
    IConversationStore conversations,
    IBranchStore branches,
    ISummaryStore summaries,
    ISummaryLinkStore links,
    IShareMarkdown markdown) : IShareContents
{
    public async Task<ExchangeDocument> BuildAsync(string title, WorkspaceId workspace, ShareSource source, ShareSelection selection, DateTimeOffset madeAt, string appVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selection);
        var read = new Dictionary<ConversationId, ShareableContent>();
        foreach (var id in selection.Over(source.Parents).Ticked)
        {
            if (await ReadAsync(id, cancellationToken).ConfigureAwait(false) is { } content)
            {
                read[id] = content;
            }
        }

        var linked = await links.ListAsync(workspace, cancellationToken).ConfigureAwait(false);
        return ExchangeDocument.Content(title, source, selection, new ShareContents(read, linked), madeAt, appVersion);
    }

    private async Task<ShareableContent?> ReadAsync(ConversationId id, CancellationToken cancellationToken)
    {
        if (await conversations.GetAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        var messages = await conversations.GetMessagesAsync(id, cancellationToken).ConfigureAwait(false);
        var branch = await branches.GetSourceAsync(id, cancellationToken).ConfigureAwait(false);
        var entry = await summaries.ForConversationAsync(id, cancellationToken).ConfigureAwait(false);
        var summary = entry is null ? null : await summaries.GetAsync(entry.Node.Id, cancellationToken).ConfigureAwait(false);
        return new ShareableContent(messages)
        {
            Source = branch,
            SourceOffset = branch is null ? null : markdown.OffsetOf(branch),
            SummaryNode = summary?.Node.Id,
            Summary = summary?.Current,
        };
    }
}
