using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace FontCompare;

/// <summary>One font choice: the family list for the interface and the one for answer text.</summary>
internal sealed record Preset(string Name, string Note, string Ui, string Answer);

internal static class Presets
{
    // The real app today (App.axaml, Program.cs): Inter for the interface with the system choosing a Chinese
    // fallback, and a CJK-first list for answers.
    public const string CurrentAnswer =
        "PingFang SC, Microsoft YaHei UI, Microsoft YaHei, Noto Sans CJK SC, Noto Sans SC, Source Han Sans SC, $Default";

    public static readonly Preset[] All =
    [
        new("A · Now", "Window font from the Fluent theme (Inter) + system fallback; answers PingFang", "$Default", CurrentAnswer),
        new("B · PingFang SC", "macOS native everywhere (Windows has no PingFang)", "PingFang SC", "PingFang SC"),
        new("C · Inter + PingFang", "Latin from Inter, Chinese from PingFang · Avalonia drops Chinese bold mid-paragraph", "fonts:Inter#Inter, PingFang SC", "fonts:Inter#Inter, PingFang SC"),
        new("D · Noto Sans SC", "= Source Han Sans; could be bundled, same on Mac and Windows", "Noto Sans CJK SC", "Noto Sans CJK SC"),
        new("E · Inter + Noto Sans SC", "Latin from Inter, Chinese from Noto · Avalonia drops Chinese bold mid-paragraph", "fonts:Inter#Inter, Noto Sans CJK SC", "fonts:Inter#Inter, Noto Sans CJK SC"),
        new("F · Serif answers", "Answers Charter; Chinese falls back to PingFang, not Noto Serif · bold lost", "PingFang SC", "Charter, Noto Serif CJK SC"),
        new("G · Hiragino Sans GB", "Older macOS Chinese face, lighter strokes", "Hiragino Sans GB", "Hiragino Sans GB"),
    ];
}

/// <summary>Settings shared by every pane.</summary>
internal sealed class Globals
{
    public TextRenderingMode Rendering = TextRenderingMode.Antialias;
    public FontWeight Heading = FontWeight.SemiBold;
    public FontWeight Body = FontWeight.Normal;
    public double Size = 14;
    public bool Dark;
    public int Panes = 2;
}

internal sealed class App : Application
{
    public static string[] Args = [];

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(Args);
        }

        base.OnFrameworkInitializationCompleted();
    }
}

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.Args = args;
        if (args.Contains("--diag"))
        {
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            Diagnostics.Run();
            return;
        }

        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
    }
}

internal sealed class MainWindow : Window
{
    private readonly Globals globals = new();
    private readonly int[] chosen = [0, 3, 1, 5];
    private readonly Grid panes = new() { ColumnDefinitions = new("*,*") };

    public MainWindow(string[] args)
    {
        Title = "Mindweave font comparison";
        Width = 1560;
        Height = 1000;

        // --shot <file.png> [--four] [--dark] [--presets 0,2,3,5] [--rendering subpixel] [--heading medium] [--body 350] [--size 15]
        string? shot = Option(args, "--shot");
        globals.Dark = args.Contains("--dark");
        if (Option(args, "--presets") is { } list)
        {
            var picks = list.Split(',').Select(int.Parse).ToArray();
            for (int i = 0; i < Math.Min(4, picks.Length); i++) chosen[i] = picks[i];
        }
        if (Option(args, "--rendering") == "subpixel") globals.Rendering = TextRenderingMode.SubpixelAntialias;
        if (Option(args, "--heading") == "medium") globals.Heading = FontWeight.Medium;
        if (Option(args, "--body") is { } body) globals.Body = (FontWeight)int.Parse(body);
        if (args.Contains("--four")) globals.Panes = 4;
        if (Option(args, "--size") is { } size) globals.Size = double.Parse(size, System.Globalization.CultureInfo.InvariantCulture);

        var root = new DockPanel();
        var bar = BuildBar();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(panes);
        Content = root;
        Rebuild();

        if (shot != null)
        {
            Opened += (_, _) => DispatcherTimer.RunOnce(() => { Save(shot); Close(); }, TimeSpan.FromMilliseconds(600));
        }
    }

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private void Save(string path)
    {
        double scale = RenderScaling;
        var size = new PixelSize((int)(Bounds.Width * scale), (int)(Bounds.Height * scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(this);
        bitmap.Save(path);
    }

    private Control BuildBar()
    {
        var bar = new WrapPanel { Margin = new Thickness(14, 10), Orientation = Orientation.Horizontal };

        bar.Children.Add(Choice("Rendering", ["Grayscale (now)", "Subpixel"],
            globals.Rendering == TextRenderingMode.SubpixelAntialias ? 1 : 0,
            i => globals.Rendering = i == 1 ? TextRenderingMode.SubpixelAntialias : TextRenderingMode.Antialias));
        bar.Children.Add(Choice("Headings", ["SemiBold (now)", "Medium"],
            globals.Heading == FontWeight.Medium ? 1 : 0,
            i => globals.Heading = i == 1 ? FontWeight.Medium : FontWeight.SemiBold));
        bar.Children.Add(Choice("Body weight", ["400 (now)", "350", "300"],
            (int)globals.Body switch { 350 => 1, 300 => 2, _ => 0 },
            i => globals.Body = (FontWeight)(i switch { 1 => 350, 2 => 300, _ => 400 })));
        bar.Children.Add(Choice("Body size", ["14 (now)", "15"], globals.Size >= 15 ? 1 : 0, i => globals.Size = i == 1 ? 15 : 14));
        bar.Children.Add(Choice("Panes", ["2", "4"], globals.Panes == 4 ? 1 : 0, i => globals.Panes = i == 1 ? 4 : 2));
        bar.Children.Add(Choice("Theme", ["Light", "Dark"], globals.Dark ? 1 : 0, i => globals.Dark = i == 1));
        return bar;
    }

    private Control Choice(string label, string[] options, int selected, Action<int> apply)
    {
        var box = new ComboBox { ItemsSource = options, SelectedIndex = selected, MinWidth = 130 };
        box.SelectionChanged += (_, _) =>
        {
            apply(box.SelectedIndex);
            Rebuild();
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 0, 18, 0),
            Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, box },
        };
    }

    private void Rebuild()
    {
        RequestedThemeVariant = globals.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Background = new SolidColorBrush(Palette.Of(globals.Dark).Separator);
        panes.Children.Clear();
        panes.RowDefinitions = new(globals.Panes == 4 ? "*,*" : "*");
        for (int i = 0; i < globals.Panes; i++)
        {
            int slot = i;
            var pane = new PaneView(globals, Presets.All[chosen[slot]], index =>
            {
                chosen[slot] = index;
                Rebuild();
            });
            Grid.SetColumn(pane, slot % 2);
            Grid.SetRow(pane, slot / 2);
            pane.Margin = new Thickness(slot % 2 == 0 ? 0 : 1, slot / 2 == 0 ? 0 : 1, 0, 0);
            panes.Children.Add(pane);
        }
    }
}

/// <summary>The colours of the real app (Themes/Colors.axaml), light and dark.</summary>
internal sealed record Palette(Color Window, Color Sidebar, Color Primary, Color Muted, Color Selected, Color Bubble, Color Card, Color Border, Color Link, Color Separator)
{
    private static readonly Palette Light = new(
        Color.Parse("#FFFFFF"), Color.Parse("#F5F5F4"), Color.Parse("#1C1917"), Color.Parse("#78716C"), Color.Parse("#E7E5E4"),
        Color.Parse("#F0EFED"), Color.Parse("#F7F7F6"), Color.Parse("#ECEAE8"), Color.Parse("#2563EB"), Color.Parse("#D6D3D1"));

    private static readonly Palette DarkPalette = new(
        Color.Parse("#18191B"), Color.Parse("#1D1E22"), Color.Parse("#E6E8EE"), Color.Parse("#9299A6"), Color.Parse("#2A2D35"),
        Color.Parse("#26282E"), Color.Parse("#1E1F23"), Color.Parse("#2A2C31"), Color.Parse("#A9B8F5"), Color.Parse("#000000"));

    public static Palette Of(bool dark) => dark ? DarkPalette : Light;
}

/// <summary>A miniature of the Mindweave window (sidebar + conversation) drawn with one preset.</summary>
internal sealed class PaneView : DockPanel
{
    private readonly Globals g;
    private readonly Palette p;
    private readonly FontFamily? ui;
    private readonly FontFamily answer;

    public PaneView(Globals globals, Preset preset, Action<int> choose)
    {
        g = globals;
        p = Palette.Of(globals.Dark);
        // "$Default" means: set nothing and inherit the window font, as the real app does.
        ui = preset.Ui == "$Default" ? null : FontFamily.Parse(preset.Ui);
        answer = FontFamily.Parse(preset.Answer);
        Background = new SolidColorBrush(p.Window);
        RenderOptions.SetTextRenderingMode(this, globals.Rendering);

        var header = Header(preset, choose);
        SetDock(header, Dock.Top);
        Children.Add(header);

        var sidebar = Sidebar();
        SetDock(sidebar, Dock.Left);
        Children.Add(sidebar);
        Children.Add(Conversation());
    }

    private IBrush Brush(Color c) => new SolidColorBrush(c);

    private TextBlock Ui(string text, double size, Color color, FontWeight? weight = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? g.Body,
            Foreground = Brush(color),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (ui != null) block.FontFamily = ui;
        return block;
    }

    /// <summary>The window font the Fluent theme gives every control that sets none.</summary>
    public static FontFamily WindowFont() =>
        Application.Current?.FindResource("ContentControlThemeFontFamily") as FontFamily ?? FontFamily.Default;

    private Control Header(Preset preset, Action<int> choose)
    {
        var box = new ComboBox
        {
            ItemsSource = Presets.All.Select(x => x.Name).ToArray(),
            SelectedIndex = Array.IndexOf(Presets.All, preset),
            MinWidth = 210,
        };
        box.SelectionChanged += (_, _) => choose(box.SelectedIndex);

        var info = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = preset.Note, FontSize = 11.5, Foreground = Brush(p.Muted) });
        info.Children.Add(new TextBlock
        {
            Text = $"Chinese drawn with: interface {Resolve(ui ?? WindowFont(), '中')} · answers {Resolve(answer, '中')}   Latin: {Resolve(ui ?? WindowFont(), 'a')} · {Resolve(answer, 'a')}",
            FontSize = 11,
            Foreground = Brush(p.Muted),
        });

        return new Border
        {
            Background = Brush(p.Card),
            BorderBrush = Brush(p.Border),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 6),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { box, info } },
        };
    }

    /// <summary>Which font actually draws <paramref name="c"/> for this family list, after fallback.</summary>
    private string Resolve(FontFamily family, char c)
    {
        var layout = new TextLayout(c.ToString(), new Typeface(family, FontStyle.Normal, g.Body), 14, Brushes.Black);
        foreach (var line in layout.TextLines)
        {
            foreach (var run in line.TextRuns)
            {
                if (run is ShapedTextRun shaped)
                {
                    return shaped.GlyphRun.GlyphTypeface.FamilyName;
                }
            }
        }

        return "?";
    }

    private Control Sidebar()
    {
        var list = new StackPanel { Spacing = 1, Margin = new Thickness(8, 10) };
        list.Children.Add(Ui("我的工作区", 13, p.Primary, g.Heading));
        list.Children.Add(new Border { Height = 8 });
        foreach (var nav in new[] { "新对话", "总结网络", "使用统计", "设置" })
        {
            list.Children.Add(new Border { Padding = new Thickness(8, 5), Child = Ui(nav, 13, p.Primary) });
        }

        list.Children.Add(new Border { Height = 10 });
        list.Children.Add(new Border { Padding = new Thickness(8, 2), Child = Ui("最近", 11.5, p.Muted, g.Heading) });

        var rows = new (string Title, string Time, bool Selected, bool Unread)[]
        {
            ("蒸汽机如何改变了 19 世纪的城市", "10:42", true, false),
            ("摄影入门：光圈、快门与 ISO", "昨天", false, true),
            ("React 19 迁移笔记", "11月18日", false, false),
            ("周末读书 · 第 3 章的论证", "11月17日", false, false),
            ("Q3 预算表 2,480 元差额", "11月12日", false, false),
        };
        foreach (var row in rows)
        {
            var title = Ui(row.Title, 13, p.Primary, row.Unread ? g.Heading : null);
            var time = Ui(row.Time, 11.5, p.Muted);
            list.Children.Add(new Border
            {
                Background = row.Selected ? Brush(p.Selected) : Brushes.Transparent,
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(8, 5),
                Child = new StackPanel { Spacing = 1, Children = { title, time } },
            });
        }

        return new Border
        {
            Width = 210,
            Background = Brush(p.Sidebar),
            Child = list,
        };
    }

    private Control Conversation()
    {
        var column = new StackPanel { Spacing = 10, Margin = new Thickness(22, 16, 22, 12), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };

        column.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = Brush(p.Bubble),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 9),
            Margin = new Thickness(60, 0, 0, 0),
            Child = Question(),
        });

        column.Children.Add(AnswerHeading("结论"));
        column.Children.Add(AnswerParagraph(
            ("小字号下，", false), ("真正影响清晰度的是字重和渲染方式", true),
            ("，而不只是字体本身。Inter was designed for screens, so its Latin letters stay open at 12.5 px; 中文部分则取决于系统回退到哪一款字体。", false)));
        column.Children.Add(AnswerParagraph(
            ("1. 正文保持 14 px，行高约 1.6。", false)));
        column.Children.Add(AnswerParagraph(
            ("2. 标题用 Medium 代替 SemiBold，", false), ("对比依然足够", true), ("。", false)));
        column.Children.Add(AnswerParagraph(
            ("3. 数字与单位：1,204 tokens、8.2 秒、¥2,480.00、2026-11-21。", false)));

        column.Children.Add(AnswerHeading("为什么看起来发粗"));
        column.Children.Add(AnswerParagraph(
            ("屏幕上的文字是否清爽，取决于笔画的粗细、字面的大小和留白，以及渲染时边缘如何处理。同一款字体，在系统自带的应用里和在自绘界面的程序里，看起来可能并不一样：前者常用系统的文字渲染，后者用自己的绘制引擎，边缘的灰度会略多一些，于是笔画显得更粗、更软。中文字形结构复杂，小字号时这种差别尤其明显。", false)));
        column.Children.Add(AnswerParagraph(
            ("混排时还要注意中西文的协调：拉丁字母的高度、数字的宽度和标点的位置，都会影响一行文字是否整齐。", false)));

        var caption = Ui("已回答 · 8.2 秒 · 1,204 tokens", 12, p.Muted);
        column.Children.Add(caption);

        var composer = new Border
        {
            Background = Brush(p.Card),
            BorderBrush = Brush(p.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10),
            Margin = new Thickness(0, 6, 0, 0),
            Child = Ui("输入问题，Enter 发送，Shift+Enter 换行", g.Size, p.Muted),
        };
        column.Children.Add(composer);

        return new ScrollViewer { Content = column };
    }

    private TextBlock Question()
    {
        var block = Ui("比较一下 PingFang、Noto Sans 和 Inter 在 13 px 时的可读性，给 3 条建议。", g.Size, p.Primary);
        block.TextWrapping = TextWrapping.Wrap;
        block.TextTrimming = TextTrimming.None;
        return block;
    }

    private TextBlock AnswerHeading(string text) => new()
    {
        Text = text,
        FontFamily = answer,
        FontSize = g.Size + 3,
        FontWeight = g.Heading,
        Foreground = Brush(p.Primary),
    };

    private TextBlock AnswerParagraph(params (string Text, bool Bold)[] parts)
    {
        var block = new TextBlock
        {
            FontFamily = answer,
            FontSize = g.Size,
            FontWeight = g.Body,
            LineHeight = Math.Round(g.Size * 1.6),
            Foreground = Brush(p.Primary),
            TextWrapping = TextWrapping.Wrap,
        };
        foreach (var (text, bold) in parts)
        {
            block.Inlines!.Add(new Run(text) { FontWeight = bold ? FontWeight.SemiBold : g.Body });
        }

        return block;
    }
}

/// <summary>Prints which font really draws each kind of text for each preset (run with --diag).</summary>
internal static class Diagnostics
{
    public static void Run()
    {
        Console.WriteLine($"FontManager default family: {FontManager.Current.DefaultFontFamily.Name}");
        Console.WriteLine($"Fluent window font: {PaneView.WindowFont()}  ->  a={Describe(PaneView.WindowFont(), "a", FontWeight.Normal)}  中={Describe(PaneView.WindowFont(), "中", FontWeight.Normal)}");
        Console.WriteLine($"Unset TextBlock: {Describe(FontFamily.Default, "a", FontWeight.Normal)} | {Describe(FontFamily.Default, "中", FontWeight.Normal)}");
        foreach (var preset in Presets.All)
        {
            foreach (var (label, list) in new[] { ("ui", preset.Ui), ("answer", preset.Answer) })
            {
                var family = FontFamily.Parse(list);
                Console.WriteLine($"{preset.Name,-26} {label,-6} a={Describe(family, "a", FontWeight.Normal)}  中={Describe(family, "中", FontWeight.Normal)}  bold中={Describe(family, "中", FontWeight.SemiBold)}  'ab中'={Describe(family, "ab中", FontWeight.SemiBold)}");
            }
        }
    }

    public static string Describe(FontFamily family, string text, FontWeight weight)
    {
        var layout = new TextLayout(text, new Typeface(family, FontStyle.Normal, weight), 14, Brushes.Black);
        var parts = new List<string>();
        foreach (var line in layout.TextLines)
        {
            foreach (var run in line.TextRuns)
            {
                if (run is ShapedTextRun shaped)
                {
                    var face = shaped.GlyphRun.GlyphTypeface;
                    parts.Add($"{face.FamilyName}/{(int)face.Weight}");
                }
            }
        }

        return string.Join("+", parts);
    }
}
