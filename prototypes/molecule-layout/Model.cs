using System.Text.Json;

namespace MoleculeLayout;

/// <summary>One conversation: a dot. Positions and label placement are filled in by <see cref="Molecule"/>.</summary>
public sealed class Dot
{
    public required string Id { get; init; }
    public string? ParentId { get; init; }
    public long Created { get; init; }
    public required string Title { get; init; }
    public int Questions { get; init; }
    public bool HasSummary { get; init; }

    public int Index;
    public int Parent = -1;
    public readonly List<int> Children = [];
    public int Root;
    public int Depth;
    public int Weight;
    public int Size;
    public double Radius;
    public double Footprint;

    public double X;
    public double Y;

    public string[] Lines = [];
    public string? DateLine;
    public double LabelW;
    public double LabelH;

    /// <summary>Label box, relative to the dot's centre: left, top.</summary>
    public double LabelDx;
    public double LabelDy;
    public int LabelSide;
}

public sealed class Workspace
{
    public required string Name { get; init; }
    public required List<Dot> Dots { get; init; }
    public List<int> Roots { get; } = [];
    public string? NowPicture { get; init; }

    /// <summary>Links parents and children, computes weights (questions of the dot and all its branches) and sizes.</summary>
    public void Prepare()
    {
        Dots.Sort((a, b) => a.Created != b.Created ? a.Created.CompareTo(b.Created) : string.CompareOrdinal(a.Id, b.Id));
        var byId = new Dictionary<string, int>();
        for (var i = 0; i < Dots.Count; i++)
        {
            Dots[i].Index = i;
            byId[Dots[i].Id] = i;
        }

        foreach (var d in Dots)
        {
            if (d.ParentId is { } p && byId.TryGetValue(p, out var pi))
            {
                d.Parent = pi;
                Dots[pi].Children.Add(d.Index);
            }
            else
            {
                Roots.Add(d.Index);
            }
        }

        // Dots are sorted by creation and a branch is created after its parent, so a reverse pass is bottom-up.
        for (var i = Dots.Count - 1; i >= 0; i--)
        {
            var d = Dots[i];
            d.Weight += Math.Max(d.Questions, 1);
            d.Size += 1;
            if (d.Parent >= 0)
            {
                Dots[d.Parent].Weight += d.Weight;
                Dots[d.Parent].Size += d.Size;
            }
        }

        foreach (var r in Roots)
        {
            Walk(r, r, 0);
        }

        foreach (var d in Dots)
        {
            d.Radius = Math.Clamp(5 + (1.3765 * Math.Sqrt(Math.Max(0, d.Weight - 1))), 5, 14);
            d.Footprint = d.Radius + (d.HasSummary ? 4.5 : 0);
        }

        // A faint date line under a root whose day differs from the previous root's (design §4.4).
        string? previousDay = null;
        foreach (var r in Roots)
        {
            var day = DateTimeOffset.FromUnixTimeMilliseconds(Dots[r].Created).ToLocalTime().ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture);
            if (day != previousDay)
            {
                Dots[r].DateLine = day;
            }

            previousDay = day;
        }

        foreach (var d in Dots)
        {
            Labels.Measure(d);
        }
    }

    private void Walk(int i, int root, int depth)
    {
        Dots[i].Root = root;
        Dots[i].Depth = depth;
        foreach (var c in Dots[i].Children)
        {
            Walk(c, root, depth + 1);
        }
    }

    public static Workspace Load(string path, string? nowPicture)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var dots = new List<Dot>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            dots.Add(new Dot
            {
                Id = e.GetProperty("id").GetString()!,
                ParentId = e.GetProperty("parent").ValueKind == JsonValueKind.String ? e.GetProperty("parent").GetString() : null,
                Created = e.GetProperty("created").GetInt64(),
                Title = e.GetProperty("title").GetString()!,
                Questions = e.GetProperty("q").GetInt32(),
                HasSummary = e.GetProperty("summary").GetInt32() != 0,
            });
        }

        return new Workspace { Name = Path.GetFileNameWithoutExtension(path), Dots = dots, NowPicture = nowPicture };
    }
}

/// <summary>Label text: whole titles, wrapped to a narrow column so a label sits beside its dot like an atom's symbol.</summary>
public static class Labels
{
    public const double FontSize = 13;
    public const double LineHeight = 17;
    public const double DateSize = 11;
    public const double MaxLineWidth = 118;

    public static double CharWidth(char c) =>
        c >= 0x2E80 ? FontSize :
        char.IsUpper(c) || char.IsDigit(c) ? 8.2 :
        c == ' ' ? 3.7 :
        c is 'i' or 'l' or 'j' or '.' or ',' or ';' or ':' or '\'' ? 3.6 :
        c is 'm' or 'w' ? 10.5 :
        6.9;

    public static double Width(string s) => s.Sum(CharWidth);

    public static void Measure(Dot d)
    {
        d.Lines = Wrap(d.Title);
        d.LabelW = d.Lines.Max(Width);
        d.LabelH = d.Lines.Length * LineHeight;
        if (d.DateLine is { } date)
        {
            d.LabelW = Math.Max(d.LabelW, date.Length * 6.4);
            d.LabelH += DateSize + 4;
        }
    }

    /// <summary>Breaks CJK text anywhere and other text at spaces, so no line is wider than <see cref="MaxLineWidth"/>.</summary>
    public static string[] Wrap(string title)
    {
        if (Width(title) <= MaxLineWidth * 1.15)
        {
            return [title];
        }

        var lines = new List<string>();
        var line = new System.Text.StringBuilder();
        var i = 0;
        while (i < title.Length)
        {
            // Take one unit: a CJK character, or a run of non-CJK characters up to and including a space.
            var j = i;
            if (title[i] >= 0x2E80)
            {
                j = i + 1;
            }
            else
            {
                while (j < title.Length && title[j] < 0x2E80 && title[j] != ' ')
                {
                    j++;
                }

                if (j < title.Length && title[j] == ' ')
                {
                    j++;
                }

                if (j == i)
                {
                    j = i + 1;
                }
            }

            var unit = title[i..j];
            if (line.Length > 0 && Width(line.ToString() + unit.TrimEnd()) > MaxLineWidth)
            {
                lines.Add(line.ToString().TrimEnd());
                line.Clear();
                unit = unit.TrimStart();
            }

            line.Append(unit);
            i = j;
        }

        if (line.Length > 0)
        {
            lines.Add(line.ToString().TrimEnd());
        }

        // Avoid a lonely last character or two: rebalance the last two lines.
        if (lines.Count >= 2 && lines[^1].Length <= 2 && lines[^2].Length > 4)
        {
            var take = (lines[^2].Length - lines[^1].Length) / 2;
            lines[^1] = lines[^2][^take..] + lines[^1];
            lines[^2] = lines[^2][..^take];
        }

        return [.. lines];
    }
}
