using System.Diagnostics;
using System.Net;
using System.Text;
using MoleculeLayout;

// Usage: dotnet run -c Release -- <data folder with workspace .json files> <output folder> [folder of "now" pictures]
// Each .json is a list of {id, parent, created, title, q, summary}; see README.md for the export command.
var dataDir = args.Length > 0 ? args[0] : "data";
var outDir = args.Length > 1 ? args[1] : "screenshots";
var nowDir = args.Length > 2 && args[2] != "-" ? args[2] : null;
var settings = new Settings();
// Optional knob overrides for comparisons: name=value pairs after the folders, e.g. ThreadPreference=1.
foreach (var pair in args.Skip(3))
{
    var (name, value) = (pair.Split('=')[0], double.Parse(pair.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture));
    var property = typeof(Settings).GetProperty(name) ?? throw new ArgumentException($"No setting {name}");
    settings = settings with { };
    property.SetValue(settings, property.PropertyType == typeof(int) ? (object)(int)value : value);
}

Directory.CreateDirectory(outDir);

var workspaces = new List<Workspace>();
if (Directory.Exists(dataDir))
{
    foreach (var file in Directory.GetFiles(dataDir, "*.json").Order(StringComparer.Ordinal))
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var now = nowDir is null ? null : Directory.GetFiles(nowDir, $"{name}*.png").FirstOrDefault();
        workspaces.Add(Workspace.Load(file, now));
    }
}

workspaces.AddRange(Synthetic.All());

var html = new StringBuilder();
html.Append("<!doctype html><meta charset=utf-8><title>Molecule layout</title><style>body{font:14px -apple-system,PingFang SC,sans-serif;background:#eef0f2;margin:24px;color:#222}section{margin:0 0 40px}h2{font-size:17px;margin:0 0 4px}p{margin:0 0 10px;color:#666;font-size:12px}.row{display:flex;gap:16px;align-items:flex-start;flex-wrap:wrap}.row figure{margin:0;background:#fff;padding:8px;border-radius:6px}figcaption{font-size:12px;color:#888;margin-bottom:4px}img{max-width:100%}</style>");
html.Append("<h1 style=\"font-size:20px\">Spatial view: molecule layout (prototype)</h1><p style=\"font-size:13px;color:#444;max-width:820px\">"
    + "At every dot its lines spread to the widest angles; a bigger branch takes more room, and turns alternate sides so chains never curl. "
    + "Each exploration is one molecule; molecules keep a gap between them and the heaviest sits in the middle. Titles are whole, wrapped to a narrow column, "
    + "and go where the dot has the most free space. Dotted arrows are the time thread between explorations. "
    + "First your four workspaces (copies of your data), then made-up larger ones. The grey line under each title lists the automatic checks (things that must not happen).</p>");
var report = new StringBuilder();
foreach (var ws in workspaces)
{
    ws.Prepare();
    var watch = Stopwatch.StartNew();
    var molecule = new Molecule(ws, settings);
    molecule.Run();
    var ms = watch.ElapsedMilliseconds;
    var (svg, w, h) = Picture.Svg(ws, molecule);
    var file = Slug(ws.Name) + ".svg";
    File.WriteAllText(Path.Combine(outDir, file), svg);
    var measure = Picture.Measure(ws, molecule);
    report.AppendLine($"{ws.Name}: {measure}; {ms} ms; {w}x{h}");
    html.Append($"<section><h2>{WebUtility.HtmlEncode(ws.Name)}</h2><p>{WebUtility.HtmlEncode(measure)}</p><div class=row>");
    if (ws.NowPicture is { } now)
    {
        // Embedded, so the page is one file that opens anywhere.
        var data = Convert.ToBase64String(File.ReadAllBytes(now));
        html.Append($"<figure><figcaption>Now (round 2, the app's spatial view)</figcaption><img src=\"data:image/png;base64,{data}\" style=\"max-width:620px\"></figure>");
    }

    html.Append($"<figure><figcaption>Molecule (prototype)</figcaption><div style=\"max-width:{w}px\">{svg.Replace($"width=\"{w}\" height=\"{h}\"", "width=\"100%\"")}</div></figure></div></section>");
}

File.WriteAllText(Path.Combine(outDir, "index.html"), html.ToString());
File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
Console.Write(report);

static string Slug(string name) => new([.. name.Select(c => char.IsLetterOrDigit(c) ? c : '-')]);
