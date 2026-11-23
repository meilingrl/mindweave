using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Application.SummaryNetwork;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.SummaryNetwork;

namespace Mindweave.Desktop.Tests;

/// <summary>The network of a workspace in memory: every conversation a dot, two views, positions computed.</summary>
public class SummaryNetworkSessionTests
{
    private static async Task<(SummaryNetworkSession Session, ConversationId A, ConversationId B, ConversationId Branch)> OpenAsync(DesktopHarness app)
    {
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var a = await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, "火车是怎么发明的？");
        var branch = await SummaryNetworkScenario.BranchAsync(app, conversations, a, "谁造了第一台？");
        var b = await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, "摄影怎么入门？");
        var session = app.Services.GetRequiredService<SummaryNetworkSessions>().Get(workspace);
        await session.LoadAsync(CancellationToken.None);
        return (session, a, b, branch);
    }

    [AvaloniaFact]
    public async Task Loading_shows_every_conversation_and_saves_only_the_explorations_places()
    {
        await using var app = DesktopHarness.Create();
        var (session, a, b, _) = await OpenAsync(app);

        Assert.Equal(3, session.Projection.Nodes.Count);
        Assert.Equal(3, session.Positions.Count);
        var places = await app.Services.GetRequiredService<ISummaryNetworkLayout>().GetExplorationPlacesAsync(app.Workbench.CurrentWorkspace!.Id, CancellationToken.None);
        Assert.Equal([a, b], places.Keys.OrderBy(k => k.Value));
    }

    [AvaloniaFact]
    public async Task Switching_the_view_changes_positions_and_keeps_the_projection()
    {
        await using var app = DesktopHarness.Create();
        var (session, a, b, _) = await OpenAsync(app);
        var projection = session.Projection;

        session.SetViewMode(SummaryNetworkViewMode.Timeline);

        Assert.Equal(SummaryNetworkViewMode.Timeline, session.ViewMode);
        Assert.True(session.IsTimeline);
        Assert.Same(projection, session.Projection);
        var roots = projection.Nodes.Where(n => n.Conversation == a || n.Conversation == b).Select(n => session.Positions[n.Id]).ToList();
        Assert.All(roots, p => Assert.Equal(0, p.Y));
        Assert.NotEmpty(session.DayStarts);
    }

    [AvaloniaFact]
    public async Task The_selection_stays_on_its_conversation_when_it_gets_its_first_summary()
    {
        await using var app = DesktopHarness.Create();
        var (session, a, _, _) = await OpenAsync(app);
        var before = session.Projection.Nodes.Single(n => n.Conversation == a);
        Assert.False(before.HasSummary);
        session.Selected = before.Id;

        var summaries = app.Services.GetRequiredService<Application.Summaries.SummaryService>();
        Assert.True((await summaries.SummarizeAsync(a, CancellationToken.None)).Accepted);
        await UiWait.UntilAsync(() => summaries.LatestTaskAsync(a, CancellationToken.None).Result is { State: GenerationTaskState.Completed });
        await session.LoadAsync(CancellationToken.None);

        var after = session.Projection.Nodes.Single(n => n.Conversation == a);
        Assert.True(after.HasSummary);
        Assert.NotEqual(before.Id, after.Id);
        Assert.Equal(after.Id, session.Selected);
    }

    [Fact]
    public void The_timeline_day_is_the_local_calendar_day()
    {
        var local = TimeZoneInfo.Local;
        var midnight = new DateTimeOffset(new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Unspecified), local.GetUtcOffset(new DateTime(2026, 3, 10, 0, 0, 0)));
        var before = midnight.AddMinutes(-1).ToUniversalTime();
        var after = midnight.AddMinutes(1).ToUniversalTime();

        Assert.Equal(new DateOnly(2026, 3, 9), SummaryNetworkSession.LocalDay(before));
        Assert.Equal(new DateOnly(2026, 3, 10), SummaryNetworkSession.LocalDay(after));

        var a = new SummaryNodeId(Guid.NewGuid());
        var b = new SummaryNodeId(Guid.NewGuid());
        var arrangement = TimelineLayout.Arrange([
            new TimelineNode(a, null, SummaryNetworkSession.LocalDay(before), 0),
            new TimelineNode(b, null, SummaryNetworkSession.LocalDay(after), 1)]);
        Assert.Equal([a, b], arrangement.DayStarts);
    }
}
