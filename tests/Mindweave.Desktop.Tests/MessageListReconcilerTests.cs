using System.Collections.ObjectModel;
using Mindweave.Application.Conversations;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels.Conversations;

namespace Mindweave.Desktop.Tests;

/// <summary>The page's in-place message list update, without building a page.</summary>
public class MessageListReconcilerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 9, 30, 0, TimeSpan.Zero);
    private static readonly ConversationId Owner = ConversationId.New();

    [Fact]
    public void Kept_messages_keep_their_view_new_ones_are_added_and_gone_ones_are_removed()
    {
        var question = Message(1, MessageRole.User);
        var answer = Message(2, MessageRole.Assistant);
        var dropped = Message(3, MessageRole.User);
        var items = new ObservableCollection<MessageItemViewModel>();
        var created = new List<MessageId>();
        MessageItemViewModel Create(Message m)
        {
            created.Add(m.Id);
            return new MessageItemViewModel(m, null, new InlineDispatcher());
        }

        MessageListReconciler.Reconcile(items, Content(question, answer, dropped), [], new HashSet<ConversationId>(), false, Create);
        Assert.Equal([question.Id, answer.Id, dropped.Id], items.Select(i => i.Id));
        var keptQuestion = items[0];
        var keptAnswer = items[1];

        var retry = Message(4, MessageRole.Assistant, supersedes: answer.Id);
        MessageListReconciler.Reconcile(items, Content(question, answer, retry), [], new HashSet<ConversationId>(), false, Create);

        Assert.Equal([question.Id, answer.Id, retry.Id], items.Select(i => i.Id));
        Assert.Same(keptQuestion, items[0]);
        Assert.Same(keptAnswer, items[1]);
        Assert.Equal([question.Id, answer.Id, dropped.Id, retry.Id], created);
        Assert.True(items[1].IsSuperseded);
        Assert.False(items[2].IsSuperseded);
    }

    [Fact]
    public void Only_the_last_finished_answer_can_be_retried_and_none_on_an_archived_page()
    {
        var question = Message(1, MessageRole.User);
        var first = Message(2, MessageRole.Assistant);
        var again = Message(3, MessageRole.User);
        var last = Message(4, MessageRole.Assistant);
        var items = new ObservableCollection<MessageItemViewModel>();
        var content = Content(question, first, again, last);

        MessageListReconciler.Reconcile(items, content, [], new HashSet<ConversationId>(), false, Create);
        Assert.Equal([false, false, false, true], items.Select(i => i.CanRetry));
        Assert.All(items, i => Assert.False(i.IsArchived));

        MessageListReconciler.Reconcile(items, content, [], new HashSet<ConversationId>(), true, Create);
        Assert.All(items, i => Assert.False(i.CanRetry));
        Assert.All(items, i => Assert.True(i.IsArchived));
    }

    [Fact]
    public void Answers_list_their_branches_with_the_archived_ones_marked_and_compressed_answers_are_flagged()
    {
        var question = Message(1, MessageRole.User);
        var answer = Message(2, MessageRole.Assistant);
        var open = Branch(answer.Id, "甲");
        var archived = Branch(answer.Id, "乙");
        var items = new ObservableCollection<MessageItemViewModel>();
        var content = new ConversationContent(Stored(), [question, answer], new HashSet<MessageId> { answer.Id });

        MessageListReconciler.Reconcile(items, content, [open, archived], new HashSet<ConversationId> { archived.Child }, false, Create);

        Assert.Empty(items[0].Branches);
        Assert.Equal([new BranchEntry(open.Child, "甲"), new BranchEntry(archived.Child, "乙", Archived: true)], items[1].Branches);
        Assert.False(items[0].WasCompressed);
        Assert.True(items[1].WasCompressed);

        // Unchanged entries are left alone; a branch gone is dropped.
        var entries = items[1].Branches;
        MessageListReconciler.Reconcile(items, content, [open], new HashSet<ConversationId>(), false, Create);
        Assert.Same(entries, items[1].Branches);
        Assert.Equal([new BranchEntry(open.Child, "甲")], items[1].Branches);
    }

    private static MessageItemViewModel Create(Message message) => new(message, null, new InlineDispatcher());

    private static Conversation Stored() =>
        new(Owner, WorkspaceId.New(), null, TitleSource.None, Start, Start);

    private static ConversationContent Content(params Message[] messages) => new(Stored(), messages);

    private static Message Message(int seq, MessageRole role, MessageId? supersedes = null) =>
        new(MessageId.New(), Owner, seq, role, ContentBlocks.FromText(role == MessageRole.User ? "问" : "答"), MessageState.Complete, 0, supersedes, null, null, Start, Start.AddSeconds(seq));

    private static BranchSource Branch(MessageId source, string quote) =>
        new(ConversationId.New(), Owner, source, 0, quote, "{}", 1, "default", Start);

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }
}
