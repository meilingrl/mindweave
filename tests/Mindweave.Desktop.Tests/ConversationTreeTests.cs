using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.ViewModels.Conversations;

namespace Mindweave.Desktop.Tests;

public class ConversationTreeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Branches_follow_their_parent_and_roots_are_ordered_by_activity_anywhere_in_their_tree()
    {
        var quiet = Summary("quiet", minutes: 50);
        var busy = Summary("busy", minutes: 10);
        var first = Summary("busy/1", minutes: 20, parent: busy);
        var deep = Summary("busy/1/a", minutes: 90, parent: first);
        var second = Summary("busy/2", minutes: 30, parent: busy);

        // Storage order: most recently updated first.
        var tree = ConversationListViewModel.AsTree([deep, quiet, second, first, busy]);

        Assert.Equal(["busy", "busy/1", "busy/1/a", "busy/2", "quiet"], tree.Select(t => t.Summary.FirstQuestion));
        Assert.Equal([0, 1, 2, 1, 0], tree.Select(t => t.Depth));
    }

    [Fact]
    public void A_branch_whose_parent_is_not_listed_is_shown_as_a_root()
    {
        var orphan = Summary("orphan", minutes: 5, parent: Summary("missing", minutes: 1));

        var tree = ConversationListViewModel.AsTree([orphan]);

        Assert.Equal(0, Assert.Single(tree).Depth);
    }

    private static ConversationSummary Summary(string name, int minutes, ConversationSummary? parent = null)
    {
        // Created in the order of their names' depth; updated at the given minute.
        var created = T0.AddMinutes(name.Count(c => c == '/') + (name.EndsWith('2') ? 1 : 0));
        var conversation = new Conversation(ConversationId.New(), WorkspaceId.New(), null, TitleSource.None, created, T0.AddMinutes(minutes));
        return new ConversationSummary(conversation, name, MessageState.Complete, parent?.Conversation.Id, parent is null ? null : name);
    }
}
