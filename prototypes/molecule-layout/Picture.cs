using System.Globalization;
using System.Net;
using System.Text;

namespace MoleculeLayout;

/// <summary>Draws a laid-out workspace as SVG in the spatial view's look, and measures it (guards, not goals).</summary>
public static class Picture
{
    private const string Ink = "#2f3a4a";
    private const string Line = "#9aa4b0";
    private const string Thread = "#b9c0c8";
    private const string Text = "#2b2f36";
    private const string Faint = "#8a9099";

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    public static (string Svg, int Width, int Height) Svg(Workspace ws, Molecule m)
    {
        var d = ws.Dots;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var x in d)
        {
            minX = Math.Min(minX, Math.Min(x.X - x.Footprint, x.X + x.LabelDx));
            minY = Math.Min(minY, Math.Min(x.Y - x.Footprint, x.Y + x.LabelDy));
            maxX = Math.Max(maxX, Math.Max(x.X + x.Footprint, x.X + x.LabelDx + x.LabelW));
            maxY = Math.Max(maxY, Math.Max(x.Y + x.Footprint, x.Y + x.LabelDy + x.LabelH));
        }

        const double pad = 36;
        var w = (int)Math.Ceiling(maxX - minX + (2 * pad));
        var h = (int)Math.Ceiling(maxY - minY + (2 * pad));
        var ox = pad - minX;
        var oy = pad - minY;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\" font-family=\"PingFang SC, -apple-system, Helvetica Neue, sans-serif\">");
        sb.Append(CultureInfo.InvariantCulture, $"<rect width=\"{w}\" height=\"{h}\" fill=\"#fbfbf9\"/>");
        sb.Append("<defs><marker id=\"arr\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"7\" markerHeight=\"7\" orient=\"auto\"><path d=\"M0,1 L9,5 L0,9\" fill=\"none\" stroke=\"" + Thread + "\" stroke-width=\"1.4\"/></marker></defs>");

        foreach (var (a, b) in m.ThreadPairs())
        {
            var (x1, y1, x2, y2) = Trim(d[a], d[b], 4);
            sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{F(x1 + ox)}\" y1=\"{F(y1 + oy)}\" x2=\"{F(x2 + ox)}\" y2=\"{F(y2 + oy)}\" stroke=\"{Thread}\" stroke-width=\"1.2\" stroke-dasharray=\"2 4\" marker-end=\"url(#arr)\"/>");
        }

        foreach (var (a, b) in m.BondList)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{F(d[a].X + ox)}\" y1=\"{F(d[a].Y + oy)}\" x2=\"{F(d[b].X + ox)}\" y2=\"{F(d[b].Y + oy)}\" stroke=\"{Line}\" stroke-width=\"1.5\"/>");
        }

        foreach (var x in d)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{F(x.X + ox)}\" cy=\"{F(x.Y + oy)}\" r=\"{F(x.Radius)}\" fill=\"{Ink}\"/>");
            if (x.HasSummary)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{F(x.X + ox)}\" cy=\"{F(x.Y + oy)}\" r=\"{F(x.Radius + 3.5)}\" fill=\"none\" stroke=\"{Ink}\" stroke-width=\"1\"/>");
            }
        }

        foreach (var x in d)
        {
            var anchor = Molecule.Sides[x.LabelSide].Anchor;
            var tx = anchor switch
            {
                "middle" => x.X + x.LabelDx + (x.LabelW / 2),
                "end" => x.X + x.LabelDx + x.LabelW,
                _ => x.X + x.LabelDx,
            };
            var ty = x.Y + x.LabelDy;
            foreach (var line in x.Lines)
            {
                ty += Labels.LineHeight;
                sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{F(tx + ox)}\" y=\"{F(ty - 4 + oy)}\" font-size=\"{Labels.FontSize}\" fill=\"{Text}\" text-anchor=\"{anchor}\">{WebUtility.HtmlEncode(line)}</text>");
            }

            if (x.DateLine is { } date)
            {
                ty += Labels.DateSize + 4;
                sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{F(tx + ox)}\" y=\"{F(ty - 3 + oy)}\" font-size=\"{Labels.DateSize}\" fill=\"{Faint}\" text-anchor=\"{anchor}\">{date}</text>");
            }
        }

        sb.Append("</svg>");
        return (sb.ToString(), w, h);
    }

    private static (double, double, double, double) Trim(Dot a, Dot b, double extra)
    {
        var ex = b.X - a.X;
        var ey = b.Y - a.Y;
        var len = Math.Max(1e-6, Math.Sqrt((ex * ex) + (ey * ey)));
        ex /= len;
        ey /= len;
        return (a.X + (ex * (a.Footprint + extra)), a.Y + (ey * (a.Footprint + extra)), b.X - (ex * (b.Footprint + extra)), b.Y - (ey * (b.Footprint + extra)));
    }

    /// <summary>Guards: things that must not happen, plus two numbers that describe the shape.</summary>
    public static string Measure(Workspace ws, Molecule m)
    {
        var d = ws.Dots;
        var n = d.Count;
        int dotOverlaps = 0, labelOnDot = 0, labelOverlaps = 0, crossings = 0, lineThroughDot = 0, lineThroughLabel = 0;
        for (var u = 0; u < n; u++)
        {
            for (var v = u + 1; v < n; v++)
            {
                if (Math.Sqrt(Sq(d[u].X - d[v].X) + Sq(d[u].Y - d[v].Y)) < d[u].Footprint + d[v].Footprint)
                {
                    dotOverlaps++;
                }

                var a = m.LabelRect(u, d[u].LabelSide);
                var b = m.LabelRect(v, d[v].LabelSide);
                if (Math.Min(a.R, b.R) > Math.Max(a.L, b.L) && Math.Min(a.B, b.B) > Math.Max(a.T, b.T))
                {
                    labelOverlaps++;
                }
            }

            var r = m.LabelRect(u, d[u].LabelSide);
            for (var v = 0; v < n; v++)
            {
                if (v != u && Math.Sqrt(Sq(Math.Clamp(d[v].X, r.L, r.R) - d[v].X) + Sq(Math.Clamp(d[v].Y, r.T, r.B) - d[v].Y)) < d[v].Footprint)
                {
                    labelOnDot++;
                }
            }

            foreach (var (a, b) in m.BondList)
            {
                if (Molecule.SegmentHitsRect(d[a].X, d[a].Y, d[b].X, d[b].Y, r))
                {
                    lineThroughLabel++;
                }

                if (u != a && u != b && SegmentDistance(d[u].X, d[u].Y, d[a].X, d[a].Y, d[b].X, d[b].Y) < d[u].Footprint)
                {
                    lineThroughDot++;
                }
            }
        }

        var bl = m.BondList;
        for (var i = 0; i < bl.Count; i++)
        {
            for (var j = i + 1; j < bl.Count; j++)
            {
                var (a, b) = bl[i];
                var (c, e) = bl[j];
                if (a == c || a == e || b == c || b == e)
                {
                    continue;
                }

                if (Cross(d[a], d[b], d[c], d[e]))
                {
                    crossings++;
                }
            }
        }

        // Angle spread: how far each dot's lines are from equal spacing, in degrees (0 = a perfect molecule).
        double angleError = 0;
        var counted = 0;
        foreach (var x in d)
        {
            var dirs = new List<double>();
            if (x.Parent >= 0)
            {
                dirs.Add(Math.Atan2(d[x.Parent].Y - x.Y, d[x.Parent].X - x.X));
            }

            dirs.AddRange(x.Children.Select(c => Math.Atan2(d[c].Y - x.Y, d[c].X - x.X)));
            if (dirs.Count < 2)
            {
                continue;
            }

            dirs.Sort();
            var ideal = 360.0 / dirs.Count;
            for (var k = 0; k < dirs.Count; k++)
            {
                var next = k + 1 < dirs.Count ? dirs[k + 1] : dirs[0] + (2 * Math.PI);
                angleError += Math.Abs(((next - dirs[k]) * 180 / Math.PI) - ideal);
                counted++;
            }
        }

        var threads = m.ThreadPairs().Select(p => Math.Sqrt(Sq(d[p.A].X - d[p.B].X) + Sq(d[p.A].Y - d[p.B].Y))).ToList();
        var threadText = threads.Count == 0 ? "" : $"; time thread mean {threads.Average():0} px, longest {threads.Max():0} px";
        return $"dots {n}; overlaps: dots {dotOverlaps}, label on dot {labelOnDot}, labels {labelOverlaps}; lines: crossings {crossings}, through dots {lineThroughDot}, through labels {lineThroughLabel}; mean angle error {(counted == 0 ? 0 : angleError / counted):0.0}°{threadText}";
    }

    private static double Sq(double v) => v * v;

    private static double SegmentDistance(double px, double py, double x1, double y1, double x2, double y2)
    {
        var ex = x2 - x1;
        var ey = y2 - y1;
        var t = Math.Clamp((((px - x1) * ex) + ((py - y1) * ey)) / Math.Max(1e-9, (ex * ex) + (ey * ey)), 0, 1);
        return Math.Sqrt(Sq(px - (x1 + (t * ex))) + Sq(py - (y1 + (t * ey))));
    }

    private static bool Cross(Dot a, Dot b, Dot c, Dot e)
    {
        static double O(Dot p, Dot q, Dot r) => ((q.X - p.X) * (r.Y - p.Y)) - ((q.Y - p.Y) * (r.X - p.X));
        return O(a, b, c) * O(a, b, e) < 0 && O(c, e, a) * O(c, e, b) < 0;
    }
}
