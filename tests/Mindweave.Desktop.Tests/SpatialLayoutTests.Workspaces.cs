using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.SummaryNetwork;

namespace Mindweave.Desktop.Tests;

/// <summary>The workspaces the spatial layout tests arrange: generated forests, balanced trees and other lone-tree shapes.</summary>
public partial class SpatialLayoutTests
{
    /// <summary>A balanced tree: every node down to <paramref name="depth"/> levels has <paramref name="fan"/> children.</summary>
    private static List<SpatialNode> Full(int fan, int depth)
    {
        var parent = new List<int> { -1 };
        var level = new List<int> { 0 };
        for (var i = 0; i < parent.Count; i++)
        {
            for (var c = 0; level[i] < depth && c < fan; c++)
            {
                parent.Add(i);
                level.Add(level[i] + 1);
            }
        }

        var mass = Enumerable.Repeat(2, parent.Count).ToArray();
        for (var i = parent.Count - 1; i > 0; i--)
        {
            mass[parent[i]] += mass[i];
        }

        return [.. Enumerable.Range(0, parent.Count).Select(i => Node(i + 1, parent[i] < 0 ? null : parent[i] + 1, i, mass[i], Title(i, 4 + (i % 5))))];
    }

    /// <summary>
    /// A generated workspace: <paramref name="roots"/> trees, at most <paramref name="maxDepth"/> levels below the
    /// root, with chains and fans; the first root reaches the full depth through a chain. Creation order interleaves
    /// roots and branches; a node's mass is its own questions plus all its branches' (the weight w), and its dot
    /// grows with it as on the canvas. With <paramref name="total"/>, trees keep growing until that many nodes exist.
    /// </summary>
    private static List<SpatialNode> Workspace(int seed, int roots, int maxDepth, int total = 0)
    {
        var random = new Random(seed);
        var parent = new List<int>();
        var depth = new List<int>();
        var time = new List<double>();
        var open = new List<int>();
        for (var r = 0; r < roots; r++)
        {
            Add(-1, random.NextDouble() * 1000 * roots);
        }

        // The first root reaches the full depth.
        var tip = 0;
        for (var d = 0; d < maxDepth; d++)
        {
            tip = Add(tip, 0);
        }

        // Fans and chains: each open node gets 0–5 children, most often none or one.
        for (var i = 0; i < parent.Count && (total == 0 || parent.Count < total); i++)
        {
            if (depth[i] >= maxDepth)
            {
                continue;
            }

            var roll = random.NextDouble();
            var count = roll < 0.45 ? 0 : roll < 0.75 ? 1 : roll < 0.88 ? 2 : roll < 0.95 ? 3 : 4 + random.Next(2);
            for (var c = 0; c < count && (total == 0 || parent.Count < total); c++)
            {
                Add(i, 0);
            }

            if (total > 0 && i == parent.Count - 1 && parent.Count < total)
            {
                Add(-1, random.NextDouble() * 1000 * roots); // keep growing until the budgeted size
            }
        }

        var n = parent.Count;
        var own = Enumerable.Range(0, n).Select(_ => 1 + random.Next(12)).ToArray();
        var mass = (int[])own.Clone();
        for (var i = n - 1; i >= 0; i--)
        {
            if (parent[i] >= 0)
            {
                mass[parent[i]] += mass[i];
            }
        }

        var order = Enumerable.Range(0, n).OrderBy(i => time[i]).ThenBy(i => i).ToArray();
        var rank = new int[n];
        for (var k = 0; k < n; k++)
        {
            rank[order[k]] = k;
        }

        var nodes = new List<SpatialNode>(n);
        for (var i = 0; i < n; i++)
        {
            var chars = 2 + random.Next(11);
            var label = string.Concat(Enumerable.Range(0, chars).Select(_ => random.NextDouble() < 0.3 ? "a" : "测"));
            var ring = random.NextDouble() < 0.3 ? 4.5 : 0; // the summary ring around the dot
            var radius = SummaryNetworkLayout.DotRadius(mass[i]) + ring;
            nodes.Add(new SpatialNode(Id(i + 1), parent[i] < 0 ? null : Id(parent[i] + 1), rank[i], mass[i], radius, label));
        }

        return nodes;

        int Add(int of, double at)
        {
            parent.Add(of);
            depth.Add(of < 0 ? 0 : depth[of] + 1);
            time.Add(of < 0 ? at : time[of] + 1 + (random.NextDouble() * 400));
            return parent.Count - 1;
        }
    }

    private static SpatialNode Node(int id, int? parent, int order, int mass, string label) =>
        new(Id(id), parent is { } p ? Id(p) : null, order, mass, SummaryNetworkLayout.DotRadius(mass), label);

    /// <summary>A made-up title of so many wide characters, different per node.</summary>
    private static string Title(int seed, int chars) => string.Concat(Enumerable.Range(0, chars).Select(k => "数据采集技术演进与层次关系"[(seed + k) % 13]));

    /// <summary>
    /// A comb: a chain of <paramref name="spine"/> sessions, each with <paramref name="legs"/> one-question branches
    /// (a long main line with side questions). Created in order down the chain, each node before its legs.
    /// </summary>
    private static List<SpatialNode> Comb(int spine, int legs)
    {
        var nodes = new List<SpatialNode>();
        var id = 0;
        int? previous = null;
        for (var s = 0; s < spine; s++)
        {
            var me = ++id;
            var mass = (spine - s) * (1 + legs) * 2;
            nodes.Add(Node(me, previous, me, mass, Title(me, 5)));
            for (var l = 0; l < legs; l++)
            {
                ++id;
                nodes.Add(Node(id, me, id, 1, Title(id, 3)));
            }

            previous = me;
        }

        return nodes;
    }

    /// <summary>A broom: a root with <paramref name="handles"/> children, each with <paramref name="bristles"/> one-question branches.</summary>
    private static List<SpatialNode> Broom(int handles, int bristles)
    {
        var parent = new List<int> { -1 };
        for (var h = 0; h < handles; h++)
        {
            parent.Add(0);
            var handle = parent.Count - 1;
            parent.AddRange(Enumerable.Repeat(handle, bristles));
        }

        var mass = Enumerable.Repeat(2, parent.Count).ToArray();
        for (var i = parent.Count - 1; i > 0; i--)
        {
            mass[parent[i]] += mass[i];
        }

        return [.. Enumerable.Range(0, parent.Count).Select(i => Node(i + 1, parent[i] < 0 ? null : parent[i] + 1, i, mass[i], Title(i, 4 + (i % 5))))];
    }

    private static SummaryNodeId Id(int n) => new(new Guid(n, 0, 0, new byte[8]));
}
