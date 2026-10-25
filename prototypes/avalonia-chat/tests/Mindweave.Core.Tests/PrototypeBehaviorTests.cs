using Mindweave.Desktop;
using Xunit;

namespace Mindweave.Core.Tests;

public class PrototypeBehaviorTests
{
    // Catches showing an unavailable parent-return action in a new root conversation.
    [Fact]
    public async Task Root_demo_answer_does_not_claim_it_has_a_branch_source()
    {
        var workspace = new WorkspaceViewModel(demoDuration: TimeSpan.Zero);
        workspace.NewConversation(new(null, 0), "");
        await workspace.SendAsync("城市的公共交通");
        Assert.Null(workspace.Current.Source);
        Assert.DoesNotContain("返回来源", workspace.Current.Messages.Last().Markdown);
        Assert.DoesNotContain("这条分支已经保留", workspace.Current.Messages.Last().Markdown);
    }
}
