using Mindweave.Application.Share;
using Mindweave.Core.Conversations;

namespace Mindweave.Infrastructure.Tests.Share;

/// <summary>Which conversations a share holds (docs/design/sharing.md S1).</summary>
public sealed class ShareSelectionTests
{
    private static readonly ConversationId Root = ConversationId.New();
    private static readonly ConversationId Branch = ConversationId.New();
    private static readonly ConversationId Leaf = ConversationId.New();
    private static readonly ConversationId Sibling = ConversationId.New();
    private static readonly ConversationId OtherRoot = ConversationId.New();
    private static readonly ConversationId OtherBranch = ConversationId.New();

    private static readonly Dictionary<ConversationId, ConversationId?> Parents = new()
    {
        [Root] = null,
        [Branch] = Root,
        [Leaf] = Branch,
        [Sibling] = Root,
        [OtherRoot] = null,
        [OtherBranch] = OtherRoot,
    };

    [Fact]
    public void It_starts_as_the_whole_exploration_whichever_of_its_conversations_it_was_opened_from()
    {
        foreach (var opened in new[] { Root, Branch, Leaf, Sibling })
        {
            AssertTicked(ShareSelection.Exploration(Parents, opened), Root, Branch, Leaf, Sibling);
        }
    }

    [Fact]
    public void Opened_from_a_conversation_that_is_gone_it_starts_empty()
    {
        AssertTicked(ShareSelection.Exploration(Parents, ConversationId.New()));
    }

    [Fact]
    public void Unticking_a_conversation_unticks_its_branches()
    {
        var selection = ShareSelection.Exploration(Parents, Root);

        AssertTicked(selection.Untick(Branch), Root, Sibling);
        AssertTicked(selection.Untick(Root));
    }

    [Fact]
    public void Ticking_a_branch_ticks_its_ancestors_and_nothing_else()
    {
        var empty = ShareSelection.Exploration(Parents, Root).Untick(Root);

        AssertTicked(empty.Tick(Leaf), Root, Branch, Leaf);
        AssertTicked(empty.Tick(Branch), Root, Branch);
    }

    [Fact]
    public void Ticking_a_root_ticks_its_whole_exploration()
    {
        var selection = ShareSelection.Exploration(Parents, Root);

        AssertTicked(selection.Tick(OtherRoot), Root, Branch, Leaf, Sibling, OtherRoot, OtherBranch);
    }

    [Fact]
    public void Ticking_what_is_not_there_changes_nothing()
    {
        var selection = ShareSelection.Exploration(Parents, Root);

        Assert.Equal(selection.Ticked, selection.Tick(ConversationId.New()).Ticked);
        Assert.Equal(selection.Ticked, selection.Untick(ConversationId.New()).Ticked);
    }

    [Fact]
    public void Kept_through_a_reload_it_loses_what_was_deleted_and_a_new_branch_is_not_ticked()
    {
        var added = ConversationId.New();
        var reloaded = new Dictionary<ConversationId, ConversationId?> { [Root] = null, [Sibling] = Root, [added] = Root };

        AssertTicked(ShareSelection.Exploration(Parents, Root).Over(reloaded), Root, Sibling);
    }

    private static void AssertTicked(ShareSelection selection, params ConversationId[] expected) =>
        Assert.Equal(expected.ToHashSet(), selection.Ticked.ToHashSet());
}
