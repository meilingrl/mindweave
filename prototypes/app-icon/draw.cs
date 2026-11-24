// Draws the application icon candidates (prototype; nothing here is used by the app).
//
//   dotnet run prototypes/app-icon/draw.cs            writes screenshots/ (sheets, single PNGs, .iconset folders)
//
// Every size is drawn from the geometry, not scaled down: 128 px and up use the full drawing, 32–64 px a bolder one,
// 16 px the simplest one. Coordinates are on a 1024 canvas with an 824 body (the macOS icon grid).
#:package SkiaSharp@3.119.4
#:property ManagePackageVersionsCentrally=false
#:property TreatWarningsAsErrors=false
#:property AnalysisMode=None
#:property PublishAot=false

using System.Runtime.CompilerServices;
using SkiaSharp;

var here = Here();
var outDir = Path.Combine(here, "screenshots");
Directory.CreateDirectory(outDir);

var candidates = new[]
{
    new Candidate("1", "Braid", "One root, three strands woven, three branches", Tile.Blue, Glyphs.Braid),
    new Candidate("2", "Molecule", "The network's nodes and 120° bonds, making an m", Tile.Ink, Glyphs.Molecule),
    new Candidate("3", "Linked bubbles", "Two conversations, woven through each other", Tile.Violet, Glyphs.Bubbles),
    new Candidate("4", "Thread", "One thread woven through the messages of a talk", Tile.Paper, Glyphs.Thread),
    new Candidate("5", "Woven M", "Two strands make an M and end in one summary", Tile.Blue, Glyphs.WovenM),
};

var second = new[]
{
    new Candidate("3A", "Linked bubbles", "Candidate 3 with real speech tails", Tile.FlatViolet, Flat.Linked),
    new Candidate("3B", "Meeting bubbles", "Two conversations and what they share", Tile.FlatBlue, Flat.Meeting),
    new Candidate("3C", "One-colour bubbles", "The same, in one colour", Tile.FlatBlue, Flat.OneColour),
    new Candidate("3D", "Branching bubble", "A conversation that branches", Tile.FlatBlue, Flat.Branching),
    new Candidate("4A", "Message and branch", "A new line grows out of a message", Tile.FlatBlue, Flat.MessageBranch),
    new Candidate("4B", "Three lines", "Candidate 4 with one smooth thread", Tile.FlatPaper, Flat.ThreeLines),
};
var third = new[]
{
    new Candidate("6", "Knot", "Two equal loops, woven", Tile.PureBlue, Pure.Knot),
    new Candidate("6B", "Knot, one tone", "The same in white only", Tile.PureBlue, (c, t, l) => Pure.Knot(c, t with { B = t.A }, l)),
    new Candidate("7", "Three rings", "Held together only as three", Tile.PureBlue, Pure.Rings),
    new Candidate("8", "Junction", "One point, three equal branches", Tile.PureBlue, Pure.Junction),
    new Candidate("9", "Arcade", "Two arches crossing, an m", Tile.PureBlue, Pure.Arcade),
    new Candidate("10", "Basket", "Lines of text as a weave", Tile.PureBlue, Pure.Basket),
};
var fourth = new[]
{
    new Candidate("12", "Trefoil", "One ribbon, woven into three equal loops", Tile.Dusk, Rich.Trefoil),
    new Candidate("13", "Lattice", "Conversations overlapping around one centre", Tile.Dusk, Rich.Lattice),
    new Candidate("14", "Constellation", "The network, as a six-fold figure", Tile.Night, Rich.Constellation),
    new Candidate("15", "Strands", "One question, woven branches, one summary", Tile.Dusk, Rich.Strands),
};
Round(candidates, Tile.All, "");
Round(second, Tile.AllFlat, "round2-");
Round(third, Tile.AllPure, "round3-");
Round(fourth, Tile.AllRich, "round4-");
Console.WriteLine(outDir);

void Round(Candidate[] all, Tile[] tiles, string prefix)
{
    foreach (var c in all)
    {
        var set = Path.Combine(outDir, $"candidate-{c.Id}.iconset");
        Directory.CreateDirectory(set);
        foreach (var s in new[] { 16, 32, 128, 256, 512 })
        {
            Save(Icon(c, c.Tile, s), Path.Combine(set, $"icon_{s}x{s}.png"));
            Save(Icon(c, c.Tile, s * 2), Path.Combine(set, $"icon_{s}x{s}@2x.png"));
        }
        foreach (var s in new[] { 1024, 128, 32, 16 }) Save(Icon(c, c.Tile, s), Path.Combine(outDir, $"candidate-{c.Id}-{s}.png"));
        Save(Sheets.One(c, tiles), Path.Combine(outDir, $"sheet-{c.Id}.png"));
    }
    Save(Sheets.Overview(all), Path.Combine(outDir, prefix + "overview.png"));
    Save(Sheets.Dock(all), Path.Combine(outDir, prefix + "dock.png"));
    Save(Sheets.Small(all), Path.Combine(outDir, prefix + "small-sizes.png"));
    Save(Sheets.Tiles(all, tiles), Path.Combine(outDir, prefix + "tiles.png"));
}

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

static void Save(SKImage image, string path)
{
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(path, data.ToArray());
    image.Dispose();
}

static SKImage Icon(Candidate c, Tile tile, int px)
{
    using var surface = SKSurface.Create(new SKImageInfo(px, px, SKColorType.Rgba8888, SKAlphaType.Premul));
    surface.Canvas.Clear(SKColors.Transparent);
    Draw.Icon(surface.Canvas, c, tile, 0, 0, px);
    return surface.Snapshot();
}

enum Level { Full, Bold, Tiny }

sealed record Candidate(string Id, string Name, string Idea, Tile Tile, Action<SKCanvas, Tile, Level> Glyph);

/// <summary>Background and the colours drawn on it.</summary>
sealed record Tile(string Name, SKColor Top, SKColor Bottom, SKColor A, SKColor B, SKColor C, SKColor Accent)
{
    static SKColor H(string s) => SKColor.Parse(s);
    public static readonly Tile Blue = new("Blue", H("#6C88FF"), H("#2C46D2"), H("#FFFFFF"), H("#C3D0FF"), H("#E6DEFF"), H("#FFC860"));
    public static readonly Tile Violet = new("Violet", H("#8A78F0"), H("#3D3FC4"), H("#FFFFFF"), H("#C9C6FF"), H("#E9E2FF"), H("#FFC860"));
    public static readonly Tile Paper = new("Paper", H("#FFFFFF"), H("#EFEDE9"), H("#4B6BFB"), H("#9AACFD"), H("#7A62D3"), H("#F2A03D"));
    public static readonly Tile Ink = new("Ink", H("#2C3357"), H("#121628"), H("#FFFFFF"), H("#8CA2FF"), H("#B9A6F8"), H("#FFC860"));
    public static readonly Tile[] All = [Blue, Violet, Paper, Ink];

    // Round 2: one solid colour, and fewer colours on it.
    public static readonly Tile FlatBlue = new("Blue", H("#4B6BFB"), H("#4B6BFB"), H("#FFFFFF"), H("#AFBFFF"), H("#1F36B0"), H("#FFC860"));
    public static readonly Tile FlatViolet = new("Violet", H("#6656DC"), H("#6656DC"), H("#FFFFFF"), H("#BDB4FB"), H("#33249C"), H("#FFC860"));
    public static readonly Tile FlatPaper = new("Paper", H("#FAFAF9"), H("#FAFAF9"), H("#4B6BFB"), H("#B4C2FF"), H("#1F36B0"), H("#F2A03D"));
    public static readonly Tile FlatInk = new("Ink", H("#1C2240"), H("#1C2240"), H("#FFFFFF"), H("#7F97FF"), H("#4B6BFB"), H("#FFC860"));
    public static readonly Tile[] AllFlat = [FlatBlue, FlatViolet, FlatPaper, FlatInk];
    // Round 3: one hue and at most one tint of it.
    public static readonly Tile PureBlue = new("Blue", H("#4B6BFB"), H("#4B6BFB"), H("#FFFFFF"), H("#B9C6FF"), H("#FFFFFF"), H("#FFFFFF"));
    public static readonly Tile PurePaper = new("Paper", H("#FBFBFA"), H("#FBFBFA"), H("#4B6BFB"), H("#A9B8FD"), H("#4B6BFB"), H("#4B6BFB"));
    public static readonly Tile PureInk = new("Ink", H("#1B2142"), H("#1B2142"), H("#FFFFFF"), H("#8599F8"), H("#FFFFFF"), H("#FFFFFF"));
    public static readonly Tile[] AllPure = [PureBlue, PurePaper, PureInk];
    // Round 4: gradients with light from above.
    public static readonly Tile Dusk = new("Dusk", H("#7F8CFF"), H("#3A2BB8"), H("#FFFFFF"), H("#AEBBFF"), H("#D9CCFF"), H("#FFD28A")) { Glow = H("#B9B4FF") };
    public static readonly Tile Night = new("Night", H("#2B3168"), H("#0E1026"), H("#FFFFFF"), H("#8EA2FF"), H("#B79CF7"), H("#FFD28A")) { Glow = H("#5561C9") };
    public static readonly Tile Pearl = new("Pearl", H("#FFFFFF"), H("#E9EAF6"), H("#3A34C6"), H("#7F95FF"), H("#A07BEA"), H("#F0A23C")) { Glow = H("#FFFFFF") };
    public static readonly Tile[] AllRich = [Dusk, Night, Pearl];
    public SKColor? Glow { get; init; }
    public bool IsFlat => Top == Bottom;
}

static class Draw
{
    /// <summary>Level of detail for a size in real pixels.</summary>
    public static Level LevelOf(float px) => px >= 100 ? Level.Full : px >= 24 ? Level.Bold : Level.Tiny;

    public static void Icon(SKCanvas canvas, Candidate c, Tile tile, float x, float y, float size, float? pixels = null)
    {
        var level = LevelOf(pixels ?? size);
        canvas.Save();
        canvas.Translate(x, y);
        canvas.Scale(size / 1024f);
        using var body = Body(level);
        if (level == Level.Full && !tile.IsFlat)
        {
            using var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 70), ImageFilter = SKImageFilter.CreateBlur(14, 14) };
            canvas.Save(); canvas.Translate(0, 12); canvas.DrawPath(body, shadow); canvas.Restore();
        }
        using var fill = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new(512, 100), new(512, 924), [tile.Top, tile.Bottom], SKShaderTileMode.Clamp) };
        canvas.DrawPath(body, fill);
        if (tile.Glow is { } glow && level != Level.Tiny)
        {
            using var light = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new(512, 300), 520, [glow.WithAlpha(150), glow.WithAlpha(0)], SKShaderTileMode.Clamp) };
            canvas.DrawPath(body, light);
        }
        if (tile.Top.Red > 240)
        {
            using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = level == Level.Full ? 8 : 1024f / (pixels ?? size) * 2, Color = new SKColor(0, 0, 0, 36) };
            canvas.Save(); canvas.ClipPath(body, SKClipOperation.Intersect, true); canvas.DrawPath(body, edge); canvas.Restore();
        }
        canvas.SaveLayer();
        c.Glyph(canvas, tile, level);
        canvas.Restore();
        canvas.Restore();
    }

    /// <summary>The rounded body, on whole pixels at the small sizes.</summary>
    static SKPath Body(Level level)
    {
        var half = level switch { Level.Full => 412f, Level.Bold => 416f, _ => 448f };
        var path = new SKPath();
        // A superellipse corner joined to straight sides (continuous curvature, close to the macOS shape).
        const float r = 0.62f; const int n = 60; const double e = 2 / 2.9;
        var c = half * (1 - r); var k = half * r;
        for (var q = 0; q < 4; q++)
            for (var i = 0; i <= n; i++)
            {
                var t = Math.PI / 2 * i / n;
                double x = c + k * Math.Pow(Math.Cos(t), e), y = c + k * Math.Pow(Math.Sin(t), e);
                var p = q switch { 0 => (x, y), 1 => (-y, x), 2 => (-x, -y), _ => (y, -x) };
                var point = new SKPoint(512 + (float)p.Item1, 512 + (float)p.Item2);
                if (q == 0 && i == 0) path.MoveTo(point); else path.LineTo(point);
            }
        path.Close();
        return path;
    }

    public static void Dot(SKCanvas canvas, float x, float y, float r, SKColor color)
    {
        using var p = new SKPaint { IsAntialias = true, Color = color };
        canvas.DrawCircle(x, y, r, p);
    }

    /// <summary>Clears a ring around a dot, so a strand stops short of it.</summary>
    public static void Clear(SKCanvas canvas, float x, float y, float r)
    {
        using var p = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Clear };
        canvas.DrawCircle(x, y, r, p);
    }
}

/// <summary>A strand as a dense polyline, with a depth per point for deciding which one is on top.</summary>
sealed class Strand
{
    public required SKPoint[] Points;
    public float[]? Depth;
    public required float Width;
    public required SKColor Color;
    public SKShader? Shader;
    float[]? length;

    public static Strand FromSvg(string d, float width, SKColor color)
    {
        using var path = SKPath.ParseSvgPathData(d);
        using var m = new SKPathMeasure(path, false);
        var n = (int)(m.Length / 3) + 2;
        var pts = new SKPoint[n];
        for (var i = 0; i < n; i++) pts[i] = m.GetPosition(m.Length * i / (n - 1));
        return new Strand { Points = pts, Width = width, Color = color };
    }

    public static Strand FromFunction(Func<float, (float X, float Y, float Z)> f, float width, SKColor color, int n = 500)
    {
        var pts = new SKPoint[n]; var z = new float[n];
        for (var i = 0; i < n; i++) { var v = f(i / (n - 1f)); pts[i] = new(v.X, v.Y); z[i] = v.Z; }
        return new Strand { Points = pts, Depth = z, Width = width, Color = color };
    }

    float[] Length()
    {
        if (length != null) return length;
        length = new float[Points.Length];
        for (var i = 1; i < Points.Length; i++) length[i] = length[i - 1] + SKPoint.Distance(Points[i], Points[i - 1]);
        return length;
    }

    public void Draw(SKCanvas canvas, SKPaint paint)
    {
        using var path = new SKPath();
        path.AddPoly(Points, false);
        canvas.DrawPath(path, paint);
    }

    /// <summary>The outline of the strand around one point, widened a little.</summary>
    public SKPath Outline(int index, float half, float extra)
    {
        var l = Length();
        var a = index; var b = index;
        while (a > 0 && l[index] - l[a] < half) a--;
        while (b < Points.Length - 1 && l[b] - l[index] < half) b++;
        using var path = new SKPath();
        path.AddPoly(Points[a..(b + 1)], false);
        using var p = Paint(extra, SKStrokeCap.Butt);
        return p.GetFillPath(path)!;
    }

    public void DrawAround(SKCanvas canvas, int index, float half, SKPaint paint)
    {
        var l = Length();
        var a = index; var b = index;
        while (a > 0 && l[index] - l[a] < half) a--;
        while (b < Points.Length - 1 && l[b] - l[index] < half) b++;
        using var path = new SKPath();
        path.AddPoly(Points[a..(b + 1)], false);
        canvas.DrawPath(path, paint);
    }

    public SKPaint Paint(float extra = 0, SKStrokeCap cap = SKStrokeCap.Round) => new()
    {
        IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Width + extra, StrokeCap = cap, StrokeJoin = SKStrokeJoin.Round, Color = Color, Shader = Shader,
    };
}

static class Weave
{
    /// <summary>
    /// Draws strands and, at every crossing, the upper one again with a gap cut into the lower one. Without depths the
    /// crossings alternate along the first strand.
    /// </summary>
    public static void Draw(SKCanvas canvas, float gap, bool firstOver, Strand[] strands, SKPoint? skip = null, float skipRadius = 0, Func<int, int, bool>? over = null, Func<SKPoint, bool>? ignore = null, float shadow = 0)
    {
        foreach (var s in strands) { using var p = s.Paint(); s.Draw(canvas, p); }
        var crossings = new List<(Strand Over, int At, Strand Under, int UnderAt, float Sin)>();
        for (var i = 0; i < strands.Length; i++)
            for (var j = i + 1; j < strands.Length; j++)
            {
                var found = Cross(strands[i], strands[j]);
                var n = 0;
                foreach (var (a, b, sin) in found)
                {
                    var top = over != null ? over(i, j) : strands[i].Depth != null && strands[j].Depth != null
                        ? strands[i].Depth![a] > strands[j].Depth![b]
                        : firstOver ^ ((n++ + (strands.Length > 2 ? j : 0)) % 2 == 1);
                    if (ignore?.Invoke(strands[i].Points[a]) == true) continue;
                    if (skip is { } z && SKPoint.Distance(z, strands[i].Points[a]) < skipRadius) continue;
                    crossings.Add(top ? (strands[i], a, strands[j], b, sin) : (strands[j], b, strands[i], a, sin));
                }
            }
        foreach (var (upper, at, under, uat, angle) in crossings)
        {
            // Only inside the lower strand: clear a band along the upper one, then draw the upper one again there.
            var sin = Math.Max(.4f, angle);
            using var lower = under.Outline(uat, (upper.Width / 2 + gap * 2 + shadow * 2.6f) / sin + under.Width / 2, shadow > 0 ? 0 : 3);
            canvas.Save();
            canvas.ClipPath(lower, SKClipOperation.Intersect, true);
            var half = under.Width / sin + upper.Width + shadow * 2;
            if (gap > 0) using (var cut = upper.Paint(gap * 2, SKStrokeCap.Butt)) { cut.Shader = null; cut.BlendMode = SKBlendMode.Clear; upper.DrawAround(canvas, at, half, cut); }
            if (shadow > 0)
            {
                // The upper strand's shadow, falling on the lower one only.
                using var dark = upper.Paint(shadow, SKStrokeCap.Butt);
                dark.Shader = null; dark.Color = new SKColor(52, 40, 150, 135); dark.ImageFilter = SKImageFilter.CreateBlur(shadow * .5f, shadow * .5f);
                dark.BlendMode = SKBlendMode.SrcATop;
                upper.DrawAround(canvas, at, half, dark);
            }
            canvas.Restore();
            using var again = upper.Paint(0, SKStrokeCap.Butt);
            canvas.Save();
            using var wider = under.Outline(uat, (upper.Width / 2 + gap * 2) / sin + under.Width / 2 + 6, 10);
            canvas.ClipPath(wider, SKClipOperation.Intersect, true);
            upper.DrawAround(canvas, at, half, again);
            canvas.Restore();
        }
    }

    static IEnumerable<(int A, int B, float Sin)> Cross(Strand s, Strand t)
    {
        for (var i = 1; i < s.Points.Length; i++)
            for (var j = 1; j < t.Points.Length; j++)
            {
                SKPoint p = s.Points[i - 1], r = s.Points[i] - p, q = t.Points[j - 1], u = t.Points[j] - q;
                var d = r.X * u.Y - r.Y * u.X;
                if (Math.Abs(d) < 1e-6) continue;
                var w = q - p;
                var a = (w.X * u.Y - w.Y * u.X) / d;
                var b = (w.X * r.Y - w.Y * r.X) / d;
                if (a < 0 || a >= 1 || b < 0 || b >= 1) continue;
                yield return (i, j, Math.Abs(d) / (r.Length * u.Length));
            }
    }
}

static class Glyphs
{
    static float Smooth(float a, float b, float t) { t = Math.Clamp((t - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

    public static void Braid(SKCanvas canvas, Tile k, Level level)
    {
        if (level == Level.Tiny)
        {
            foreach (var (d, c) in new[] { ("M512 736C512 560 256 600 256 288", k.B), ("M512 736C512 560 768 600 768 288", k.C), ("M512 736V288", k.A) })
                { var s = Strand.FromSvg(d, 112, c); using var p = s.Paint(); s.Draw(canvas, p); }
            Draw.Dot(canvas, 512, 736, 112, k.Accent);
            return;
        }
        var (w, gap, root, tip) = level == Level.Full ? (62f, 18f, 74f, 66f) : (78f, 24f, 92f, 86f);
        Strand Make(string d, SKColor color, float depth)
        {
            var s = Strand.FromSvg(d, w, color);
            s.Depth = Enumerable.Repeat(depth, s.Points.Length).ToArray();
            return s;
        }
        Strand[] strands =
        [
            Make("M512 752C640 704 706 648 706 576C706 456 512 432 512 270", k.A, 0),
            Make("M512 752C512 600 290 520 290 270", k.B, 2),
            Make("M512 752C384 704 318 654 318 590C318 440 734 470 734 270", k.C, 1),
        ];
        Weave.Draw(canvas, gap, true, strands, new SKPoint(512, 752), root + w);
        Draw.Dot(canvas, 512, 752, root, k.Accent);
        foreach (var s in strands) Draw.Dot(canvas, s.Points[^1].X, s.Points[^1].Y, tip, s.Color);
    }

    public static void Molecule(SKCanvas canvas, Tile k, Level level)
    {
        // The upper halves of two joined rings: every angle is 120 degrees, as in the network's layout.
        var (bond, r) = level switch { Level.Full => (58f, 70f), Level.Bold => (76f, 84f), _ => (128f, 0f) };
        float dx = level == Level.Tiny ? 128 : 140, y0 = level == Level.Tiny ? 430 : 400, foot = level == Level.Tiny ? 704 : 690;
        var dy = dx * .577f;
        SKPoint P(float n, float y) => new(512 + n * dx, y);
        SKPoint[] roof = [P(-2, foot), P(-2, y0), P(-1, y0 - dy), P(0, y0), P(1, y0 - dy), P(2, y0), P(2, foot)];
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = bond, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = k.B };
        using (var path = new SKPath()) { path.AddPoly(roof, false); canvas.DrawPath(path, line); }
        canvas.DrawLine(P(0, y0), P(0, foot), line);
        if (level == Level.Tiny) { Draw.Dot(canvas, 512, y0 + 20, 112, k.Accent); return; }
        foreach (var n in new[] { -2, 0, 2 }) Draw.Dot(canvas, P(n, foot).X, foot, r, k.A);
        Draw.Dot(canvas, 512, y0, r * 1.16f, k.Accent);
    }

    public static void Bubbles(SKCanvas canvas, Tile k, Level level)
    {
        var (w, gap) = level switch { Level.Full => (66f, 20f), Level.Bold => (80f, 26f), _ => (100f, 34f) };
        // A rounded rectangle with one nearly square corner, the speech bubble's tail.
        static string Bubble(float l, float t, float r, float b, float rad, bool tailLeft) => tailLeft
            ? FormattableString.Invariant($"M{l + 14} {b}Q{l} {b} {l} {b - 14}V{t + rad}A{rad} {rad} 0 0 1 {l + rad} {t}H{r - rad}A{rad} {rad} 0 0 1 {r} {t + rad}V{b - rad}A{rad} {rad} 0 0 1 {r - rad} {b}H{l + 14}")
            : FormattableString.Invariant($"M{l + rad} {b}A{rad} {rad} 0 0 1 {l} {b - rad}V{t + rad}A{rad} {rad} 0 0 1 {l + rad} {t}H{r - rad}A{rad} {rad} 0 0 1 {r} {t + rad}V{b - 14}Q{r} {b} {r - 14} {b}H{l + rad}");
        if (level == Level.Tiny)
        {
            // Outlines need more than 16 px: two filled bubbles, the front one with a gap around it.
            using var back = new SKPaint { IsAntialias = true, Color = k.A };
            using var cut = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Clear, Style = SKPaintStyle.StrokeAndFill, StrokeWidth = 128 };
            using var front = new SKPaint { IsAntialias = true, Color = k.Accent };
            using var a1 = new SKPath(); a1.AddRoundRect(Corner(192, 192, 576, 576, 128, true));
            using var b1 = new SKPath(); b1.AddRoundRect(Corner(448, 448, 832, 832, 128, false));
            canvas.DrawPath(a1, back); canvas.DrawPath(b1, cut); canvas.DrawPath(b1, front);
            return;
        }
        static SKRoundRect Corner(float l, float t, float r, float b, float rad, bool left)
        {
            var rr = new SKRoundRect();
            rr.SetRectRadii(new(l, t, r, b), [new(rad, rad), new(rad, rad), left ? new(rad, rad) : new(20, 20), left ? new(20, 20) : new(rad, rad)]);
            return rr;
        }
        var a = Strand.FromSvg(Bubble(222, 284, 622, 602, 152, true), w, k.A);
        var b = Strand.FromSvg(Bubble(402, 422, 802, 740, 152, false), w, k.Accent);
        Weave.Draw(canvas, gap, true, [a, b]);
    }

    public static void Thread(SKCanvas canvas, Tile k, Level level)
    {
        var (w, tw, gap, amp, dot) = level switch { Level.Full => (104f, 60f, 20f, 40f, 60f), Level.Bold => (112f, 76f, 26f, 34f, 76f), _ => (128f, 128f, 40f, 0f, 0f) };
        var bars = new[] { "M274 344H606", "M418 512H750", "M274 680H606" }.Select(d => Strand.FromSvg(level == Level.Tiny ? d.Replace("344", "320").Replace("680", "704") : d, w, k.B)).ToArray();
        var thread = Strand.FromFunction(t =>
        {
            var y = 214 + 596 * t;
            var phase = (y - 512) / 168 * MathF.PI;
            return (512 + amp * MathF.Cos(phase), y, -MathF.Cos(phase));
        }, tw, k.A);
        foreach (var b in bars) b.Depth = new float[b.Points.Length];
        Weave.Draw(canvas, gap, true, [.. bars, thread]);
        if (dot > 0) Draw.Dot(canvas, thread.Points[^1].X, 800, dot, k.Accent);
    }

    public static void WovenM(SKCanvas canvas, Tile k, Level level)
    {
        var (w, gap, dot) = level switch { Level.Full => (74f, 20f, 72f), Level.Bold => (88f, 26f, 88f), _ => (124f, 34f, 0f) };
        var d = level == Level.Tiny
            ? "M256 720V416C256 320 310 290 376 290C470 290 512 400 512 600"
            : "M240 740V392C240 316 290 268 354 268C430 268 470 340 512 386C556 434 606 480 606 556C606 630 512 640 512 720";
        var left = Strand.FromSvg(d, w, k.A);
        var right = Strand.FromSvg(d, w, k.B);
        right.Points = right.Points.Select(p => new SKPoint(1024 - p.X, p.Y)).ToArray();
        Weave.Draw(canvas, gap, true, [left, right]);
        if (dot > 0) { Draw.Clear(canvas, 512, 716, dot + gap); Draw.Dot(canvas, 512, 716, dot, k.Accent); }
    }
}

/// <summary>Round 2: simpler shapes for solid backgrounds.</summary>
static class Flat
{
    /// <summary>A closed speech bubble with a tail below its left or (mirrored) right end.</summary>
    static string Bubble(float l, float t, float r, float b, float rad, float tail, float tailWidth, bool left)
    {
        float M(float x) => left ? x : l + r - x;
        var sweep = left ? 1 : 0;
        return FormattableString.Invariant($"M{M(r - rad)} {b}H{M(l + rad * .82f + tailWidth)}L{M(l + rad * .82f)} {b + tail}V{b - rad * .02f}A{rad} {rad} 0 0 {sweep} {M(l)} {b - rad}V{t + rad}A{rad} {rad} 0 0 {sweep} {M(l + rad)} {t}H{M(r - rad)}A{rad} {rad} 0 0 {sweep} {M(r)} {t + rad}V{b - rad}A{rad} {rad} 0 0 {sweep} {M(r - rad)} {b}Z");
    }

    static SKPaint Fill(SKColor c) => new() { IsAntialias = true, Color = c };

    static void Solid(SKCanvas canvas, SKPath path, SKColor c, float soften)
    {
        using var p = new SKPaint { IsAntialias = true, Color = c, Style = SKPaintStyle.StrokeAndFill, StrokeWidth = soften, StrokeJoin = SKStrokeJoin.Round };
        canvas.DrawPath(path, p);
    }

    public static void Linked(SKCanvas canvas, Tile k, Level level)
    {
        if (level == Level.Tiny) { Meeting(canvas, k, level); return; }
        var (w, gap) = level == Level.Full ? (58f, 18f) : (72f, 24f);
        var a = Strand.FromSvg(Bubble(192, 258, 600, 552, 104, 96, 92, true), w, k.A);
        var b = Strand.FromSvg(Bubble(440, 408, 832, 694, 104, 96, 92, false), w, k.Accent);
        Weave.Draw(canvas, gap, true, [a, b]);
    }

    public static void Meeting(SKCanvas canvas, Tile k, Level level)
    {
        var tiny = level == Level.Tiny;
        using var a = SKPath.ParseSvgPathData(tiny ? Bubble(192, 256, 640, 576, 96, 96, 110, true) : Bubble(206, 262, 628, 570, 112, 92, 70, true));
        using var b = SKPath.ParseSvgPathData(tiny ? Bubble(384, 448, 832, 704, 96, 96, 110, false) : Bubble(396, 418, 818, 726, 112, 92, 70, false));
        Solid(canvas, a, k.A, 24);
        Solid(canvas, b, k.B, 24);
        canvas.Save();
        canvas.ClipPath(a, SKClipOperation.Intersect, true);
        Solid(canvas, b, k.Accent, 24);
        canvas.Restore();
    }

    public static void OneColour(SKCanvas canvas, Tile k, Level level)
    {
        var tiny = level == Level.Tiny;
        using var a = SKPath.ParseSvgPathData(tiny ? Bubble(192, 256, 640, 576, 96, 96, 110, true) : Bubble(206, 262, 628, 570, 112, 92, 70, true));
        using var b = SKPath.ParseSvgPathData(tiny ? Bubble(384, 448, 832, 704, 96, 96, 110, false) : Bubble(396, 418, 818, 726, 112, 92, 70, false));
        Solid(canvas, a, k.A, 24);
        Solid(canvas, b, k.A, 24);
        canvas.Save();
        canvas.ClipPath(a, SKClipOperation.Intersect, true);
        using var cut = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Clear };
        canvas.DrawPath(b, cut);
        canvas.Restore();
    }

    public static void Branching(SKCanvas canvas, Tile k, Level level)
    {
        var (w, r) = level switch { Level.Full => (62f, 58f), Level.Bold => (74f, 68f), _ => (120f, 0f) };
        using var bubble = SKPath.ParseSvgPathData(level == Level.Tiny ? Bubble(192, 192, 832, 704, 128, 112, 150, true) : Bubble(196, 226, 828, 690, 150, 112, 130, true));
        Solid(canvas, bubble, k.A, 24);
        var ink = k.A.Red > 240 ? k.Top : SKColors.White;
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = ink };
        var warm = k.A.Red > 240 ? SKColor.Parse("#F2A03D") : k.Accent;
        using var branch = line.Clone(); branch.Color = warm;
        if (level == Level.Tiny)
        {
            using var t = SKPath.ParseSvgPathData("M448 448C560 448 576 320 704 320"); canvas.DrawPath(t, branch);
            canvas.DrawLine(320, 512, 704, 512, line);
            return;
        }
        using (var up = SKPath.ParseSvgPathData("M404 540C540 540 520 372 664 372")) canvas.DrawPath(up, branch);
        canvas.DrawLine(340, 540, 664, 540, line);
        Draw.Dot(canvas, 340, 540, r, ink); Draw.Dot(canvas, 684, 540, r, ink); Draw.Dot(canvas, 684, 372, r, warm);
    }

    public static void MessageBranch(SKCanvas canvas, Tile k, Level level)
    {
        var (bar, w) = level switch { Level.Full => (132f, 60f), Level.Bold => (140f, 72f), _ => (128f, 128f) };
        SKPaint Line(SKColor c, float width) => new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeWidth = width, Color = c };
        float top = level == Level.Tiny ? 320 : 342, low = level == Level.Tiny ? 640 : 676, half = bar / 2;
        using var elbow = SKPath.ParseSvgPathData(FormattableString.Invariant($"M352 {top}V{low - 120}C352 {low - 40} 392 {low} 472 {low}H560"));
        using (var p = Line(k.B, w)) canvas.DrawPath(elbow, p);
        using (var p = Line(k.A, bar)) canvas.DrawLine(212 + half, top, 752 - half, top, p);
        using (var p = Line(k.Accent, bar)) canvas.DrawLine(472 + half, low, 820 - half, low, p);
    }

    public static void ThreeLines(SKCanvas canvas, Tile k, Level level)
    {
        var (w, tw, gap) = level switch { Level.Full => (112f, 76f, 22f), Level.Bold => (116f, 88f, 30f), _ => (128f, 128f, 40f) };
        var ys = level == Level.Tiny ? new[] { 320, 512, 704 } : [332, 512, 692];
        var bars = ys.Select((y, i) => Strand.FromSvg(i == 1 ? $"M364 {y}H768" : $"M256 {y}H660", w, k.A)).ToArray();
        var thread = Strand.FromSvg(level == Level.Tiny ? "M512 226V798" : "M424 222C424 400 600 624 600 802", tw, k.Accent);
        for (var i = 0; i < bars.Length; i++) bars[i].Depth = Enumerable.Repeat(i == 1 ? 1f : -1f, bars[i].Points.Length).ToArray();
        thread.Depth = new float[thread.Points.Length];
        Weave.Draw(canvas, gap, true, [.. bars, thread]);
    }
}

/// <summary>Round 3: strict symmetry, one figure, one hue.</summary>
static class Pure
{
    static SKPaint Fill(SKColor c) => new() { IsAntialias = true, Color = c };

    static SKPaint Line(SKColor c, float w, SKStrokeCap cap = SKStrokeCap.Round) => new()
    {
        IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w, StrokeCap = cap, StrokeJoin = SKStrokeJoin.Round, Color = c,
    };

    static SKPoint Polar(float r, double degrees) => new(512 + r * (float)Math.Cos(degrees * Math.PI / 180), 512 + r * (float)Math.Sin(degrees * Math.PI / 180));

    public static void Knot(SKCanvas canvas, Tile k, Level level)
    {
        if (level == Level.Tiny)
        {
            // Two strands crossing: the loops have no room at 16 px.
            using var under = Line(k.B, 150); using var over = Line(k.A, 150);
            using var cut = Line(k.A, 250, SKStrokeCap.Butt); cut.BlendMode = SKBlendMode.Clear;
            canvas.DrawLine(320, 704, 704, 320, under);
            canvas.DrawLine(400, 400, 624, 624, cut);
            canvas.DrawLine(320, 320, 704, 704, over);
            return;
        }
        // Two stadium loops at right angles. Along each loop the crossings alternate.
        var (w, gap, r, half) = level switch { Level.Full => (58f, 18f, 92f, 188f), Level.Bold => (70f, 24f, 98f, 190f), _ => (96f, 36f, 100f, 170f) };
        Strand Loop(float turn)
        {
            var d = FormattableString.Invariant($"M512 {512 - r}H{512 + half}A{r} {r} 0 0 1 {512 + half} {512 + r}H{512 - half}A{r} {r} 0 0 1 {512 - half} {512 - r}Z");
            var s = Strand.FromSvg(d, w, turn > 0 ? k.A : k.B);
            var m = SKMatrix.CreateRotationDegrees(turn, 512, 512);
            s.Points = s.Points.Select(p => m.MapPoint(p)).ToArray();
            return s;
        }
        Weave.Draw(canvas, gap, true, [Loop(45), Loop(-45)]);
    }

    public static void Rings(SKCanvas canvas, Tile k, Level level)
    {
        var (w, gap, d, r) = level switch { Level.Full => (42f, 14f, 122f, 178f), Level.Bold => (58f, 20f, 122f, 176f), _ => (0f, 0f, 150f, 150f) };
        SKPoint[] c = [Polar(d, -90), Polar(d, 30), Polar(d, 150)];
        for (var i = 0; i < 3; i++) c[i].Offset(0, d * .18f);
        if (level == Level.Tiny)
        {
            using var dot = Fill(k.A);
            foreach (var p in c) canvas.DrawCircle(p, 118, dot);
            return;
        }
        // Each ring starts at its outermost point, away from every crossing.
        Strand Ring(SKPoint o, double start) => Strand.FromFunction(t =>
        {
            var a = (start + 360 * t) * Math.PI / 180;
            return (o.X + r * (float)Math.Cos(a), o.Y + r * (float)Math.Sin(a), 0);
        }, w, k.A);
        // Each ring lies over the next one: no two are linked, the three are.
        Weave.Draw(canvas, gap, true, [Ring(c[0], -90), Ring(c[1], 30), Ring(c[2], 150)], over: (i, j) => j - i == 1);
    }

    public static void Junction(SKCanvas canvas, Tile k, Level level)
    {
        var (arm, d, r, web) = level switch { Level.Full => (78f, 226f, 94f, 130f), Level.Bold => (96f, 226f, 106f, 140f), _ => (124f, 220f, 112f, 100f) };
        using var fill = Fill(k.A);
        using var line = Line(k.A, arm);
        canvas.Save();
        canvas.Translate(0, d * .22f);
        double[] at = [-90, 30, 150];
        for (var i = 0; i < 3; i++)
        {
            canvas.DrawLine(new(512, 512), Polar(d, at[i]), line);
            canvas.DrawCircle(Polar(d, at[i]), r, fill);
            // A concave corner between this arm and the next, as if the arms flowed into each other.
            double a = at[i], b = at[(i + 1) % 3];
            SKPoint Edge(double angle, int side)
            {
                var p = Polar(web, angle); var n = (angle + 90 * side) * Math.PI / 180;
                return new(p.X + arm / 2 * (float)Math.Cos(n), p.Y + arm / 2 * (float)Math.Sin(n));
            }
            using var path = new SKPath();
            path.MoveTo(512, 512);
            path.LineTo(Edge(a, 1));
            var inner = arm / 2 / (float)Math.Sin(Math.PI / 3);
            path.QuadTo(Polar(inner, a + 60), Edge(b, -1));
            path.Close();
            canvas.DrawPath(path, fill);
        }
        canvas.Restore();
    }

    public static void Arcade(SKCanvas canvas, Tile k, Level level)
    {
        if (level == Level.Tiny)
        {
            using var t = Line(k.A, 128, SKStrokeCap.Butt);
            foreach (var x in new[] { 256f, 512f })
                using (var p = SKPath.ParseSvgPathData(FormattableString.Invariant($"M{x} 768V384A128 128 0 0 1 {x + 256} 384V768"))) canvas.DrawPath(p, t);
            return;
        }
        var w = level == Level.Full ? 66f : 78f;
        // Four legs at equal distances: each arch spans three of them, and the two cross at the centre line.
        const float pitch = 166f, r = pitch, spring = 448, foot = 740;
        var x0 = 512 - pitch * 1.5f;
        using var line = Line(k.A, w);
        foreach (var x in new[] { x0, x0 + pitch })
            using (var p = SKPath.ParseSvgPathData(FormattableString.Invariant($"M{x} {foot}V{spring}A{r} {r} 0 0 1 {x + 2 * r} {spring}V{foot}"))) canvas.DrawPath(p, line);
    }

    public static void Basket(SKCanvas canvas, Tile k, Level level)
    {
        // Eight equal capsules; the gap between bars and between cells is the same.
        var (bar, gap) = level switch { Level.Full => (100f, 36f), Level.Bold => (96f, 32f), _ => (128f, 64f) };
        var cells = 2;
        var cell = bar * 2 + gap;
        var start = 512 - (cell * cells + gap * (cells - 1)) / 2;
        using var a = Fill(k.A); using var b = Fill(k.B);
        if (level == Level.Tiny)
        {
            // Whole pixels: bars of 2, 1 between them, 2 between the cells.
            (bar, cell, gap, start) = (128, 320, 128, 128);
            for (var row = 0; row < 2; row++)
                for (var col = 0; col < 2; col++)
                    for (var n = 0; n < 2; n++)
                    {
                        float x = start + col * (cell + gap), y = start + row * (cell + gap), s = n * 192;
                        var across = (row + col) % 2 == 0;
                        var rect = across ? new SKRect(x, y + s, x + cell, y + s + bar) : new SKRect(x + s, y, x + s + bar, y + cell);
                        canvas.DrawRect(rect, across ? a : b);
                    }
            return;
        }
        for (var row = 0; row < cells; row++)
            for (var col = 0; col < cells; col++)
                for (var n = 0; n < 2; n++)
                {
                    float x = start + col * (cell + gap), y = start + row * (cell + gap), s = n * (bar + gap);
                    var across = (row + col) % 2 == 0;
                    var rect = across ? new SKRect(x, y + s, x + cell, y + s + bar) : new SKRect(x + s, y, x + s + bar, y + cell);
                    canvas.DrawRoundRect(rect, bar / 2, bar / 2, across ? a : b);
                }
    }
}

/// <summary>Round 4: symmetric figures with depth and finer detail; simpler at small sizes.</summary>
static class Rich
{
    static SKPoint Polar(float r, double degrees) => new(512 + r * (float)Math.Cos(degrees * Math.PI / 180), 512 + r * (float)Math.Sin(degrees * Math.PI / 180));

    static SKShader Radial(float r, params SKColor[] colors) => SKShader.CreateRadialGradient(new(512, 512), r, colors, SKShaderTileMode.Clamp);

    static void Glowing(SKCanvas canvas, SKPoint p, float r, SKColor color, SKColor glow)
    {
        using (var halo = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(p, r * 2.1f, [glow.WithAlpha(120), glow.WithAlpha(0)], SKShaderTileMode.Clamp) })
            canvas.DrawCircle(p, r * 2.1f, halo);
        using var dot = new SKPaint { IsAntialias = true, Color = color };
        canvas.DrawCircle(p, r, dot);
    }

    public static void Trefoil(SKCanvas canvas, Tile k, Level level)
    {
        var (w, gap, shadow, size) = level switch { Level.Full => (78f, 0f, 46f, 92f), Level.Bold => (88f, 16f, 0f, 92f), _ => (120f, 28f, 0f, 92f) };
        // The trefoil knot, cut at its three tips into three equal strands.
        Strand Third(int n) => Strand.FromFunction(u =>
        {
            var t = MathF.PI / 3 + (n + u) * 2 * MathF.PI / 3;
            return (512 + size * (MathF.Sin(t) + 2 * MathF.Sin(2 * t)), 512 - size * .3f + size * (2 * MathF.Cos(2 * t) - MathF.Cos(t)) * 1f, -MathF.Sin(3 * t));
        }, w, k.A);
        Strand[] s = [Third(0), Third(1), Third(2)];
        foreach (var strand in s) strand.Shader = Radial(300, k.B, k.A);
        Weave.Draw(canvas, gap, true, s, shadow: shadow, ignore: p => SKPoint.Distance(p, new(512, 484)) > 240);
    }

    public static void Lattice(SKCanvas canvas, Tile k, Level level)
    {
        var (n, r, w) = level switch { Level.Full => (8, 152f, 15f), Level.Bold => (6, 152f, 32f), _ => (4, 160f, 64f) };
        for (var i = 0; i < n; i++)
        {
            var c = Polar(r, -90 + 360.0 / n * i);
            using var veil = new SKPaint { IsAntialias = true, Color = k.A.WithAlpha((byte)(level == Level.Full ? 34 : 40)) };
            canvas.DrawCircle(c, r, veil);
        }
        for (var i = 0; i < n; i++)
        {
            var c = Polar(r, -90 + 360.0 / n * i);
            using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w, Shader = Radial(r * 2, k.C, k.A) };
            canvas.DrawCircle(c, r, line);
        }
        if (level == Level.Full) Glowing(canvas, new(512, 512), 26, k.A, k.A);
    }

    public static void Constellation(SKCanvas canvas, Tile k, Level level)
    {
        var (c, a, b, line) = level switch { Level.Full => (66f, 40f, 27f, 14f), Level.Bold => (88f, 56f, 0f, 30f), _ => (112f, 72f, 0f, 0f) };
        float ra = level == Level.Full ? 168 : 250, rb = 300;
        for (var i = 0; i < 6; i++)
        {
            var inner = Polar(ra, -90 + 60 * i);
            using var bond = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeWidth = line, Shader = Radial(rb, k.A, k.B) };
            if (line > 0) canvas.DrawLine(new(512, 512), inner, bond);
            if (b == 0) continue;
            foreach (var side in new[] { -30, 30 })
            {
                // A shallow curve from this node to the outer node on either side.
                var outer = Polar(rb, -90 + 60 * i + side);
                var mid = Polar((ra + rb) / 2 - 34, -90 + 60 * i + side * .62);
                using var path = new SKPath();
                path.MoveTo(inner); path.QuadTo(mid, outer);
                using var fine = bond.Clone(); fine.StrokeWidth = line * .8f;
                canvas.DrawPath(path, fine);
            }
        }
        for (var i = 0; i < 6; i++)
        {
            if (b > 0) Glowing(canvas, Polar(rb, -60 + 60 * i), b, k.C, k.B);
            Glowing(canvas, Polar(ra, -90 + 60 * i), a, k.B, k.B);
        }
        Glowing(canvas, new(512, 512), c, k.A, k.A);
    }

    public static void Strands(SKCanvas canvas, Tile k, Level level)
    {
        // The first harmonics of a string, with their mirror images, between two points.
        var (w, gap, shadow, dot, top) = level switch { Level.Full => (46f, 0f, 26f, 56f, 222f), Level.Bold => (62f, 18f, 0f, 76f, 226f), _ => (112f, 0f, 0f, 112f, 256f) };
        var bottom = 1024 - top;
        SKShader Vertical() => SKShader.CreateLinearGradient(new(512, top), new(512, bottom), [k.A, k.B, k.A], [0, .5f, 1], SKShaderTileMode.Clamp);
        Strand Make(int harmonic, float amp, float phase) => Strand.FromFunction(t =>
            (512 + amp * MathF.Sin(harmonic * MathF.PI * t), top + (bottom - top) * t, MathF.Cos(harmonic * MathF.PI * t + phase)), w, k.A);
        List<Strand> s = [Make(1, 248, 0), Make(1, -248, 0)];
        if (level != Level.Tiny) s.AddRange([Make(2, 138, 0), Make(2, -138, MathF.PI)]);
        foreach (var x in s) x.Shader = Vertical();
        Weave.Draw(canvas, gap, true, [.. s], shadow: shadow, ignore: p => p.Y < top + 110 || p.Y > bottom - 110);
        foreach (var y in new[] { top, bottom })
            if (level == Level.Full) Glowing(canvas, new(512, y), dot, k.Accent, k.Accent); else Draw.Dot(canvas, 512, y, dot, k.Accent);
    }
}

static class Sheets
{
    static readonly SKTypeface Regular = SKTypeface.FromFamilyName("Helvetica Neue");
    static readonly SKTypeface Medium = SKTypeface.FromFamilyName("Helvetica Neue", SKFontStyleWeight.Medium, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

    static void Text(SKCanvas canvas, string text, float x, float y, float size, SKColor color, bool medium = false, SKTextAlign align = SKTextAlign.Left)
    {
        using var font = new SKFont(medium ? Medium : Regular, size) { Subpixel = true };
        using var p = new SKPaint { IsAntialias = true, Color = color };
        canvas.DrawText(text, x, y, align, font, p);
    }

    static SKImage Make(int w, int h, SKColor back, Action<SKCanvas> draw)
    {
        using var s = SKSurface.Create(new SKImageInfo(w, h));
        s.Canvas.Clear(back);
        draw(s.Canvas);
        return s.Snapshot();
    }

    static readonly SKColor Dark = SKColor.Parse("#1C1917"), Muted = SKColor.Parse("#78716C"), Page = SKColor.Parse("#FAFAF9");

    /// <summary>Draws at real pixels and enlarges without smoothing, to show what a small size really has.</summary>
    static void Zoomed(SKCanvas canvas, Candidate c, int px, float x, float y, float size)
    {
        using var s = SKSurface.Create(new SKImageInfo(px, px, SKColorType.Rgba8888, SKAlphaType.Premul));
        s.Canvas.Clear(SKColors.Transparent);
        Draw.Icon(s.Canvas, c, c.Tile, 0, 0, px);
        using var img = s.Snapshot();
        canvas.DrawImage(img, new SKRect(x, y, x + size, y + size), new SKSamplingOptions(SKFilterMode.Nearest));
    }

    public static SKImage One(Candidate c, Tile[] tiles) => Make(1800, 1180, Page, canvas =>
    {
        Text(canvas, $"{c.Id}  {c.Name}", 60, 84, 44, Dark, true);
        Text(canvas, c.Idea, 60, 130, 28, Muted);
        Draw.Icon(canvas, c, c.Tile, 20, 140, 1024);
        Text(canvas, "128", 1150, 220, 24, Muted); Draw.Icon(canvas, c, c.Tile, 1150, 240, 128);
        Text(canvas, "32", 1350, 220, 24, Muted); Draw.Icon(canvas, c, c.Tile, 1350, 290, 32);
        Text(canvas, "16", 1480, 220, 24, Muted); Draw.Icon(canvas, c, c.Tile, 1480, 298, 16);
        Text(canvas, "32, enlarged", 1150, 440, 24, Muted); Zoomed(canvas, c, 32, 1150, 460, 256);
        Text(canvas, "16, enlarged", 1450, 440, 24, Muted); Zoomed(canvas, c, 16, 1450, 460, 256);
        Text(canvas, "Other backgrounds", 1150, 790, 24, Muted);
        var x = 1150f;
        foreach (var t in tiles.Where(t => t != c.Tile)) { Draw.Icon(canvas, c, t, x, 810, 180); x += 200; }
    });

    public static SKImage Overview(Candidate[] all) => Make(75 + all.Length * 445, 620, Page, canvas =>
    {
        for (var i = 0; i < all.Length; i++)
        {
            var x = 50 + i * 445f;
            Draw.Icon(canvas, all[i], all[i].Tile, x, 40, 420);
            Text(canvas, $"{all[i].Id}  {all[i].Name}", x + 210, 520, 34, Dark, true, SKTextAlign.Center);
            Text(canvas, all[i].Idea, x + 210, 562, 19, Muted, false, SKTextAlign.Center);
        }
    });

    /// <summary>A light and a dark Dock at 2x (64 pt icons) and at 1x-equivalent small Dock (32 pt).</summary>
    public static SKImage Dock(Candidate[] all) => Make(2000, 1120, SKColors.White, canvas =>
    {
        void Half(float top, SKColor a, SKColor b, SKColor shelf, SKColor rim, SKColor ink, string name)
        {
            using var bg = new SKPaint { Shader = SKShader.CreateLinearGradient(new(0, top), new(2000, top + 560), [a, b], SKShaderTileMode.Clamp) };
            canvas.DrawRect(0, top, 2000, 560, bg);
            Text(canvas, name, 60, top + 64, 30, ink, true);
            void Shelf(float y, float icon, float gap, float pad)
            {
                var n = all.Length + 4;
                var w = n * icon + (n - 1) * gap + pad * 2;
                var x = 1000 - w / 2;
                using var f = new SKPaint { IsAntialias = true, Color = shelf };
                using var e = new SKPaint { IsAntialias = true, Color = rim, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
                var r = new SKRect(x, y, x + w, y + icon + pad * 2);
                canvas.DrawRoundRect(r, icon * .36f, icon * .36f, f); canvas.DrawRoundRect(r, icon * .36f, icon * .36f, e);
                // Plain neighbours, so the candidates are judged next to other icons of the usual shape.
                var neighbours = new[] { "#34C759", "#FF9F0A", "#8E8E93", "#E9E9EB" };
                for (var i = 0; i < n; i++)
                {
                    var ix = x + pad + i * (icon + gap);
                    if (i >= 2 && i < 2 + all.Length) Draw.Icon(canvas, all[i - 2], all[i - 2].Tile, ix, y + pad, icon);
                    else Draw.Icon(canvas, new("", "", "", Tile.Paper, (_, _, _) => { }), Tile.Paper with { Top = SKColor.Parse(neighbours[i < 2 ? i : i - all.Length]), Bottom = SKColor.Parse(neighbours[i < 2 ? i : i - all.Length]) }, ix, y + pad, icon);
                }
            }
            Shelf(top + 110, 160, 14, 18);
            Shelf(top + 380, 64, 6, 10);
        }
        Half(0, SKColor.Parse("#DCE6F2"), SKColor.Parse("#F3E6DC"), new SKColor(255, 255, 255, 150), new SKColor(255, 255, 255, 190), Dark, "Light Dock, large and small");
        Half(560, SKColor.Parse("#1B2030"), SKColor.Parse("#33263A"), new SKColor(40, 42, 50, 190), new SKColor(255, 255, 255, 36), SKColors.White, "Dark Dock, large and small");
    });

    public static SKImage Small(Candidate[] all) => Make(75 + all.Length * 365, 900, Page, canvas =>
    {
        Text(canvas, "Real pixels enlarged 8x (32 px) and 12x (16 px)", 60, 60, 30, Dark, true);
        for (var i = 0; i < all.Length; i++)
        {
            var x = 60 + i * 365f;
            Text(canvas, $"{all[i].Id}  {all[i].Name}", x, 120, 26, Dark, true);
            Zoomed(canvas, all[i], 32, x, 140, 256);
            Zoomed(canvas, all[i], 16, x, 430, 192);
            Draw.Icon(canvas, all[i], all[i].Tile, x, 680, 128);
            Draw.Icon(canvas, all[i], all[i].Tile, x + 150, 728, 32);
            Draw.Icon(canvas, all[i], all[i].Tile, x + 210, 736, 16);
        }
    });

    public static SKImage Tiles(Candidate[] all, Tile[] tiles) => Make(200 + all.Length * 260, 60 + tiles.Length * 250, Page, canvas =>
    {
        for (var t = 0; t < tiles.Length; t++)
        {
            Text(canvas, tiles[t].Name, 40, 150 + t * 250, 28, Muted);
            for (var i = 0; i < all.Length; i++) Draw.Icon(canvas, all[i], tiles[t], 190 + i * 260, 30 + t * 250, 230);
        }
    });
}
