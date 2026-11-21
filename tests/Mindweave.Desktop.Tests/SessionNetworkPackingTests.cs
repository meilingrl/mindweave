using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Application.SummaryNetwork;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>The gated spatial view takes its positions from the circle packing, never writes them, and opens readably.</summary>
public class SessionNetworkPackingTests
{
    private static readonly WorkspaceId Workspace = WorkspaceId.New();

    private static SummaryNodeId Id(int n) => new(new Guid(n, 0, 0, new byte[8]));

    /// <summary>
    /// <paramref name="count"/> sessions in creation order: every fifth is a root, the others branch from an earlier one;
    /// weights vary, every third has a summary, and the roots are chained by the time thread.
    /// </summary>
    private static SummaryNetworkProjection Sessions(int count)
    {
        var roots = new List<int>();
        var nodes = new List<SummaryNetworkNode>();
        var edges = new List<SummaryNetworkEdge>();
        var start = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < count; i++)
        {
            var conversation = ConversationId.New();
            nodes.Add(new SummaryNetworkNode(
                Id(i + 1), conversation, conversation, $"会话 {i}", $"会话 {i} 的名字", new SummaryMarkers(false, false, false),
                start.AddMinutes(i * 7), [], HasSummary: i % 3 == 0, Weight: 1 + ((i * 7) % 23)));
            if (i % 5 == 0)
            {
                if (roots.Count > 0)
                {
                    edges.Add(new SummaryNetworkEdge(Id(roots[^1] + 1), Id(i + 1), [], SummaryNetworkEdgeKind.TimeThread));
                }

                roots.Add(i);
            }
            else
            {
                edges.Add(new SummaryNetworkEdge(Id(i - ((i * 7) % Math.Min(i, 4))), Id(i + 1), []));
            }
        }

        return new SummaryNetworkProjection(nodes, edges);
    }

    private static List<PackNode> PackNodes(SummaryNetworkProjection projection)
    {
        var parent = projection.Edges.Where(e => e.Kind == SummaryNetworkEdgeKind.Branch).ToDictionary(e => e.To, e => e.From);
        var next = projection.Edges.Where(e => e.Kind == SummaryNetworkEdgeKind.TimeThread).ToDictionary(e => e.From, e => e.To);
        return [.. projection.Nodes.Select((n, i) => new PackNode(
            n.Id,
            parent.TryGetValue(n.Id, out var p) ? p : null,
            i,
            n.Weight,
            SummaryNetworkGeometry.DotRadius(n.Weight) + (n.HasSummary ? SummaryNetworkCanvas.SummaryRingGap + 1 : 0),
            SummaryNetworkLayout.LabelWidth(SummaryNetworkLayout.Label(n.ShortTitle, n.Markers.HasCandidate)),
            next.TryGetValue(n.Id, out var t) ? t : null))];
    }

    private static async Task<SummaryNetworkSession> LoadedAsync(SummaryNetworkProjection projection, FakeNetwork? network = null)
    {
        var session = new SummaryNetworkSession(Workspace, network ?? new FakeNetwork(projection), new SessionNetworkGate(true));
        await session.LoadAsync(CancellationToken.None);
        return session;
    }

    [Fact]
    public async Task Gated_spatial_uses_the_packing()
    {
        var projection = Sessions(23);
        var network = new FakeNetwork(projection);

        var session = await LoadedAsync(projection, network);

        var expected = SpatialPacking.Arrange(PackNodes(projection));
        Assert.Equal(projection.Nodes.Count, session.Positions.Count);
        Assert.All(expected, p => Assert.Equal(p.Value, session.Positions[p.Key]));
        Assert.False(session.IsDirty);
        Assert.Equal(0, network.Writes);
    }

    [Fact]
    public async Task Switching_back_to_the_spatial_view_packs_again_and_the_timeline_is_unchanged()
    {
        var projection = Sessions(12);
        var session = await LoadedAsync(projection);
        var packed = session.Positions.ToDictionary(p => p.Key, p => p.Value);

        session.SetViewMode(SummaryNetworkViewMode.Timeline);
        Assert.NotEqual(packed, session.Positions);
        session.SetViewMode(SummaryNetworkViewMode.Spatial);

        Assert.Equal(packed, session.Positions.ToDictionary(p => p.Key, p => p.Value));
    }

    [Fact]
    public async Task Packed_footprints_do_not_overlap_in_a_busy_workspace()
    {
        var projection = Sessions(60);
        var session = await LoadedAsync(projection);

        var report = SpatialPacking.Measure(PackNodes(projection), session.Positions);

        Assert.Equal(0, report.Overlaps);
    }

    [Fact]
    public async Task Thousand_sessions_load_quickly()
    {
        var projection = Sessions(1_000);
        var session = new SummaryNetworkSession(Workspace, new FakeNetwork(projection), new SessionNetworkGate(true));
        await session.LoadAsync(CancellationToken.None); // warm-up

        var watch = Stopwatch.StartNew();
        await session.LoadAsync(CancellationToken.None);
        var elapsed = watch.Elapsed;

        Assert.Equal(1_000, session.Positions.Count);
        TestContext.Current.TestOutputHelper?.WriteLine($"session network load of 1,000 sessions: {elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms");
        if (Environment.GetEnvironmentVariable("MINDWEAVE_PERF") == "1")
        {
            Assert.True(elapsed < TimeSpan.FromSeconds(1), $"Loading 1,000 sessions took {elapsed.TotalMilliseconds:F1} ms (budget 1,000 ms).");
        }
    }

    private static NodeView View(SummaryNetworkNode n, NodePosition at) =>
        new(n.Id, n.ShortTitle, n.DisplayName, at.X, at.Y, false, false, SummaryNetworkLayout.LabelWidth(SummaryNetworkLayout.Label(n.ShortTitle, false)), IsRoot: true, HasSummary: n.HasSummary, Weight: n.Weight);

    private static List<NodeView> Views(SummaryNetworkSession session) =>
        [.. session.Projection.Nodes.Select(n => View(n, session.Positions[n.Id]))];

    [Fact]
    public async Task One_session_opens_centred_at_zoom_one()
    {
        var session = await LoadedAsync(Sessions(1));
        var views = Views(session);

        var opening = SummaryNetworkGeometry.SpatialOpening(views, new Size(1100, 720));

        Assert.Equal(1, opening.Zoom);
        Assert.Equal(views[0].X, opening.CenterX, 6);
        Assert.Equal(views[0].Y, opening.CenterY, 6);
    }

    [Fact]
    public async Task A_workspace_that_fits_readably_opens_fitted()
    {
        var session = await LoadedAsync(Sessions(4));
        var views = Views(session);

        var opening = SummaryNetworkGeometry.SpatialOpening(views, new Size(1100, 720));

        Assert.Equal(SummaryNetworkGeometry.FitTo(views, new Size(1100, 720)), opening);
        Assert.True(opening.Zoom >= SummaryNetworkGeometry.ReadableZoom);
    }

    [Fact]
    public async Task A_large_workspace_opens_at_zoom_one_on_the_mass_weighted_centroid()
    {
        var session = await LoadedAsync(Sessions(300));
        var views = Views(session);
        var size = new Size(1100, 720);
        Assert.True(SummaryNetworkGeometry.FitTo(views, size).Zoom < SummaryNetworkGeometry.ReadableZoom, "the workspace should not fit readably");

        var opening = SummaryNetworkGeometry.SpatialOpening(views, size);

        var mass = views.Sum(v => (double)v.Weight);
        Assert.Equal(1, opening.Zoom);
        Assert.Equal(views.Sum(v => v.X * v.Weight) / mass, opening.CenterX, 6);
        Assert.Equal(views.Sum(v => v.Y * v.Weight) / mass, opening.CenterY, 6);
    }

    [Fact]
    public async Task A_recommendation_in_a_dense_pack_covers_no_session()
    {
        var projection = Sessions(30);
        var session = await LoadedAsync(projection);
        var views = Views(session);
        var obstacles = views.Select(v => CanvasBox.OfNode(v.X, v.Y, v.LabelWidth)).ToList();
        var width = SummaryNetworkLayout.LabelWidth("一个推荐的问题会出现在这里");

        // Beside each session in turn: wherever the recommendation hangs, nothing of any session is under it.
        foreach (var basis in views)
        {
            var place = RecommendationPlacement.Place(obstacles, [(basis.X, basis.Y, width)])[0];
            var box = CanvasBox.OfNode(place.X, place.Y, width);
            Assert.DoesNotContain(views, v => box.Overlaps(CanvasBox.OfNode(v.X, v.Y, v.LabelWidth)));
        }
    }

    private sealed class FakeNetwork(SummaryNetworkProjection projection) : ISummaryNetworkLayout
    {
        public int Writes { get; private set; }

        public Task<SummaryNetworkProjection> ProjectAsync(WorkspaceId workspace, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SummaryNetworkProjection> ProjectSessionsAsync(WorkspaceId workspace, CancellationToken cancellationToken) => Task.FromResult(projection);

        public Task<IReadOnlyDictionary<SummaryNodeId, NodePosition>> GetLayoutAsync(WorkspaceId workspace, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> PlaceAsync(WorkspaceId workspace, SummaryNodeId node, NodePosition position, CancellationToken cancellationToken)
        {
            Writes++;
            return Task.FromResult(true);
        }

        public Task<int> SaveLayoutAsync(WorkspaceId workspace, IReadOnlyDictionary<SummaryNodeId, NodePosition> positions, CancellationToken cancellationToken)
        {
            Writes++;
            return Task.FromResult(0);
        }
    }
}
