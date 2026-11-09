using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Desktop.ViewModels;

/// <summary>Archiving several conversations in one go, as the sidebar's bar and the delete dialog's "改为归档" do.</summary>
internal static class ConversationArchiving
{
    /// <summary>
    /// Archives each of <paramref name="roots"/> (each with its whole subtree, as a single archive does). Returns the
    /// ones refused because an answer or summary in them runs, and whether storage refused; it goes on past refusals.
    /// </summary>
    public static async Task<(List<ConversationId> Busy, bool StorageFailed)> ArchiveAllAsync(IConversations conversations, IEnumerable<ConversationId> roots)
    {
        var busy = new List<ConversationId>();
        foreach (var root in roots)
        {
            try
            {
                var outcome = await conversations.ArchiveAsync(root, CancellationToken.None);
                busy.AddRange(outcome.Busy);
            }
            catch (StorageWriteException)
            {
                return (busy, true);
            }
        }

        return (busy, false);
    }

    public static async Task<Dictionary<ConversationId, string>> TitlesAsync(IConversations conversations, WorkspaceId workspace) =>
        (await conversations.ListAsync(workspace, CancellationToken.None))
            .ToDictionary(r => r.Conversation.Id, r => UiText.ConversationTitle(r.Conversation, r.FirstQuestion, r.Quote));

    /// <summary>The titles of <paramref name="ids"/>, in order; one not listed in the workspace reads as a new conversation.</summary>
    public static async Task<IReadOnlyList<string>> TitlesAsync(IConversations conversations, WorkspaceId workspace, IEnumerable<ConversationId> ids)
    {
        var titles = await TitlesAsync(conversations, workspace);
        return [.. ids.Select(id => titles.GetValueOrDefault(id, UiText.NewConversation))];
    }
}
