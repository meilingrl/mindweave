namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// 设置 › 工作区 says how many of a workspace's conversations are archived (2026-11-10): those with an archive row and
/// the branches hidden under one, counted among all its conversations.
/// </summary>
public class WorkspaceArchivedCountTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Archived_counts_archived_conversations_and_the_branches_hidden_under_them()
    {
        await using var h = await ChatHarness.StartAsync();
        var other = await WorkspaceSeed.AddWorkspaceAsync(h, "摄影史");
        var root = await WorkspaceSeed.FillAsync(h, h.Workspace);
        await WorkspaceSeed.FillAsync(h, other);

        // The seed's tree: a root, its branch, and an archived grandchild.
        var seeded = await h.WorkspaceDeletion.InspectAsync(h.Workspace, Ct);
        Assert.Equal((3, 1), (seeded.Conversations, seeded.Archived));

        // Archiving the root hides its whole tree; the other workspace's count is its own.
        Assert.True((await h.Conversations.ArchiveAsync(root, Ct)).Archived);
        var archived = await h.WorkspaceDeletion.InspectAsync(h.Workspace, Ct);
        Assert.Equal((3, 3), (archived.Conversations, archived.Archived));
        Assert.Equal(1, (await h.WorkspaceDeletion.InspectAsync(other, Ct)).Archived);

        // Restoring brings them back; the grandchild keeps its own archive row.
        await h.Conversations.RestoreAsync(root, Ct);
        Assert.Equal(1, (await h.WorkspaceDeletion.InspectAsync(h.Workspace, Ct)).Archived);
    }
}
