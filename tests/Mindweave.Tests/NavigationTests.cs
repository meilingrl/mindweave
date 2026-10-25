using Mindweave.Application.Navigation;
using Mindweave.Desktop;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Xunit;

namespace Mindweave.Tests;

public sealed class NavigationTests
{
    [Fact]
    public async Task First_launch_guides_creation_then_reopens_last_workspace_and_area()
    {
        using var dir = new TestDirectory();
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        Guid id;
        await using (var store = await SqliteWorkspaceStore.OpenAsync(paths))
        {
            var main = CompositionRoot.CreateMain(store);
            await main.InitializeAsync();
            Assert.True(main.NeedsWorkspace);
            main.NewWorkspaceName = "火车历史";
            await main.CreateWorkspaceAsync();
            Assert.False(main.NeedsWorkspace);
            id = main.CurrentWorkspace!.Id;
            Assert.Equal("", main.NewWorkspaceName);
            await main.NavigateAsync(new(MainArea.Network));
            await main.NavigateAsync(new(MainArea.Settings));
        }
        await using var reopened = await SqliteWorkspaceStore.OpenAsync(paths);
        var restored = CompositionRoot.CreateMain(reopened);
        await restored.InitializeAsync();
        Assert.Equal(id, restored.CurrentWorkspace!.Id);
        Assert.Equal(MainArea.Settings, restored.CurrentArea);
        Assert.Single(restored.ContentStack);
    }

    [Fact]
    public async Task Back_histories_are_scoped_to_workspace_and_menu_with_one_active_content_stack()
    {
        using var dir = new TestDirectory();
        await using var store = await SqliteWorkspaceStore.OpenAsync(AppPaths.ForTrack("codex-steady", dir.Path));
        var main = CompositionRoot.CreateMain(store);
        await main.InitializeAsync();
        main.NewWorkspaceName = "主题 A";
        await main.CreateWorkspaceAsync();
        var a = main.CurrentWorkspace!.Id;
        // S1 requires a real same-workspace conversation. Summary remains an explicit future placeholder.
        var conversation = (await store.CreateConversationAsync(a)).Id;
        var summary = Guid.NewGuid();
        await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, conversation));
        await main.NavigateAsync(new(MainArea.Network));
        await main.NavigateAsync(new(MainArea.Network, ContentKind.Summary, summary));
        await main.NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, conversation, NavigationIntent.ReturnToSource));
        Assert.Equal(conversation, main.ActiveContent!.Request.TargetId);
        Assert.Equal(2, main.ContentStack.Count);
        await main.GoBackAsync();
        Assert.Equal(ContentKind.Overview, main.ActiveContent!.Request.Kind);
        await main.NavigateAsync(new(MainArea.Network));
        Assert.Equal(summary, main.ActiveContent!.Request.TargetId);

        main.NewWorkspaceName = "主题 B";
        await main.CreateWorkspaceAsync();
        Assert.Single(main.ContentStack);
        Assert.Equal(MainArea.Explore, main.CurrentArea);
        await main.SwitchWorkspaceAsync(a);
        Assert.Equal(MainArea.Network, main.CurrentArea);
        Assert.Equal(summary, main.ActiveContent!.Request.TargetId);
        await main.GoBackAsync();
        Assert.Equal(ContentKind.Overview, main.ActiveContent!.Request.Kind);
    }

    [Fact]
    public async Task Failed_save_keeps_draft_and_active_workspace_for_retry()
    {
        using var dir = new TestDirectory();
        await using var store = await SqliteWorkspaceStore.OpenAsync(AppPaths.ForTrack("codex-steady", dir.Path));
        var main = CompositionRoot.CreateMain(store);
        await main.InitializeAsync();
        main.NewWorkspaceName = "已保存";
        await main.CreateWorkspaceAsync();
        var current = main.CurrentWorkspace;
        await store.DisposeAsync();
        main.NewWorkspaceName = "需要保留的输入";
        await main.CreateWorkspaceAsync();
        Assert.NotNull(main.ErrorMessage);
        Assert.Equal("需要保留的输入", main.NewWorkspaceName);
        Assert.Same(current, main.CurrentWorkspace);
        Assert.Single(main.Workspaces);
        await main.NavigateAsync(new(MainArea.Network));
        // Saving selection failure does not prevent reading another area in S1.
        Assert.Equal(MainArea.Network, main.CurrentArea);
    }
}
