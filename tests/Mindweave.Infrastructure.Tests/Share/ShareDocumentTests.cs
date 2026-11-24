using Mindweave.Application.Share;
using Mindweave.Core.Conversations;

namespace Mindweave.Infrastructure.Tests.Share;

/// <summary>What a structure-only share holds (docs/design/sharing.md §6.1, S6, S7): only what is ticked.</summary>
public sealed class ShareDocumentTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 18, 19, 42, 0, TimeSpan.FromHours(8));

    private static readonly ShareableConversation Root = new(ConversationId.New(), null, "History of trains", Day, 3, true, false);
    private static readonly ShareableConversation Kept = new(ConversationId.New(), Root.Id, null, Day.AddMinutes(9), 1, true, false);
    private static readonly ShareableConversation Left = new(ConversationId.New(), Root.Id, "Standard gauge", Day.AddMinutes(20), 5, true, false);

    private static readonly ShareSource Source = new(
        [Left, Kept, Root],
        [new ShareLink(Kept.Id, Root.Id), new ShareLink(Root.Id, Left.Id)]);

    private static ShareSelection Selection => ShareSelection.Exploration(Source.Parents, Root.Id).Untick(Left.Id);

    private static ShareDocument Build() => ShareDocument.Structure("  Trains ", Source, Selection, Day.AddDays(7), "0.7.0");

    [Fact]
    public void It_names_its_format_version_and_kind_and_what_made_it()
    {
        var document = Build();

        Assert.Equal("mindweave-export", ShareDocument.Format);
        Assert.Equal(1, ShareDocument.Version);
        Assert.Equal("structure", document.Kind);
        Assert.Equal("Trains", document.Title);
        Assert.Equal(Day.AddDays(7).ToUniversalTime(), document.MadeAt);
        Assert.Equal(TimeSpan.Zero, document.MadeAt.Offset);
        Assert.Equal("0.7.0", document.AppVersion);
    }

    [Fact]
    public void It_holds_the_ticked_conversations_in_the_order_they_started_with_their_stored_identities()
    {
        Assert.Equal(
            [
                new SharedConversation(Root.Id, null, "History of trains", Day.ToUniversalTime(), 3, true),
                new SharedConversation(Kept.Id, Root.Id, null, Day.AddMinutes(9).ToUniversalTime(), 1, true),
            ],
            Build().Conversations);
    }

    [Fact]
    public void Start_times_are_instants_without_the_sharers_zone()
    {
        Assert.All(Build().Conversations, c => Assert.Equal(TimeSpan.Zero, c.StartedAt.Offset));
    }

    [Fact]
    public void A_link_is_in_it_only_between_two_shared_conversations()
    {
        Assert.Equal([new ShareLink(Kept.Id, Root.Id)], Build().Links);
    }

    [Fact]
    public void Its_counts_cover_only_what_is_shared()
    {
        var document = Build();

        Assert.Equal(1, document.Explorations);
        Assert.Equal(4, document.Questions);
    }

    [Fact]
    public void Two_documents_of_the_same_things_have_the_same_contents_whenever_they_were_made()
    {
        var later = ShareDocument.Structure("Another title", Source, Selection, Day.AddDays(8), "0.7.1");
        var renamed = ShareDocument.Structure("Trains", Source with { Conversations = [Root with { Name = "Trains" }, Kept, Left] }, Selection, Day, "0.7.0");

        Assert.True(Build().SameContents(later));
        Assert.False(Build().SameContents(renamed));
    }
}
