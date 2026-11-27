using Mindweave.Application.Conversations;
using Mindweave.Application.Exchange;
using Mindweave.Application.Import;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Import;

/// <summary>The rules of comparison for contents (docs/design/sharing-import.md §2), and what a share with content may say (§7).</summary>
public sealed class ImportContentRulesTests
{
    private static readonly WorkspaceId Here = WorkspaceId.New();
    private static readonly WorkspaceId There = WorkspaceId.New();

    private static readonly ExchangeMessage Asked = SharedParts.Question(0, "How did trains begin?");
    private static readonly ExchangeMessage Told = SharedParts.Answer(1, "With steam.");
    private static readonly ExchangeMessage AskedMore = SharedParts.Question(2, "And then?");
    private static readonly ExchangeMessage ToldMore = SharedParts.Answer(3, "Electricity.");

    private static readonly ExchangeConversation Root = SharedParts.Conversation(null, "Trains", null, Asked, Told, AskedMore, ToldMore).With(SharedParts.Summary("Trains", 3));
    private static readonly ExchangeConversation Branch = SharedParts.Conversation(Root, "Steam", SharedParts.From(Told, "steam"), SharedParts.Question(0, "Why?"), SharedParts.Answer(1, "Heat.")).With(SharedParts.Summary("Steam", 1));
    private static readonly ExchangeDocument Document = SharedParts.Document(Branch, Root) with { Links = [new(Root.Id, Branch.Id) { Note = "related" }] };

    [Fact]
    public void Everything_is_new_with_its_link_where_nothing_is_here()
    {
        var plan = Plan(Stored());

        Assert.Equal([Root.Id, Branch.Id], plan.New.Select(c => c.Id));
        Assert.Equal("related", Assert.Single(plan.Links).Note);
        Assert.Empty(plan.Gains);
        Assert.Equal((0, 0, 0), (plan.AlreadyHere, plan.Unfit, plan.Own));
    }

    [Fact]
    public void A_received_conversation_gains_only_what_it_lacks()
    {
        var plan = Plan(Stored((Root, true, false, true, [Asked, Told]), (Branch, true, true, true, [.. Branch.Content!.Messages])), known: [Branch]);

        var gain = Assert.Single(plan.Gains);
        Assert.Equal((Root.Id, false, true), (gain.Conversation.Id, gain.Source, gain.Summary));
        Assert.Equal([AskedMore, ToldMore], gain.Messages);
        Assert.Equal(1, plan.AlreadyHere);
    }

    [Fact]
    public void An_empty_received_branch_gains_its_messages_and_its_source_and_a_stored_source_stays()
    {
        var plan = Plan(Stored((Root, true, false, false, []), (Branch, true, false, false, [])));
        Assert.Equal([Root.Id, Branch.Id], plan.Gains.Select(g => g.Conversation.Id));
        Assert.True(plan.Gains[1].Source);

        var again = Plan(Stored((Root, true, false, true, [.. Root.Content!.Messages]), (Branch, true, true, true, [.. Branch.Content!.Messages])), known: [Root, Branch]);
        Assert.False(again.IsEmpty); // only the link is missing
        Assert.Empty(again.Gains);
        Assert.Equal(2, again.AlreadyHere);
    }

    [Fact]
    public void A_source_waits_until_its_parent_holds_the_message()
    {
        // The parent does not fit, so the message the branch left from does not arrive; the branch itself does.
        var plan = Plan(Stored((Root, true, false, false, [SharedParts.Answer(1, "Something else.")]), (Branch, true, false, false, [])));

        Assert.Equal(1, plan.Unfit);
        Assert.False(Assert.Single(plan.Gains).Source);
    }

    [Fact]
    public void A_number_held_by_another_message_skips_that_conversation_and_nothing_else()
    {
        var plan = Plan(Stored((Root, true, false, false, [Asked, SharedParts.Answer(1, "Another answer.")])));

        Assert.Equal(1, plan.Unfit);
        Assert.Empty(plan.Gains);
        Assert.Equal([Branch.Id], plan.New.Select(c => c.Id));
    }

    /// <summary>
    /// Numbers below what is here can only be attempts that were replaced: anything else would slip into the context of what
    /// the user already continued, which is never rewritten.
    /// </summary>
    [Fact]
    public void Between_what_is_here_only_a_replaced_attempt_arrives()
    {
        var (late, lateAnswer) = (SharedParts.Question(4, "Later?"), SharedParts.Answer(6, "Later."));
        var stored = Stored((Root, true, false, true, [Asked, Told, late, lateAnswer]));
        ImportPlan With(params ExchangeMessage[] messages) =>
            ImportRules.Plan(SharedParts.Document(Root with { Content = Root.Content! with { Messages = messages } }), Held((Root, Here)), Here, stored with { Versions = new Dictionary<SummaryVersionId, ConversationId> { [Root.Content!.Summary!.Id] = Root.Id } })!;

        Assert.Equal(1, With(Asked, Told, AskedMore, ToldMore, late, lateAnswer).Unfit);
        Assert.Equal(1, With(Asked, Told, SharedParts.Question(5, "Between?"), lateAnswer).Unfit);
        Assert.Equal(1, With(Asked, Told, SharedParts.Answer(2, "Another answer to the first."), late, lateAnswer).Unfit);
        Assert.Single(With(Asked, Told, late, SharedParts.Answer(5, "Earlier.") with { ReplacedBy = lateAnswer.Id }, lateAnswer).Gains);
        Assert.Single(With(Asked, Told, late, SharedParts.Answer(5, "It stood when an older file was made.")).Gains);
        Assert.Single(With(Asked, Told, late, lateAnswer, SharedParts.Question(7, "And now?")).Gains);
    }

    /// <summary>Whether it is an answer is asked of what is here, not of what the file says it is.</summary>
    [Fact]
    public void A_sentence_is_never_left_from_a_stored_question()
    {
        var branch = Branch with { Content = Branch.Content! with { Source = SharedParts.From(Asked, "trains") } };
        var stored = Stored((Root, true, false, true, [.. Root.Content!.Messages]), (Branch, true, false, true, [.. Branch.Content!.Messages]));
        var plan = ImportRules.Plan(SharedParts.Document(branch), Held((Root, Here), (Branch, Here)), Here, stored with { Versions = new Dictionary<SummaryVersionId, ConversationId> { [Branch.Content.Summary!.Id] = Branch.Id } })!;
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void An_identity_another_conversation_has_skips_a_new_one_with_its_branches()
    {
        var stored = Stored() with { Messages = new Dictionary<MessageId, ConversationId> { [Told.Id] = ConversationId.New() } };
        var plan = ImportRules.Plan(Document, new Dictionary<ConversationId, WorkspaceId>(), Here, stored)!;
        Assert.Empty(plan.New);
        Assert.Equal(2, plan.Unfit);

        var version = Stored() with { Versions = new Dictionary<SummaryVersionId, ConversationId> { [Branch.Content!.Summary!.Id] = ConversationId.New() } };
        plan = ImportRules.Plan(Document, new Dictionary<ConversationId, WorkspaceId>(), Here, version)!;
        Assert.Equal([Root.Id], plan.New.Select(c => c.Id));
        Assert.Equal(1, plan.Unfit);
        Assert.Empty(plan.Links);
    }

    [Fact]
    public void The_users_own_conversation_never_gains_messages_and_is_counted()
    {
        var plan = Plan(Stored((Root, false, false, false, [Asked, Told])));

        Assert.Equal(1, plan.Own);
        Assert.Empty(plan.Gains);
        Assert.Equal([Branch.Id], plan.New.Select(c => c.Id));
    }

    [Fact]
    public void The_users_own_conversation_gains_a_summary_version_it_lacks()
    {
        var plan = Plan(Stored((Root, false, false, true, [.. Root.Content!.Messages])));

        var gain = Assert.Single(plan.Gains);
        Assert.Equal((Root.Id, true), (gain.Conversation.Id, gain.Summary));
        Assert.Empty(gain.Messages);
        Assert.Equal(0, plan.Own);
    }

    [Fact]
    public void The_users_own_branch_never_gains_a_source()
    {
        var plan = Plan(Stored((Root, true, false, true, [.. Root.Content!.Messages]), (Branch, false, false, true, [.. Branch.Content!.Messages])), known: [Root, Branch]);
        Assert.Empty(plan.Gains);
    }

    [Fact]
    public void A_link_is_added_only_where_both_have_a_summary_and_none_joins_them()
    {
        Assert.Empty(Plan(Stored((Root, true, false, true, [.. Root.Content!.Messages]), (Branch, true, true, true, [.. Branch.Content!.Messages])) with
        {
            Linked = new HashSet<(ConversationId, ConversationId)> { StoredContents.Pair(Branch.Id, Root.Id) },
        }, known: [Root, Branch]).Links);

        var without = Document with { Conversations = [Root, Branch with { Content = Branch.Content! with { Summary = null } }] };
        Assert.Empty(ImportRules.Plan(without, new Dictionary<ConversationId, WorkspaceId>(), Here, Stored())!.Links);

        // A structure-only share brings the line, without the note, to summaries that are here.
        var bare = ImportRules.Plan(Document.Structure(), Held((Root, Here), (Branch, Here)), Here, Stored((Root, true, false, true, []), (Branch, true, false, true, [])))!;
        Assert.Null(Assert.Single(bare.Links).Note);
        Assert.Empty(bare.Gains);
    }

    /// <summary>Acceptance 13.</summary>
    [Fact]
    public void What_lives_elsewhere_gains_nothing_and_brings_no_link()
    {
        var plan = ImportRules.Plan(Document, Held((Root, There)), Here, Stored((Root, true, false, false, [])))!;

        Assert.Equal(new ImportSkipped(There, 2), Assert.Single(plan.Skipped));
        Assert.True(plan.IsEmpty);
    }

    public static TheoryData<string> Untrue =>
    [
        "a message twice", "a number twice", "numbers that fall", "an answer first", "two standing answers", "replaced by another question's answer", "a standing answer that is replaced",
        "an empty question", "an empty answer", "an unfinished question", "a question with citations", "an answer with attachments", "a number cited twice", "a question too long",
        "a root with a source", "a source that is not in the parent", "a sentence of a question", "a version twice", "a conversation without content", "a link to itself", "a note too long",
    ];

    [Theory]
    [MemberData(nameof(Untrue))]
    public void What_cannot_be_true_is_not_sound(string what)
    {
        Assert.True(ImportRules.IsSound(Document));
        ExchangeDocument With(params ExchangeMessage[] messages) => SharedParts.Document(SharedParts.Conversation(null, "x", null, messages));
        var retried = SharedParts.Answer(2, "Another.");
        var document = what switch
        {
            "a message twice" => SharedParts.Document(Root, SharedParts.Conversation(null, "x", null, Asked)),
            "a number twice" => With(Asked, Told, AskedMore with { Seq = 1 }),
            "numbers that fall" => With(Asked, AskedMore, Told),
            "an answer first" => With(Told),
            "two standing answers" => With(Asked, Told, retried),
            "replaced by another question's answer" => With(Asked, Told with { ReplacedBy = ToldMore.Id }, AskedMore, ToldMore),
            "a standing answer that is replaced" => With(Asked, Told with { ReplacedBy = retried.Id }, retried with { ReplacedBy = Told.Id }),
            "an empty question" => With(Asked with { Text = " " }),
            "an empty answer" => With(Asked, Told with { Text = "\n" }),
            "an unfinished question" => With(Asked with { State = MessageState.Stopped }),
            "a question with citations" => With(Asked with { Citations = [new(1, CitationKind.Web)] }),
            "an answer with attachments" => With(Asked, Told with { Attachments = ["a.txt"] }),
            "a number cited twice" => With(Asked, Told with { Citations = [new(1, CitationKind.Web), new(1, CitationKind.Summary)] }),
            "a question too long" => With(Asked with { Text = new string('x', ConversationService.MaxQuestionLength + 1) }),
            "a root with a source" => SharedParts.Document(Root with { Content = Root.Content! with { Source = SharedParts.From(Told, "steam") } }),
            "a source that is not in the parent" => SharedParts.Document(Root, Branch with { Content = Branch.Content! with { Source = new(MessageId.New(), "steam", null, null) } }),
            "a sentence of a question" => SharedParts.Document(Root, Branch with { Content = Branch.Content! with { Source = SharedParts.From(Asked, "trains") } }),
            "a version twice" => SharedParts.Document(Root, Branch.With(Root.Content!.Summary!)),
            "a conversation without content" => Document with { Conversations = [Root, Branch with { Content = null }] },
            "a link to itself" => Document with { Links = [new(Root.Id, Root.Id)] },
            _ => Document with { Links = [new(Root.Id, Branch.Id) { Note = new string('x', 17) }] },
        };
        Assert.False(ImportRules.IsSound(document));
    }

    [Fact]
    public void An_earlier_attempt_names_the_answer_that_stands_however_many_were_replaced()
    {
        var (first, second, third) = (SharedParts.Answer(1, "One."), SharedParts.Answer(2, "Two."), SharedParts.Answer(3, "Three."));
        Assert.True(ImportRules.IsSound(SharedParts.Document(SharedParts.Conversation(null, "x", null, Asked, first with { ReplacedBy = third.Id }, second with { ReplacedBy = third.Id }, third))));
    }

    /// <summary>A continuation left from a question too, when nothing answers it.</summary>
    [Fact]
    public void A_source_without_a_sentence_can_be_any_message_of_the_parent()
    {
        Assert.True(ImportRules.IsSound(SharedParts.Document(Root, Branch with { Content = Branch.Content! with { Source = new(Asked.Id, null, null, null) } })));
    }

    private static ImportPlan Plan(StoredContents stored, IReadOnlyList<ExchangeConversation>? known = null) =>
        ImportRules.Plan(Document, stored.Conversations.Keys.ToDictionary(id => id, _ => Here), Here, stored with
        {
            Versions = (known ?? []).ToDictionary(c => c.Content!.Summary!.Id, c => c.Id),
        })!;

    private static Dictionary<ConversationId, WorkspaceId> Held(params (ExchangeConversation Conversation, WorkspaceId Workspace)[] held) =>
        held.ToDictionary(h => h.Conversation.Id, h => h.Workspace);

    private static StoredContents Stored(params (ExchangeConversation Conversation, bool Received, bool Source, bool Summary, ExchangeMessage[] Messages)[] held) =>
        StoredContents.Nothing with
        {
            Conversations = held.ToDictionary(h => h.Conversation.Id, h => new StoredConversation(h.Conversation.Parent, h.Received, h.Source, h.Summary, h.Messages.ToDictionary(m => m.Seq, m => new StoredMessage(m.Id, m.Role)))),
            Messages = held.SelectMany(h => h.Messages.Select(m => (m.Id, h.Conversation.Id))).ToDictionary(m => m.Item1, m => m.Item2),
        };
}
