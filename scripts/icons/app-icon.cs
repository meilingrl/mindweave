// Draws the application icon and writes it in the two formats the platforms want.
//
//   dotnet run scripts/icons/app-icon.cs            rewrite src/Mindweave.Desktop/Assets/Mindweave.icns and Mindweave.ico
//   dotnet run scripts/icons/app-icon.cs -- DIR     also write every size as a PNG into DIR, to look at
//
// The drawing is a brain made of one line, like a strand of yarn: the owner's choice of 2026-11-24, traced from the
// picture they sent (prototypes/app-icon/README.md). The points below are the source; the two files are generated
// and committed, so a build needs neither this script nor SkiaSharp.
//
// Every size is drawn from the points, not scaled down. From 100 px it is the whole line. Below that the whole line
// turns to mud, so 24 to 99 px draw the outline, the stem and one inner fold with a bolder line, and smaller sizes
// only the outline and the stem.
//
// macOS: a 1024 canvas with an 824 body (the system's icon grid), in Mindweave.icns. Windows: the body fills the
// canvas, in Mindweave.ico, which is also the window icon.
#:package SkiaSharp@3.119.4
#:property ManagePackageVersionsCentrally=false
#:property TreatWarningsAsErrors=false
#:property AnalysisMode=None
#:property PublishAot=false

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using SkiaSharp;

var root = Path.GetFullPath(Path.Combine(Here(), "..", ".."));
var assets = Path.Combine(root, "src", "Mindweave.Desktop", "Assets");
Directory.CreateDirectory(assets);
var look = args.FirstOrDefault(a => !a.StartsWith('-'));
if (look is not null) Directory.CreateDirectory(look);

// icns: type code and pixel size. The @2x entries are the same pixels under another code.
(string Code, int Px)[] mac =
[
    ("icp4", 16), ("icp5", 32), ("ic11", 32), ("ic12", 64), ("ic07", 128), ("ic13", 256), ("ic08", 256), ("ic14", 512), ("ic09", 512), ("ic10", 1024),
];
int[] windows = [16, 20, 24, 32, 40, 48, 64, 128, 256];

var macPng = mac.Select(e => e.Px).Distinct().ToDictionary(px => px, px => Icon.Draw(px, Icon.MacBody));
var winPng = windows.ToDictionary(px => px, px => Icon.Draw(px, Icon.WindowsBody));

File.WriteAllBytes(Path.Combine(assets, "Mindweave.icns"), Container.Icns(mac.Select(e => (e.Code, macPng[e.Px])).ToArray()));
File.WriteAllBytes(Path.Combine(assets, "Mindweave.ico"), Container.Ico(windows.Select(px => (px, winPng[px])).ToArray()));
Console.WriteLine($"wrote {Path.Combine(assets, "Mindweave.icns")} and Mindweave.ico");

if (look is not null)
{
    foreach (var (px, png) in macPng) File.WriteAllBytes(Path.Combine(look, $"mac-{px}.png"), png);
    foreach (var (px, png) in winPng) File.WriteAllBytes(Path.Combine(look, $"windows-{px}.png"), png);
    Console.WriteLine($"wrote the sizes into {look}");
}

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

static class Icon
{
    static readonly SKColor Ink = new(0x0B, 0x0B, 0x0B);
    static readonly SKColor Yarn = new(0xF4, 0xF1, 0xEA);

    // The body on the 1024 canvas: its inset from the edge.
    public const float MacBody = 100, WindowsBody = 24;

    // The owner's drawing, in the 1180 x 1180 units of the picture: up the stem, the inner folds, then once round
    // the outline with a small loop at each turn.
    static readonly float[] Line =
    [
        478,1075, 495,985, 550,930, 630,885, 668,830, 672,790, 652,732, 610,697, 560,688, 510,705, 485,742, 495,782, 535,800, 572,783, 585,742, 562,702, 520,675, 450,632,
        370,597, 312,552, 285,482, 300,402, 350,342, 430,315, 492,335, 545,385,
        600,452, 660,532, 730,588, 800,600, 868,572, 908,512, 905,440, 870,385, 815,360, 770,375, 745,420, 765,465, 820,490, 890,497, 960,485, 1008,442, 1020,382, 992,322, 932,287,
        860,270, 770,270, 680,287, 600,322, 545,377, 500,442, 475,512, 486,570, 530,610, 600,632, 670,662, 720,712, 770,780, 830,820,
        900,815, 960,788, 998,732, 1005,662, 985,607, 940,586, 900,606, 890,645, 915,680, 960,690, 1010,680,
        1070,652, 1122,600, 1158,520, 1165,422, 1130,322, 1062,232, 962,152, 842,106, 722,95, 642,110, 588,145, 565,190, 585,235, 625,246, 655,220, 645,180, 600,155,
        540,132, 470,123, 400,130, 312,160, 232,215, 187,280, 175,335, 182,382, 215,410, 254,400, 266,371, 241,346, 192,338, 132,350,
        70,387, 26,452, 15,522, 36,592, 88,648, 150,680, 200,685, 234,660, 230,622, 196,608, 152,625, 112,665, 98,720, 120,770, 170,800, 230,805, 300,798, 380,808, 450,830, 495,852,
    ];

    // The pieces of the same line that small sizes keep.
    static readonly float[] Outline =
    [
        600,640, 670,662, 720,712, 770,780, 830,820, 900,815, 960,788, 1010,720, 1070,652, 1122,600, 1158,520, 1165,422, 1130,322, 1062,232, 962,152, 842,106, 722,95,
        640,115, 600,150, 540,132, 470,123, 400,130, 312,160, 232,215, 187,280, 178,340, 130,350, 70,387, 26,452, 15,522, 36,592, 88,648, 125,670, 105,720, 120,770,
        170,800, 230,805, 300,798, 380,808, 450,830, 495,852,
    ];
    static readonly float[] Stem = [478,1075, 495,985, 550,930, 630,885, 668,830, 672,790, 652,732, 610,697];
    static readonly float[] Fold =
    [
        520,675, 450,632, 370,597, 312,552, 285,482, 300,402, 350,342, 430,315, 492,335, 545,385, 600,322, 680,287, 770,270, 860,270, 932,287, 992,322, 1020,382, 1008,442, 960,485,
    ];

    public static byte[] Draw(int px, float inset)
    {
        using var surface = SKSurface.Create(new SKImageInfo(px, px, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(px / 1024f);

        var body = 1024 - 2 * inset;
        using var fill = new SKPaint { Color = Ink, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(inset, inset, 1024 - inset, 1024 - inset), body * .2245f, body * .2245f, fill);

        // The line's own units: the picture's centre goes to the middle of the body, the drawing takes about 78 % of it.
        canvas.Translate(512, 512);
        canvas.Scale(.56f * body / 824f);
        canvas.Translate(-590, -585);
        var (width, pieces) = px switch
        {
            >= 100 => (30f, new[] { Line }),
            >= 24 => (58f, new[] { Outline, [.. Stem, .. Fold] }),
            _ => (92f, new[] { Outline, Stem }),
        };
        using var stroke = new SKPaint { Color = Yarn, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        foreach (var piece in pieces)
        {
            using var path = Smooth(piece);
            canvas.DrawPath(path, stroke);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // One smooth curve through the points (Catmull-Rom, written as cubic curves).
    static SKPath Smooth(float[] xy)
    {
        var n = xy.Length / 2;
        SKPoint At(int i) { i = Math.Clamp(i, 0, n - 1); return new SKPoint(xy[2 * i], xy[2 * i + 1]); }
        var path = new SKPath();
        path.MoveTo(At(0));
        for (var i = 0; i < n - 1; i++)
        {
            SKPoint a = At(i - 1), b = At(i), c = At(i + 1), d = At(i + 2);
            path.CubicTo(b.X + (c.X - a.X) / 6, b.Y + (c.Y - a.Y) / 6, c.X - (d.X - b.X) / 6, c.Y - (d.Y - b.Y) / 6, c.X, c.Y);
        }
        return path;
    }
}

static class Container
{
    // Apple icon image: "icns", the file length, then entries of a four-letter type, the entry length and a PNG.
    public static byte[] Icns((string Code, byte[] Png)[] entries)
    {
        using var file = new MemoryStream();
        Span<byte> number = stackalloc byte[4];
        void Number(int value, Span<byte> buffer) { BinaryPrimitives.WriteInt32BigEndian(buffer, value); file.Write(buffer); }
        file.Write(Encoding.ASCII.GetBytes("icns"));
        Number(8 + entries.Sum(e => 8 + e.Png.Length), number);
        foreach (var (code, png) in entries)
        {
            file.Write(Encoding.ASCII.GetBytes(code));
            Number(8 + png.Length, number);
            file.Write(png);
        }
        return file.ToArray();
    }

    // Windows icon: a directory of sizes, then the PNGs.
    public static byte[] Ico((int Px, byte[] Png)[] entries)
    {
        using var file = new MemoryStream();
        using var writer = new BinaryWriter(file);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)entries.Length);
        var offset = 6 + 16 * entries.Length;
        foreach (var (px, png) in entries)
        {
            writer.Write((byte)(px >= 256 ? 0 : px)); writer.Write((byte)(px >= 256 ? 0 : px));
            writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(png.Length); writer.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in entries) writer.Write(png);
        writer.Flush();
        return file.ToArray();
    }
}
