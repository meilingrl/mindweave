using Mindweave.Application.Exchange;
using Mindweave.Application.Share;
using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Tests.Share;

/// <summary>What a share with content holds (docs/design/sharing.md S6, S7, §6.6 points 3 and 4, §9), decided where the document is built.</summary>
public sealed class ExchangeContentTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 18, 19, 42, 0, TimeSpan.FromHours(8));

    private static readonly ConversationId RootId = ConversationId.New();
    private static readonly ConversationId BranchId = ConversationId.New();
    private static readonly ConversationId OtherId = ConversationId.New();
    private static readonly SummaryNodeId RootNode = SummaryNodeId.New();
    private static readonly SummaryNodeId BranchNode = SummaryNodeId.New();
    private static readonly SummaryNodeId OtherNode = SummaryNodeId.New();

    private static readonly ShareSource Source = new(
        [
            new ShareableConversation(RootId, null, null, Day, 9, true, false) { ContentName = "When did trains…" },
            new ShareableConversation(BranchId, RootId, null, Day.AddMinutes(9), 9, true, false) { ContentName = "“older than locomotives”" },
            new ShareableConversation(OtherId, RootId, "Standard gauge", Day.AddMinutes(20), 9, true, false) { ContentName = "Standard gauge" },
        ],
        [new ExchangeLink(RootId, BranchId), new ExchangeLink(RootId, OtherId)]);

    private static ShareSelection WithoutOther => ShareSelection.Exploration(Source.Parents, RootId).Untick(OtherId);

    private int _seq;

    private Message Question(string text, params ContentBlock[] attachments) =>
        new(MessageId.New(), RootId, _seq++, MessageRole.User, [.. attachments, new TextBlock(text)], MessageState.Complete, 0, null, null, null, Day, Day);

    private Message Answer(string text, MessageState state = MessageState.Complete, MessageId? supersedes = null, params ContentBlock[] more) =>
        new(MessageId.New(), RootId, _seq++, MessageRole.Assistant, [new TextBlock(text), .. more], state, 0, supersedes,
            state is MessageState.Failed ? GenerationErrorKind.Network : null, "PLANTEDDETAIL", Day, state is MessageState.Pending or MessageState.Streaming ? null : Day.AddSeconds(5));

    private static BranchSource From(Message source, string quote, string anchor = "{}", ConversationId? child = null) =>
        new(child ?? BranchId, RootId, source.Id, 0, quote, anchor, 1, "profile-1", Day);

    private static ExchangeDocument Build(IReadOnlyList<Message> messages, BranchSource? branch = null, BranchSource? other = null, int? offset = null, bool otherToo = false, IReadOnlyList<SummaryLink>? links = null) =>
        ExchangeDocument.Content(
            " Trains ", Source, otherToo ? ShareSelection.Exploration(Source.Parents, RootId) : WithoutOther,
            new ShareContents(
                new Dictionary<ConversationId, ShareableContent>
                {
                    [RootId] = new(messages) { SummaryNode = RootNode, Summary = Version(RootNode, "Root summary") },
                    [BranchId] = new([]) { Source = branch, SourceOffset = offset, SummaryNode = BranchNode, Summary = Version(BranchNode, "Branch summary") },
                    [OtherId] = new([]) { Source = other, SummaryNode = OtherNode, Summary = Version(OtherNode, "Other summary") },
                },
                links ?? []),
            Day.AddDays(7), "0.7.0");

    private static SummaryVersion Version(SummaryNodeId node, string title) =>
        new(SummaryVersionId.New(), node, 1, title, "Body of " + title, 5, SummaryOrigin.Generated, null, null, "PLANTEDMODEL", Day);

    private static IReadOnlyList<ExchangeMessage> Shared(ExchangeDocument document) => document.Conversations.Single(c => c.Id == RootId).Content!.Messages;

    [Fact]
    public void It_is_of_kind_content_and_its_names_are_the_apps()
    {
        var document = Build([Question("Q"), Answer("A")]);

        Assert.Equal("content", document.Kind);
        Assert.Equal("Trains", document.Title);
        Assert.Equal(["When did trains…", "“older than locomotives”"], document.Conversations.Select(c => c.Name));
    }

    [Fact]
    public void Messages_come_in_order_with_their_identity_role_time_and_markdown()
    {
        var question = Question("When did **trains** begin?");
        var answer = Answer("In *1804*.");

        Assert.Equal(
            [
                new ExchangeMessage(question.Id, MessageRole.User, Day.ToUniversalTime(), MessageState.Complete, "When did **trains** begin?"),
                new ExchangeMessage(answer.Id, MessageRole.Assistant, Day.AddSeconds(5).ToUniversalTime(), MessageState.Complete, "In *1804*."),
            ],
            Shared(Build([question, answer])));
    }

    [Fact]
    public void The_numbers_of_questions_are_those_of_the_shared_questions()
    {
        Assert.Equal(2, Build([Question("1"), Answer("a"), Question("2"), Answer("b")]).Questions);
    }

    [Fact]
    public void Of_a_retried_answer_only_the_one_that_stands_is_in_it()
    {
        var first = Answer("first");
        var second = Answer("second", supersedes: first.Id);

        Assert.Equal(["Q", "second"], Shared(Build([Question("Q"), first, second])).Select(m => m.Text));
    }

    [Fact]
    public void An_earlier_attempt_a_shared_branch_left_from_is_kept_and_says_what_replaced_it()
    {
        var first = Answer("first one");
        var second = Answer("second", supersedes: first.Id);
        var third = Answer("third", supersedes: second.Id);

        var shared = Shared(Build([Question("Q"), first, second, third], From(first, "first")));

        Assert.Equal(["Q", "first one", "third"], shared.Select(m => m.Text));
        Assert.Equal([null, third.Id, (MessageId?)null], shared.Select(m => m.ReplacedBy));
    }

    [Fact]
    public void An_earlier_attempt_only_an_unticked_branch_left_from_is_not()
    {
        var first = Answer("first");
        var second = Answer("second", supersedes: first.Id);

        Assert.Equal(["Q", "second"], Shared(Build([Question("Q"), first, second], other: From(first, "first", child: OtherId))).Select(m => m.Text));
    }

    /// <summary>§9, as the structure-only share counts it: up to the last finished answer.</summary>
    [Fact]
    public void A_question_still_being_answered_is_left_out_with_its_answer()
    {
        var shared = Shared(Build([Question("1"), Answer("a"), Question("2"), Answer("so far", MessageState.Streaming)]));

        Assert.Equal(["1", "a"], shared.Select(m => m.Text));
    }

    [Fact]
    public void While_an_answer_is_being_tried_again_the_earlier_one_stands()
    {
        var question = Question("Q");
        var first = Answer("first");

        Assert.Equal(["Q", "first"], Shared(Build([question, first, Answer("so far", MessageState.Streaming, first.Id)])).Select(m => m.Text));
    }

    [Fact]
    public void A_stopped_answer_and_a_failed_one_with_text_are_kept_with_their_state_and_no_error_text()
    {
        var shared = Shared(Build([Question("1"), Answer("part", MessageState.Stopped), Question("2"), Answer("cut", MessageState.Failed)]));

        Assert.Equal(["1", "part", "2", "cut"], shared.Select(m => m.Text));
        Assert.Equal([MessageState.Complete, MessageState.Stopped, MessageState.Complete, MessageState.Failed], shared.Select(m => m.State));
        Assert.Equal([false, true, false, true], shared.Select(m => m.IsIncomplete));
    }

    [Fact]
    public void A_failed_answer_with_no_text_is_left_out_and_the_answer_it_tried_again_stands()
    {
        var first = Answer("first");
        var shared = Shared(Build([Question("1"), Answer(" ", MessageState.Failed), Question("2"), first, Answer("", MessageState.Failed, first.Id)]));

        Assert.Equal(["1", "2", "first"], shared.Select(m => m.Text));
    }

    [Fact]
    public void An_attachment_is_its_file_name_and_a_paste_has_none()
    {
        var question = Question("Q",
            new AttachmentBlock(ResourceId.New(), "gauge.pdf", "application/pdf", 5, "PLANTEDTEXT"),
            new AttachmentBlock(ResourceId.New(), null, "text/plain", 5, "PLANTEDPASTE"));
        var document = Build([question, Answer("A")]);

        Assert.Equal(["gauge.pdf", null], Shared(document)[0].Attachments);
        Assert.Equal(["gauge.pdf", null], document.Attachments);
    }

    [Fact]
    public void Tool_steps_are_one_count_by_kind_and_nothing_else()
    {
        ToolCallBlock Call(string tool) => new("c", tool, "PLANTEDARGUMENTS", ToolRisk.LocalRead, "PLANTEDSERVER › x");
        var answer = Answer("A", more:
        [
            Call("web_search"), Call("web_search"), Call("fetch_page"), Call("search_attachments"), Call("search_summaries"), Call("files__echo"), Call("files__note"),
            new ToolResultBlock("c", ToolResultStatus.Succeeded, "PLANTEDRESULT", ResourceId.New(), "PLANTEDDETAIL"),
        ]);

        Assert.Equal(new ExchangeActivity(AttachmentSearches: 1, SummarySearches: 1, WebSearches: 2, PagesRead: 1, OtherCalls: 2), Shared(Build([Question("Q"), answer]))[1].Activity);
        Assert.Null(Shared(Build([Question("Q"), Answer("A")]))[1].Activity);
    }

    [Fact]
    public void A_web_citation_keeps_its_number_title_and_address_and_the_users_own_material_only_its_kind()
    {
        CitationBlock Cite(int n, CitationKind kind, string? url = null, string? target = null) =>
            new(n, "Title " + n, url is null ? null : new Uri(url), Day, ResourceId.New(), 0, 5) { Kind = kind, Target = target };
        var answer = Answer("A [1]", more:
        [
            Cite(1, CitationKind.Web, "https://example.org/rail?a=1"),
            Cite(2, CitationKind.Attachment),
            Cite(3, CitationKind.Material),
            Cite(4, CitationKind.Summary, target: OtherNode.ToString()),
            Cite(5, CitationKind.Summary, target: BranchNode.ToString()),
            Cite(6, CitationKind.Summary),
            Cite(7, CitationKind.Material, "https://example.org/kept-home"),
            Cite(8, CitationKind.Web, "file:///Users/someone/secret.txt"),
        ]);

        var document = Build([Question("Q"), answer]);

        Assert.Equal(
            [
                new ExchangeCitation(1, CitationKind.Web, "Title 1", new Uri("https://example.org/rail?a=1")),
                new ExchangeCitation(2, CitationKind.Attachment),
                new ExchangeCitation(3, CitationKind.Material),
                new ExchangeCitation(4, CitationKind.Summary),

                // The title it has in the share, not the one it had when it was cited (an earlier version's).
                new ExchangeCitation(5, CitationKind.Summary, "Branch summary", Summary: BranchId),
                new ExchangeCitation(6, CitationKind.Summary),
                new ExchangeCitation(7, CitationKind.Material),
                new ExchangeCitation(8, CitationKind.Web),
            ],
            Shared(document)[1].Citations);
        Assert.Equal(1, document.WebCitations);
    }

    [Fact]
    public void A_branch_carries_the_answer_it_left_from_its_quote_and_its_place()
    {
        var answer = Answer("Tracks are older than locomotives.");
        var source = Build([Question("Q"), answer], From(answer, "older than locomotives", """{"version":1,"segments":[{"start":11,"quote":"older than locomotives"}]}"""), offset: 11)
            .Conversations.Single(c => c.Id == BranchId).Content!.Source!;

        Assert.Equal((answer.Id, "older than locomotives"), (source.Message, source.Quote));
        Assert.Equal(new ExchangePosition(0, 11), source.Position);
        Assert.Equal(new ExchangeAnchor(1, "profile-1", """{"version":1,"segments":[{"start":11,"quote":"older than locomotives"}]}"""), source.Anchor);
    }

    [Theory]
    [InlineData("""{"hash":"AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34","q":"older"}""", true)] // A hash is not text.
    [InlineData("""{"segments":[{"quote":" older than "},{"quote":"\uFFFC"},{"readable":null}]}""", true)]
    [InlineData("""{"segments":[{"quote":"older"}],"context":"Tracks are older"}""", false)]
    [InlineData("""{"Tracks are":1}""", false)]
    [InlineData("""["older", "locomotives and more"]""", false)]
    [InlineData("Tracks are older", false)]
    public void The_stored_anchor_travels_only_when_it_holds_no_text_beyond_the_quote(string anchor, bool travels)
    {
        var answer = Answer("Tracks are older than locomotives.");
        var source = Build([Question("Q"), answer], From(answer, "older than locomotives", anchor)).Conversations.Single(c => c.Id == BranchId).Content!.Source!;

        Assert.Equal(travels, source.Anchor is not null);
        Assert.Null(source.Position);
    }

    [Fact]
    public void The_current_summary_is_its_identity_title_body_and_time()
    {
        var summary = Build([Question("Q"), Answer("A")]).Conversations.Single(c => c.Id == RootId).Content!.Summary!;

        Assert.Equal(("Root summary", "Body of Root summary", Day.ToUniversalTime()), (summary.Title, summary.Body, summary.At));
    }

    [Fact]
    public void A_link_has_its_note_and_is_there_only_between_two_shared_conversations()
    {
        SummaryLink Link(SummaryNodeId a, SummaryNodeId b, string note) => new(SummaryLinkId.New(), WorkspaceId.New(), a, b, note, SummaryLinkOrigin.User, Day);
        var links = new[] { Link(RootNode, BranchNode, "same age"), Link(RootNode, OtherNode, "PLANTED") };

        Assert.Equal([new ExchangeLink(RootId, BranchId) { Note = "same age" }], Build([Question("Q"), Answer("A")], links: links).Links);
        Assert.Equal(2, Build([Question("Q"), Answer("A")], otherToo: true, links: links).Links.Count);
    }

    [Fact]
    public void An_unticked_conversation_is_nowhere_in_it()
    {
        var document = Build([Question("Q"), Answer("A")], other: From(Answer("x"), "x", child: OtherId));

        Assert.Equal([RootId, BranchId], document.Conversations.Select(c => c.Id));
    }

    [Fact]
    public void A_conversation_deleted_since_the_list_was_read_is_left_out_with_its_branches()
    {
        var document = ExchangeDocument.Content(
            "T", Source, WithoutOther, new ShareContents(new Dictionary<ConversationId, ShareableContent> { [BranchId] = new([]) }, []), Day, "1");

        Assert.Empty(document.Conversations);
    }

    [Fact]
    public void Contents_are_the_same_only_when_every_part_is()
    {
        var question = Question("Q");
        var answer = Answer("A");

        var one = Build([question, answer]);
        Assert.True(one.SameContents(one with { Title = "x", Conversations = [.. one.Conversations] }));
        var root = one.Conversations[0];
        var same = root with { Content = root.Content! with { Messages = [.. root.Content.Messages.Select(m => m with { Attachments = [.. m.Attachments] })] } };
        var changed = root with { Content = root.Content! with { Messages = [.. root.Content.Messages.Select(m => m with { Text = "other" })] } };
        Assert.True(one.SameContents(one with { Conversations = [same, .. one.Conversations.Skip(1)] }));
        Assert.False(one.SameContents(one with { Conversations = [changed, .. one.Conversations.Skip(1)] }));
    }

    /// <summary>A source read for content never changes what a structure-only share names (§6.6, point 1).</summary>
    [Fact]
    public void A_structure_only_share_of_the_same_source_has_neither_content_nor_the_apps_fallback_names()
    {
        var document = ExchangeDocument.Structure("T", Source, WithoutOther, Day, "1");

        Assert.All(document.Conversations, c => Assert.Null(c.Content));
        Assert.All(document.Conversations, c => Assert.Null(c.Name));
        Assert.All(document.Links, l => Assert.Null(l.Note));
    }
}
