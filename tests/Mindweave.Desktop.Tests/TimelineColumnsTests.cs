using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.Views.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>The timeline's columns widened to their widest label: pure, x only, the grid's order kept.</summary>
public class TimelineColumnsTests
{
    private static readonly double Room = TimelineColumns.DotRoom + SummaryNetworkLayout.LabelGap + TimelineColumns.ColumnGap;

    [Fact]
    public void The_dot_room_is_the_widest_dot_and_its_summary_ring_with_its_pen()
    {
        Assert.Equal(TimelineColumns.DotRoom, SummaryNetworkGeometry.DotRadius(int.MaxValue) + SummaryNetworkCanvas.SummaryRingGap + 1);
        Assert.Equal(TimelineLayout.Step, TimelineColumns.MinColumn);
    }

    [Fact]
    public void Columns_take_the_width_of_their_widest_label()
    {
        // Column 0: label 50. Column 1: labels 300 (main line) and 120 (a lane above). Column 2 is empty (a day gap).
        // Column 3: label 80.
        var grid = new Dictionary<SummaryNodeId, NodePosition>
        {
            [Id(1)] = new(0, 0),
            [Id(2)] = new(110, 0),
            [Id(3)] = new(110, -TimelineLayout.LaneGap),
            [Id(4)] = new(330, 0),
        };
        var widths = new Dictionary<SummaryNodeId, double> { [Id(1)] = 50, [Id(2)] = 300, [Id(3)] = 120, [Id(4)] = 80 };

        var widened = TimelineColumns.Widen(grid, widths, TimelineColumns.MinColumn);

        var column0 = Math.Max(TimelineColumns.MinColumn, Room + 50);
        var column1 = Math.Max(TimelineColumns.MinColumn, Room + 300);
        Assert.Equal(new NodePosition(0, 0), widened[Id(1)]);
        Assert.Equal(new NodePosition(column0, 0), widened[Id(2)]);
        Assert.Equal(new NodePosition(column0, -TimelineLayout.LaneGap), widened[Id(3)]);
        Assert.Equal(new NodePosition(column0 + column1 + TimelineColumns.MinColumn, 0), widened[Id(4)]);
        Assert.Equal(110, column0); // a short label keeps the grid's step
        Assert.Equal(355.5, column1);
    }

    [Fact]
    public void Rows_keep_their_y_and_order()
    {
        var nodes = new List<TimelineNode>
        {
            new(Id(1), null, new DateOnly(2026, 11, 19), 0),
            new(Id(2), Id(1), new DateOnly(2026, 11, 19), 1),
            new(Id(3), Id(2), new DateOnly(2026, 11, 19), 2),
            new(Id(4), Id(1), new DateOnly(2026, 11, 19), 3),
            new(Id(5), null, new DateOnly(2026, 11, 19), 4),
            new(Id(6), null, new DateOnly(2026, 11, 20), 5),
            new(Id(7), Id(6), new DateOnly(2026, 11, 20), 6),
        };
        var grid = TimelineLayout.Arrange(nodes).Positions;
        var widths = nodes.ToDictionary(n => n.Id, n => 37.0 * (n.Order % 4) * (n.Order % 3));

        var widened = TimelineColumns.Widen(grid, widths, TimelineColumns.MinColumn);

        Assert.Equal(grid.Keys.OrderBy(k => k.Value), widened.Keys.OrderBy(k => k.Value));
        foreach (var (a, p) in grid)
        {
            Assert.Equal(p.Y, widened[a].Y);
            foreach (var (b, q) in grid)
            {
                // Same column, same x; a later column, a larger x by at least a step.
                Assert.Equal(Math.Sign(p.X - q.X), Math.Sign(widened[a].X - widened[b].X));
                if (p.X > q.X)
                {
                    Assert.True(widened[a].X - widened[b].X >= p.X - q.X, $"{a} and {b} came closer than on the grid.");
                }
            }
        }
    }

    [Fact]
    public void An_empty_grid_stays_empty() =>
        Assert.Empty(TimelineColumns.Widen(new Dictionary<SummaryNodeId, NodePosition>(), new Dictionary<SummaryNodeId, double>(), TimelineColumns.MinColumn));

    private static SummaryNodeId Id(int n) => new(new Guid(n, 0, 0, new byte[8]));
}
