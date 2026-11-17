using System.Collections.ObjectModel;
using Mindweave.Application.Conversations;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>
/// Brings a conversation page's message list in line with what storage holds, in place: a message view that is
/// still valid keeps its control, so a selection, a highlighted source or the layout being read survives a reload
/// (a title arriving, a branch being made). Only new messages are added, and an answer that was live and is now
/// stored as finished is replaced, so its view switches from the live builder to the stored text that anchors are
/// captured against.
/// </summary>
internal static class MessageListReconciler
{
    /// <param name="items">The page's list, updated in place; removed and replaced items are disposed.</param>
    /// <param name="content">The conversation as stored.</param>
    /// <param name="branches">Branches made from this conversation's answers.</param>
    /// <param name="archivedBranches">Which of <paramref name="branches"/> are archived.</param>
    /// <param name="isArchived">Whether the page itself is archived (read-only).</param>
    /// <param name="create">Makes the view for a message not shown yet.</param>
    public static void Reconcile(
        ObservableCollection<MessageItemViewModel> items,
        ConversationContent content,
        IReadOnlyList<BranchSource> branches,
        IReadOnlySet<ConversationId> archivedBranches,
        bool isArchived,
        Func<Message, MessageItemViewModel> create)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(branches);
        ArgumentNullException.ThrowIfNull(archivedBranches);
        ArgumentNullException.ThrowIfNull(create);

        var last = ConversationRules.LastAnswer(content.Messages);
        var superseded = content.Messages.Where(m => m.Supersedes is not null).Select(m => m.Supersedes!.Value).ToHashSet();
        var stored = content.Messages.Select(m => m.Id).ToHashSet();
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (!stored.Contains(items[i].Id))
            {
                items[i].Dispose();
                items.RemoveAt(i);
            }
        }

        for (var i = 0; i < content.Messages.Count; i++)
        {
            var message = content.Messages[i];
            var item = i < items.Count && items[i].Id == message.Id ? items[i] : null;
            if (item is not null && item.IsLive && message.IsFinished)
            {
                item.Dispose();
                item = null;
                items.RemoveAt(i);
            }

            if (item is null)
            {
                item = create(message);
                items.Insert(i, item);
            }

            item.IsSuperseded = superseded.Contains(message.Id);
            item.WasCompressed = content.CompressedAnswers?.Contains(message.Id) == true;
            item.AttachmentInputs = content.ReducedAttachments?.GetValueOrDefault(message.Id);
            item.IsArchived = isArchived;
            item.CanRetry = !isArchived && last?.Id == message.Id && message.IsFinished;
            var entries = branches.Where(b => b.SourceMessage == message.Id).Select(b => new BranchEntry(b.Child, b.Quote, archivedBranches.Contains(b.Child))).ToList();
            if (!entries.SequenceEqual(item.Branches))
            {
                item.Branches.Clear();
                foreach (var entry in entries)
                {
                    item.Branches.Add(entry);
                }
            }
        }
    }
}
