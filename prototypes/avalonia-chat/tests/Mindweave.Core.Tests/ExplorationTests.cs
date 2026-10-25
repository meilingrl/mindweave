using Mindweave.Core;
using Xunit;

namespace Mindweave.Core.Tests;

public class ExplorationTests
{
    [Fact]
    public void Growing_markdown_cannot_create_an_unstable_source_reference()
    {
        var state = State();
        state.Current.Messages.Add(new("growing", false, "[铁", ReplyStatus.Generating));
        Assert.Throws<InvalidOperationException>(() => state.Branch("growing", "b0", 1, 2, new(null, 0), ""));
        Assert.Equal(2, state.Conversations.Count);
    }

    private static ExplorationState State()
    {
        var root = new Conversation("root", "火车历史");
        root.Messages.Add(new("answer", false, "第一段。\n\n火车依靠**蒸汽机**前进，也可以使用`电动机`。\n\n最后一段。"));
        return new([root, new Conversation("other", "天空为什么是蓝色的")]);
    }

    // Catches losing the selection's actual raw range when formatting tokens are hidden.
    [Fact]
    public void Selection_across_bold_and_code_maps_to_original_markdown()
    {
        const string raw = "从**蒸汽机**到`电动机`。";
        var block = Assert.Single(MarkdownDocument.Parse(raw));
        Assert.Equal("从蒸汽机到电动机。", block.Text);
        var anchor = MarkdownDocument.Anchor("root", "a", block, 1, 8);
        Assert.Equal("蒸汽机到电动机", anchor.Quote);
        Assert.Equal("蒸汽机**到`电动机", raw[anchor.RawStart..anchor.RawEnd]);
    }

    // Catches treating link syntax or destination as selectable visible characters.
    [Fact]
    public void Link_label_maps_to_label_in_source_and_keeps_destination()
    {
        const string raw = "参考[铁路史](https://example.org/history)。";
        var block = Assert.Single(MarkdownDocument.Parse(raw));
        Assert.Equal("参考铁路史。", block.Text);
        var anchor = MarkdownDocument.Anchor("r", "m", block, 2, 5);
        Assert.Equal("铁路史", raw[anchor.RawStart..anchor.RawEnd]);
        Assert.Equal("https://example.org/history", block.Runs.Single(x => x.Link != null).Link);
    }

    // Catches creating a branch with missing/wrong parent provenance or losing the draft.
    [Fact]
    public void Branch_records_source_and_preserves_parent_reading_position_and_draft()
    {
        var state = State();
        var next = state.Branch("answer", "b1", 4, 7, new("answer/b1", -32), "还没发送");
        Assert.Equal("蒸汽机", next.Conversation.Source!.Quote);
        Assert.Equal("root", next.Conversation.Source.ConversationId);
        Assert.Equal("answer", next.Conversation.Source.MessageId);
        Assert.Equal(new ReadingPosition("answer/b1", -32), state.Conversations[0].Position);
        Assert.Equal("还没发送", state.Conversations[0].Draft);
        Assert.Same(next.Conversation, state.Current);
    }

    // Catches reusing source positioning for normal navigation.
    [Fact]
    public void Ordinary_switch_restores_target_own_position_without_source_highlight()
    {
        var state = State();
        var child = state.Branch("answer", "b1", 4, 7, new("answer/b1", -20), "parent draft").Conversation;
        state.SwitchTo("root", new("child/middle", -61), "child draft");
        var request = state.SwitchTo(child.Id, new("answer/b2", -17), "new parent draft");
        Assert.Equal(new ReadingPosition("child/middle", -61), request.Position);
        Assert.Equal("child draft", request.Conversation.Draft);
        Assert.Null(request.Highlight);
        Assert.Equal(3, state.Conversations.Count);
    }

    // Catches returning to the saved reading position instead of the exact branch source.
    [Fact]
    public void Return_to_source_requests_exact_selection_and_preserves_child_position()
    {
        var state = State();
        var child = state.Branch("answer", "b1", 4, 7, new("answer/b2", -90), "").Conversation;
        var request = state.ReturnToSource(new("child/end", -8), "pending");
        Assert.Equal("root", request.Conversation.Id);
        Assert.Equal("answer/b1", request.Position.BlockId);
        Assert.Equal("蒸汽机", request.Highlight!.Quote);
        Assert.Equal(new ReadingPosition("child/end", -8), child.Position);
        Assert.Equal("pending", child.Draft);
    }

    // Catches flattening nested branches to the root.
    [Fact]
    public void Nested_branch_returns_to_its_immediate_parent()
    {
        var state = State();
        var child = state.Branch("answer", "b1", 4, 7, new(null, 0), "").Conversation;
        child.Messages.Add(new("child-answer", false, "内燃机在气缸内燃烧燃料。"));
        var grandchild = state.Branch("child-answer", "b0", 0, 3, new(null, 10), "").Conversation;
        Assert.Equal(child.Id, grandchild.Source!.ConversationId);
        var request = state.ReturnToSource(new(null, 0), "");
        Assert.Equal(child.Id, request.Conversation.Id);
        Assert.Equal("内燃机", request.Highlight!.Quote);
    }

    // Catches an accidental blank branch or source range outside its paragraph.
    [Theory]
    [InlineData(3, 3)]
    [InlineData(-1, 4)]
    [InlineData(1, 999)]
    public void Invalid_selection_does_not_create_a_branch(int start, int end)
    {
        var state = State();
        Assert.Throws<ArgumentException>(() => state.Branch("answer", "b1", start, end, new(null, 0), ""));
        Assert.Equal(2, state.Conversations.Count);
        Assert.Equal("root", state.Current.Id);
    }
}
