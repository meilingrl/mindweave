using Mindweave.Application.Storage;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>Renaming a workspace (2026-11-10) through the real service and SQLite: only its name changes.</summary>
public class WorkspaceRenameTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WorkspaceName Name(string raw)
    {
        Assert.True(WorkspaceName.TryCreate(raw, out var name, out _));
        return name!;
    }

    [Fact]
    public async Task Renaming_stores_the_new_name_and_keeps_its_identity_age_and_everything_in_it()
    {
        await using var h = await ChatHarness.StartAsync();
        var other = await WorkspaceSeed.AddWorkspaceAsync(h, "摄影史");
        await WorkspaceSeed.FillAsync(h, h.Workspace);
        var listed = await h.Workspaces.ListAsync(Ct);
        var before = listed.Single(w => w.Id == h.Workspace);
        var contents = await h.WorkspaceDeletion.InspectAsync(h.Workspace, Ct);

        var renamed = await h.WorkspaceService.RenameAsync(h.Workspace, Name("  蒸汽   机车 "), Ct);

        Assert.NotNull(renamed);
        Assert.Equal((h.Workspace, "蒸汽 机车", before.CreatedAt), (renamed.Id, renamed.Name.Value, renamed.CreatedAt));
        var after = await h.Workspaces.ListAsync(Ct);
        Assert.Equal(listed.Select(w => w.Id), after.Select(w => w.Id));
        Assert.Equal(renamed, after.Single(w => w.Id == h.Workspace));
        Assert.Equal(listed.Single(w => w.Id == other), after.Single(w => w.Id == other));
        var now = await h.WorkspaceDeletion.InspectAsync(h.Workspace, Ct);
        Assert.Equal((contents.Exists, contents.Conversations, contents.Summaries), (now.Exists, now.Conversations, now.Summaries));
    }

    [Fact]
    public async Task A_name_already_in_use_is_accepted_as_creating_accepts_it()
    {
        await using var h = await ChatHarness.StartAsync();
        await WorkspaceSeed.AddWorkspaceAsync(h, "摄影史");

        var renamed = await h.WorkspaceService.RenameAsync(h.Workspace, Name("摄影史"), Ct);

        Assert.NotNull(renamed);
        Assert.Equal(["摄影史", "摄影史"], (await h.Workspaces.ListAsync(Ct)).Select(w => w.Name.Value));
    }

    [Fact]
    public async Task Renaming_a_workspace_that_is_gone_changes_nothing()
    {
        await using var h = await ChatHarness.StartAsync();
        var before = await h.Workspaces.ListAsync(Ct);

        Assert.Null(await h.WorkspaceService.RenameAsync(WorkspaceId.New(), Name("不存在"), Ct));

        Assert.Equal(before, await h.Workspaces.ListAsync(Ct));
    }

    [Fact]
    public async Task Renaming_is_refused_while_storage_is_paused()
    {
        await using var h = await ChatHarness.StartAsync();
        var before = await h.Workspaces.ListAsync(Ct);

        h.Health.Report(new StorageWriteException(StorageFailureKind.Full, "disk full", null));
        await Assert.ThrowsAsync<StorageWriteException>(() => h.WorkspaceService.RenameAsync(h.Workspace, Name("新名字"), Ct));

        Assert.Equal(before, await h.Workspaces.ListAsync(Ct));
    }
}
