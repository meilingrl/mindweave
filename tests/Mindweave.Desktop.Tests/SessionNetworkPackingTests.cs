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

    /// <summary>The sessions exactly as the session hands them to the spatial layout, date lines included.</summary>
    private static List<SpatialNode> SpatialNodes(SummaryNetworkSession session) =>
        SummaryNetworkSession.SpatialNodes(session.Projection, SummaryNetworkSession.BranchParents(session.Projection), session.DayStarts);

    /// <summary>The spatial arrangement the session holds: its positions and its label places.</summary>
    private static SpatialArrangement Arrangement(SummaryNetworkSession session) => new(session.Positions, session.SpatialLabels);

    private static async Task<SummaryNetworkSession> LoadedAsync(SummaryNetworkProjection projection, FakeNetwork? network = null)
    {
        var session = new SummaryNetworkSession(Workspace, network ?? new FakeNetwork(projection), new SessionNetworkGate(true));
        await session.LoadAsync(CancellationToken.None);
        return session;
    }

    [Fact]
    public async Task Gated_spatial_packs_every_session_and_writes_nothing()
    {
        var projection = Sessions(23);
        var network = new FakeNetwork(projection);

        var session = await LoadedAsync(projection, network);

        Assert.Equal(projection.Nodes.Count, session.Positions.Count);
        Assert.Equal(projection.Nodes.Count, session.Positions.Values.Distinct().Count());
        Assert.False(session.IsDirty);
        Assert.True(await session.SaveAsync(CancellationToken.None));
        Assert.Equal(0, network.Writes);
        Assert.Equal((await LoadedAsync(projection)).Positions, session.Positions);
    }

    [Fact]
    public async Task What_the_packing_is_given_changes_where_the_dots_go()
    {
        var projection = Sessions(23);
        var baseline = (await LoadedAsync(projection)).Positions;

        // A summary ring makes one dot's footprint larger.
        var ringed = projection with { Nodes = [.. projection.Nodes.Select((n, i) => i == 7 ? n with { HasSummary = !n.HasSummary } : n)] };
        Assert.NotEqual(baseline, (await LoadedAsync(ringed)).Positions);

        // The weight is the mass and sizes the dot.
        var heavier = projection with { Nodes = [.. projection.Nodes.Select((n, i) => i == 3 ? n with { Weight = n.Weight + 40 } : n)] };
        Assert.NotEqual(baseline, (await LoadedAsync(heavier)).Positions);
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

        // Back in the spatial view every exploration returns to its place: the same picture, to rounding.
        Assert.Equal(packed.Keys.OrderBy(k => k.Value), session.Positions.Keys.OrderBy(k => k.Value));
        Assert.All(packed, p => Assert.True(Math.Abs(p.Value.X - session.Positions[p.Key].X) < 1e-6 && Math.Abs(p.Value.Y - session.Positions[p.Key].Y) < 1e-6, $"{p.Key} moved."));
    }

    [Fact]
    public async Task Exploration_places_are_saved_once_and_a_new_conversation_leaves_the_others_where_they_were()
    {
        // Design §16, option C: the first load saves every exploration's place; a load with nothing new saves nothing; a new
        // exploration is placed and saved without moving any other.
        var projection = Sessions(30);
        var network = new FakeNetwork(projection);
        var first = await LoadedAsync(projection, network);
        var parents = SummaryNetworkSession.BranchParents(projection);
        var roots = projection.Nodes.Where(n => !parents.ContainsKey(n.Id)).ToList();
        Assert.Equal(roots.Count, network.PlaceWrites);
        Assert.Equal(roots.Select(r => r.Conversation.Value).Order(), network.Places.Keys.Select(k => k.Value).Order());

        await LoadedAsync(projection, network);
        Assert.Equal(roots.Count, network.PlaceWrites);

        var conversation = ConversationId.New();
        var added = new SummaryNetworkNode(Id(999), conversation, conversation, "新的探索", "新的探索", new SummaryMarkers(false, false, false),
            projection.Nodes[^1].CreatedAt.AddMinutes(5), []);
        network.Current = projection with { Nodes = [.. projection.Nodes, added] };
        var later = await LoadedAsync(network.Current, network);

        foreach (var root in roots)
        {
            Assert.Equal(first.Positions[root.Id], later.Positions[root.Id]);
        }

        Assert.Equal(roots.Count + 1, network.PlaceWrites);
        Assert.True(network.Places.ContainsKey(conversation));
        Assert.Equal(0, network.Writes);
    }

    [Fact]
    public async Task Places_first_shown_after_the_timeline_are_saved_too()
    {
        // Review 2026-11-23: a load in the timeline saves nothing; switching to the spatial view places the explorations,
        // and those places must be saved, or the next load would place them again.
        var projection = Sessions(20);
        var network = new FakeNetwork(projection);
        var session = new SummaryNetworkSession(Workspace, network, new SessionNetworkGate(true));
        session.SetViewMode(SummaryNetworkViewMode.Timeline);
        await session.LoadAsync(CancellationToken.None);
        Assert.Equal(0, network.PlaceWrites);

        session.SetViewMode(SummaryNetworkViewMode.Spatial);

        await UiWait.UntilAsync(() => network.PlaceWrites > 0);
        var parents = SummaryNetworkSession.BranchParents(projection);
        Assert.Equal(projection.Nodes.Count(n => !parents.ContainsKey(n.Id)), network.Places.Count);
    }

    [Fact]
    public async Task Dots_and_labels_do_not_overlap_in_a_busy_workspace()
    {
        var projection = Sessions(60);
        var session = await LoadedAsync(projection);

        var report = SpatialLayout.Measure(SpatialNodes(session), Arrangement(session));

        Assert.Equal(0, report.DotOverlaps);
        Assert.Equal(0, report.LabelsOnDots);
        Assert.Equal(0, report.LabelOverlaps);
        Assert.Equal(projection.Nodes.Count, session.SpatialLabels.Count);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Thousand_sessions_load_within_300_ms_in_release()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MINDWEAVE_PERF") == "1", "Run through scripts/perf.sh.");
        var projection = Sessions(1_000);
        var session = new SummaryNetworkSession(Workspace, new FakeNetwork(projection), new SessionNetworkGate(true));
        await session.LoadAsync(CancellationToken.None); // warm-up

        var watch = Stopwatch.StartNew();
        await session.LoadAsync(CancellationToken.None);
        var elapsed = watch.Elapsed;

        Assert.Equal(1_000, session.Positions.Count);
        TestContext.Current.TestOutputHelper?.WriteLine($"session network load of 1,000 sessions: {elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms");
        Assert.True(elapsed < TimeSpan.FromMilliseconds(300), $"Loading 1,000 sessions took {elapsed.TotalMilliseconds:F1} ms (budget 300 ms, spec §6.4).");
    }

    private static NodeView View(SummaryNetworkNode n, NodePosition at, SpatialLabel? placed = null, bool dated = false) =>
        new(n.Id, n.ShortTitle, n.DisplayName, at.X, at.Y, false, false, SummaryNetworkLayout.LabelWidth(SummaryNetworkLayout.Label(n.ShortTitle, false)), IsRoot: true, HasSummary: n.HasSummary, Weight: n.Weight,
            Placed: placed, Dated: dated);

    /// <summary>The nodes as the page shows them: with the labels the spatial layout placed, and roots dated as the session dates them.</summary>
    private static List<NodeView> Views(SummaryNetworkSession session) =>
        [.. session.Projection.Nodes.Select(n => View(n, session.Positions[n.Id], session.SpatialLabels.GetValueOrDefault(n.Id), session.DayStarts.Contains(n.Id)))];

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
        var obstacles = SummaryNetworkObstacles.Of(views, [], sessionNetwork: true, timeline: false);
        var width = SummaryNetworkLayout.LabelWidth("一个推荐的问题会出现在这里");

        // The obstacles hold the placed labels: wherever a label lies round its dot, its block is covered (review 2026-11-22).
        foreach (var view in views)
        {
            var block = SpatialLayout.BlockAround(view.Placed!, SummaryNetworkLayout.DotFootprintRadius(view.Weight, view.HasSummary), view.Dated);
            var inside = new CanvasBox(view.X + block.Left + 1, view.Y + block.Top + 1, view.X + block.Right - 1, view.Y + block.Bottom - 1);
            Assert.Contains(obstacles, o => o.Left <= inside.Left && o.Top <= inside.Top && o.Right >= inside.Right && o.Bottom >= inside.Bottom);
        }

        // Beside each session in turn: wherever the recommendation hangs, nothing of any session is under it.
        foreach (var basis in views)
        {
            var place = RecommendationPlacement.Place(obstacles, [(basis.X, basis.Y, width)])[0];
            var box = CanvasBox.OfNode(place.X, place.Y, width);
            Assert.DoesNotContain(obstacles, box.Overlaps);
        }
    }

    private sealed class FakeNetwork(SummaryNetworkProjection projection) : ISummaryNetworkLayout
    {
        /// <summary>Writes of node positions (the old summary network's layout); exploration places are counted apart.</summary>
        public int Writes { get; private set; }

        /// <summary>What the next load projects (a test adds a conversation by swapping it).</summary>
        public SummaryNetworkProjection Current { get; set; } = projection;

        /// <summary>Exploration places as saved, and how many were written in all.</summary>
        public Dictionary<ConversationId, ExplorationPlace> Places { get; } = [];

        public int PlaceWrites { get; private set; }

        public Task<IReadOnlyDictionary<ConversationId, ExplorationPlace>> GetExplorationPlacesAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<ConversationId, ExplorationPlace>>(new Dictionary<ConversationId, ExplorationPlace>(Places));

        public Task<int> SaveExplorationPlacesAsync(WorkspaceId workspace, IReadOnlyDictionary<ConversationId, ExplorationPlace> places, CancellationToken cancellationToken)
        {
            foreach (var (root, place) in places)
            {
                Places[root] = place;
            }

            PlaceWrites += places.Count;
            return Task.FromResult(places.Count);
        }

        public Task<SummaryNetworkProjection> ProjectAsync(WorkspaceId workspace, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SummaryNetworkProjection> ProjectSessionsAsync(WorkspaceId workspace, CancellationToken cancellationToken) => Task.FromResult(Current);

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
