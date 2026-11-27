using Mindweave.Application.Exchange;
using Mindweave.Application.Import;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Tests.Import;

/// <summary>What an import would do, by identity (docs/design/sharing-import.md §2): a shared file only adds.</summary>
public sealed class ImportRulesTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 18, 11, 42, 0, TimeSpan.Zero);
    private static readonly WorkspaceId Here = WorkspaceId.New();
    private static readonly WorkspaceId There = WorkspaceId.New();

    private static readonly ExchangeConversation Root = New(null);
    private static readonly ExchangeConversation Branch = New(Root.Id);
    private static readonly ExchangeConversation Twig = New(Branch.Id);
    private static readonly ExchangeConversation Other = New(null);

    // Out of order on purpose: a file need not name a parent before its branches.
    private static readonly ExchangeDocument Document = new(ExchangeDocument.StructureKind, "Trains", Day, "0.7.0", [Twig, Other, Branch, Root], []);

    [Fact]
    public void Everything_the_store_lacks_is_new_and_parents_come_first()
    {
        var plan = Plan(Here);

        Assert.Equal(new HashSet<ConversationId> { Root.Id, Branch.Id, Twig.Id, Other.Id }, plan.New.Select(c => c.Id).ToHashSet());
        var order = plan.New.Select(c => c.Id).ToList();
        Assert.True(order.IndexOf(Root.Id) < order.IndexOf(Branch.Id) && order.IndexOf(Branch.Id) < order.IndexOf(Twig.Id));
        Assert.Equal(0, plan.AlreadyHere);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void What_is_in_the_chosen_workspace_is_already_here_and_its_new_branches_are_added()
    {
        var plan = Plan(Here, (Root.Id, Here), (Branch.Id, Here));

        Assert.Equal([Twig.Id, Other.Id], plan.New.Select(c => c.Id).Order(Comparer<ConversationId>.Create((a, b) => (a == Other.Id).CompareTo(b == Other.Id))));
        Assert.Equal(2, plan.AlreadyHere);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void What_lives_in_another_workspace_is_skipped_with_its_branches_and_that_workspace_is_named()
    {
        var plan = Plan(Here, (Root.Id, There));

        Assert.Equal([Other.Id], plan.New.Select(c => c.Id));
        Assert.Equal(new ImportSkipped(There, 3), Assert.Single(plan.Skipped));
    }

    [Fact]
    public void Only_a_branch_that_lives_elsewhere_is_skipped_when_its_parent_does_not()
    {
        var plan = Plan(Here, (Branch.Id, There));

        Assert.Equal(new HashSet<ConversationId> { Root.Id, Other.Id }, plan.New.Select(c => c.Id).ToHashSet());
        Assert.Equal(new ImportSkipped(There, 2), Assert.Single(plan.Skipped));
    }

    [Fact]
    public void In_a_new_workspace_whatever_exists_anywhere_is_skipped()
    {
        var plan = Plan(null, (Root.Id, Here), (Other.Id, There));

        Assert.Empty(plan.New);
        Assert.Equal(0, plan.AlreadyHere);
        Assert.Equal(new HashSet<ImportSkipped> { new(Here, 3), new(There, 1) }, plan.Skipped.ToHashSet());
    }

    [Fact]
    public void A_branch_whose_parent_is_only_in_the_store_hangs_off_it_there()
    {
        var document = Document with { Conversations = [Branch] };
        Assert.Equal([Root.Id], ImportRules.ParentsOutside(document));

        Assert.Equal([Branch.Id], ImportRules.Plan(document, Holdings((Root.Id, Here)), Here)!.New.Select(c => c.Id));
        Assert.Equal(new ImportSkipped(There, 1), Assert.Single(ImportRules.Plan(document, Holdings((Root.Id, There)), Here)!.Skipped));
    }

    [Fact]
    public void A_parent_neither_in_the_file_nor_in_the_store_does_not_fit()
    {
        Assert.Null(ImportRules.Plan(Document with { Conversations = [Branch] }, Holdings(), Here));
    }

    [Fact]
    public void The_workspace_offered_first_holds_most_of_the_files_conversations()
    {
        Assert.Equal(There, ImportRules.Home(Document, Holdings((Root.Id, There), (Branch.Id, There), (Other.Id, Here))));
        Assert.Null(ImportRules.Home(Document, Holdings()));

        // A parent that is not itself in the file does not count.
        Assert.Null(ImportRules.Home(Document with { Conversations = [Branch] }, Holdings((Root.Id, There))));
    }

    private static ExchangeConversation New(ConversationId? parent) => new(ConversationId.New(), parent, null, Day, 0, false);

    private static Dictionary<ConversationId, WorkspaceId> Holdings(params (ConversationId, WorkspaceId)[] held) => held.ToDictionary(h => h.Item1, h => h.Item2);

    private static ImportPlan Plan(WorkspaceId? target, params (ConversationId, WorkspaceId)[] held) =>
        ImportRules.Plan(Document, Holdings(held), target)!;
}
