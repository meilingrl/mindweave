using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;

namespace TextSpacing;

/// <summary>
/// One adjustable setting. Option 0 is always what the app does today ("now"); the value is applied to the answer's
/// text blocks or to the space between them.
/// </summary>
internal sealed record Knob(string Label, string[] Options);

internal static class Knobs
{
    public static readonly Knob LineHeight = new("Line height", ["Now (font default)", "1.5×", "1.6×", "1.7×", "1.8×"]);
    public static readonly Knob Gap = new("Paragraph gap", ["8 (now)", "10", "12", "14", "16"]);
    public static readonly Knob Size = new("Size", ["14 (now)", "14.5", "15"]);
    public static readonly Knob Colour = new("Text colour", ["Now (pure black/white)", "App primary", "Softer", "Softest"]);
    public static readonly Knob Letter = new("Letter spacing", ["0 (now)", "0.2", "0.4"]);
    public static readonly Knob Width = new("Column", ["Fill (now)", "640", "720"]);

    public static readonly Knob BoldWeight = new("Bold weight", ["Semibold (now)", "Medium"]);
    public static readonly Knob BoldColour = new("Bold colour", ["Same as text (now)", "Softer light / brighter dark", "Warm"]);
    public static readonly Knob BoldMark = new("Bold background", ["None (now)", "Subtle"]);

    public static readonly Knob[] All = [LineHeight, Gap, Size, Colour, Letter, Width, BoldWeight, BoldColour, BoldMark];

    /// <summary>Bold text colour per option, light and dark; null keeps the paragraph's colour.</summary>
    public static Color? BoldColourOf(int i, bool dark) => (i, dark) switch
    {
        (1, false) => Color.Parse("#3B3632"),
        (2, false) => Color.Parse("#6B3A12"),
        (1, true) => Color.Parse("#FFFFFF"),
        (2, true) => Color.Parse("#F2D7A6"),
        _ => null,
    };

    public static Color? BoldMarkOf(int i, bool dark) => i == 1 ? Color.Parse(dark ? "#2C2F38" : "#F1EEEA") : null;

    public static double? LineFactor(int i) => i switch { 1 => 1.5, 2 => 1.6, 3 => 1.7, 4 => 1.8, _ => null };
    public static double GapOf(int i) => i switch { 1 => 10, 2 => 12, 3 => 14, 4 => 16, _ => 8 };
    public static double SizeOf(int i) => i switch { 1 => 14.5, 2 => 15, _ => 14 };
    public static double LetterOf(int i) => i switch { 1 => 0.2, 2 => 0.4, _ => 0 };
    public static double WidthOf(int i) => i switch { 1 => 640, 2 => 720, _ => double.PositiveInfinity };

    /// <summary>Text colour per option, light and dark; null keeps what answers get today: the Fluent window colour, pure black or white
    /// (answers set no colour, and LiveMarkdown's ForegroundColor key does not reach paragraphs).</summary>
    public static Color? ColourOf(int i, bool dark) => (i, dark) switch
    {
        (1, false) => Color.Parse("#1C1917"),
        (2, false) => Color.Parse("#2E2A27"),
        (3, false) => Color.Parse("#44403C"),
        (1, true) => Color.Parse("#E6E8EE"),
        (2, true) => Color.Parse("#D8DBE2"),
        (3, true) => Color.Parse("#C4C8D1"),
        _ => null,
    };
}

internal sealed class App : Application
{
    public static string[] Args = [];

    public override void Initialize()
    {
        // As App.axaml: LiveMarkdown's defaults and styles after the Fluent theme.
        var baseUri = new Uri("avares://TextSpacing/");
        Resources.MergedDictionaries.Add(new ResourceInclude(baseUri) { Source = new Uri("avares://LiveMarkdown.Avalonia/Defaults.axaml") });
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(baseUri) { Source = new Uri("avares://LiveMarkdown.Avalonia/Styles.axaml") });

        // LiveMarkdown's own keys, as Themes/Colors.axaml sets them (its defaults assume a dark surface).
        Resources.ThemeDictionaries[ThemeVariant.Light] = Keys("#1C1917", "#E7E5E4", "#FAFAF9", "#F3F2F0", "#9D174D", "#D6D3D1");
        Resources.ThemeDictionaries[ThemeVariant.Dark] = Keys("#E6E8EE", "#2C2E33", "#1D1E22", "#25272C", "#F0A6C8", "#3A3D45");
    }

    private static ResourceDictionary Keys(string foreground, string border, string card, string secondaryCard, string codeInline, string quoteBorder) => new()
    {
        ["ForegroundColor"] = Color.Parse(foreground),
        ["BorderColor"] = Color.Parse(border),
        ["CardBackgroundColor"] = Color.Parse(card),
        ["SecondaryCardBackgroundColor"] = Color.Parse(secondaryCard),
        ["CodeInlineColor"] = Color.Parse(codeInline),
        ["QuoteBorderColor"] = Color.Parse(quoteBorder),
    };

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
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
    }
}

internal sealed class MainWindow : Window
{
    private bool dark;
    private int paneCount = 2;
    // Every pane starts from the spacing chosen on 2026-11-21 (1.7×, 10 px gap), now in the app.
    private readonly List<int[]> settings = [[3, 1, 0, 0, 0, 0, 0, 0, 0], [3, 1, 0, 0, 0, 0, 1, 0, 0], [3, 1, 0, 0, 0, 0, 1, 1, 0]];
    private readonly Grid panes = new();

    public MainWindow(string[] args)
    {
        Title = "Mindweave text spacing";
        Width = 1560;
        Height = 1000;

        // --shot <file.png> [--dark] [--three] [--pane1 0,0,0,0,0,0] [--pane2 …] [--pane3 …]: option indexes in knob order.
        string? shot = Option(args, "--shot");
        dark = args.Contains("--dark");
        if (args.Contains("--three")) paneCount = 3;
        for (int i = 0; i < 3; i++)
        {
            if (Option(args, $"--pane{i + 1}") is { } list)
            {
                var values = list.Split(',').Select(int.Parse).ToArray();
                settings[i] = [.. values, .. new int[Math.Max(0, Knobs.All.Length - values.Length)]];
            }
        }

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Margin = new Thickness(14, 8) };
        bar.Children.Add(Choice("Panes", ["2", "3"], paneCount == 3 ? 1 : 0, i => paneCount = i + 2));
        bar.Children.Add(Choice("Theme", ["Light", "Dark"], dark ? 1 : 0, i => dark = i == 1));
        bar.Children.Add(new TextBlock
        {
            Text = "Fonts are the app's own: Inter for the interface, PingFang SC first for answers. Option “now” is today's value.",
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
        });

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(panes);
        Content = root;
        Rebuild();

        if (shot != null)
        {
            Opened += (_, _) => DispatcherTimer.RunOnce(() => { Save(shot); Close(); }, TimeSpan.FromMilliseconds(900));
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

    private Control Choice(string label, string[] options, int selected, Action<int> apply)
    {
        var box = new ComboBox { ItemsSource = options, SelectedIndex = selected };
        box.SelectionChanged += (_, _) =>
        {
            apply(box.SelectedIndex);
            Rebuild();
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, box },
        };
    }

    private void Rebuild()
    {
        RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Background = new SolidColorBrush(Color.Parse(dark ? "#000000" : "#D6D3D1"));
        panes.Children.Clear();
        panes.ColumnDefinitions = new(string.Join(",", Enumerable.Repeat("*", paneCount)));
        for (int i = 0; i < paneCount; i++)
        {
            var pane = new PaneView(dark, settings[i], Rebuild) { Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0) };
            Grid.SetColumn(pane, i);
            panes.Children.Add(pane);
        }
    }
}

/// <summary>One conversation page: a question bubble and a real LiveMarkdown answer, with its own knobs on top.</summary>
internal sealed class PaneView : DockPanel
{
    // The app's answer family (App.axaml, AnswerFontFamily), applied as in Styles/Conversation.axaml.
    private const string AnswerFamily =
        "PingFang SC, Microsoft YaHei UI, Microsoft YaHei, Noto Sans CJK SC, Noto Sans SC, Source Han Sans SC, $Default";

    // Shown only with --dump, to list how headings, code and tables are built.
    private const string DumpExtras = """
        # 一级标题，一级标题会不会因为行高太小而折行重叠呢，再长一点

        ```csharp
        var x = 1;
        var y = 2;
        ```

        | 列 A | 列 B |
        | --- | --- |
        | 表格内容 | 2,480 |

        """;

    private const string Sample = """
        火车的诞生不是一次发明，而是**蒸汽动力**与**轨道运输**两条技术线索在十九世纪初的汇合。真正让铁路走向实用的是乔治·斯蒂芬森，1829 年他和儿子设计的“**火箭号**”在雨山试验中胜出。此后几十年间的关键改进包括：

        1. **多管锅炉**：增加受热面积，使蒸汽产生得更快；
        2. **鼓风管**：用排出的废汽抽风，让炉火更旺；
        3. **熟铁轨道**：取代易碎的铸铁轨，能承受更重的机车；
        4. **标准轨距**：1435 毫米的轨距逐渐成为通行标准。

        ## 结论

        小字号下，**真正影响清晰度的是字重和渲染方式**，而不只是字体本身。行距稍大一些，笔画之间就有了呼吸的空间，整段文字看起来会更轻。Inter was designed for screens, so its Latin letters stay open even at small sizes.

        1. 正文保持 14 px，行高约为字号的 1.6 到 1.7 倍。
        2. 段落之间的距离应明显大于行距，读者才能一眼分清段落。
        3. 数字与单位：1,204 tokens、8.2 秒、¥2,480.00、2026-11-21。

        ### 为什么看起来发粗

        屏幕上的文字是否清爽，取决于笔画的粗细、字面的大小和留白，以及渲染时边缘如何处理。同一款字体，在系统自带的应用里和在自绘界面的程序里，看起来可能并不一样：前者常用系统的文字渲染，后者用自己的绘制引擎，边缘的灰度会略多一些，于是笔画显得更粗、更软。

        - 中文字形结构复杂，小字号时这种差别尤其明显。
        - 混排时还要注意中西文的协调，例如 `TextRenderingMode` 这样的代码片段。

        > 引用：排版的目的，是让读者忘记排版的存在。

        最后一段，用来观察一段较长文字在当前设置下的整体灰度：如果整页看上去发黑、发闷，通常是行距太紧或颜色太深；如果显得松散、发灰，则可能是行距过大或颜色过浅。
        """;

    private readonly int[] knobs;
    private readonly bool dark;
    private readonly TextBlock measured = new() { FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

    public PaneView(bool dark, int[] knobs, Action changed)
    {
        this.dark = dark;
        this.knobs = knobs;
        Background = new SolidColorBrush(Color.Parse(dark ? "#18191B" : "#FFFFFF"));

        var header = new WrapPanel { Margin = new Thickness(10, 6, 10, 2) };
        for (int k = 0; k < Knobs.All.Length; k++)
        {
            int index = k;
            var box = new ComboBox { ItemsSource = Knobs.All[k].Options, SelectedIndex = knobs[k], FontSize = 12 };
            box.SelectionChanged += (_, _) =>
            {
                knobs[index] = box.SelectedIndex;
                changed();
            };
            header.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(0, 2, 12, 2),
                Children = { new TextBlock { Text = Knobs.All[k].Label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, box },
            });
        }

        var top = new StackPanel
        {
            Background = new SolidColorBrush(Color.Parse(dark ? "#1E1F23" : "#F7F7F6")),
            Children = { header, new Border { Padding = new Thickness(12, 0, 12, 6), Child = measured } },
        };
        SetDock(top, Dock.Top);
        Children.Add(top);

        Styles.AddRange(AnswerStyles());
        Children.Add(new ScrollViewer { Content = Page() });
        LayoutUpdated += (_, _) => Measure();
        if (App.Args.Contains("--dump"))
        {
            AttachedToVisualTree += (_, _) => DispatcherTimer.RunOnce(Dump, TimeSpan.FromMilliseconds(500));
        }
    }

    /// <summary>Lists every text block of the answer: its classes, size, weight and the chain of its parents.</summary>
    private void Dump()
    {
        foreach (var block in this.GetVisualDescendants().OfType<TextBlock>())
        {
            var parents = string.Join(" < ", block.GetVisualAncestors().Take(4).Select(v => v.GetType().Name + (v is StyledElement e && e.Classes.Count > 0 ? "." + string.Join(".", e.Classes) : "")));
            string text = (block is MarkdownTextBlock m ? m.ActualText : block.Text) ?? "";
            Console.WriteLine($"{block.GetType().Name}.{string.Join(".", block.Classes)} size={block.FontSize} weight={block.FontWeight} lh={block.LineHeight} family={block.FontFamily.Name[..Math.Min(12, block.FontFamily.Name.Length)]} '{text[..Math.Min(14, text.Length)]}'  < {parents}");
            if (block.Inlines is { } inlines)
            {
                DumpInlines(inlines, "    ");
            }
        }
    }

    private static void DumpInlines(Avalonia.Controls.Documents.InlineCollection inlines, string indent)
    {
        foreach (var inline in inlines)
        {
            string text = inline is Avalonia.Controls.Documents.Run run ? run.Text ?? "" : "";
            string colour = inline.Foreground is ISolidColorBrush b ? b.Color.ToString() : inline.Foreground?.ToString() ?? "-";
            Console.WriteLine($"{indent}{inline.GetType().FullName} classes=[{string.Join(",", inline.Classes)}] weight={inline.FontWeight} local={inline.IsSet(Avalonia.Controls.Documents.TextElement.FontWeightProperty)} fg={colour} '{text[..Math.Min(10, text.Length)]}'");
            if (inline is Avalonia.Controls.Documents.Span span)
            {
                DumpInlines(span.Inlines, indent + "  ");
            }
        }
    }

    private IEnumerable<IStyle> AnswerStyles()
    {
        double size = Knobs.SizeOf(knobs[2]);
        var text = new Style(x => x.OfType<MarkdownTextBlock>());
        text.Setters.Add(new Setter(TextBlock.FontFamilyProperty, FontFamily.Parse(AnswerFamily)));
        text.Setters.Add(new Setter(TextBlock.FontSizeProperty, size));
        text.Setters.Add(new Setter(TextBlock.LetterSpacingProperty, Knobs.LetterOf(knobs[4])));
        if (Knobs.LineFactor(knobs[0]) is { } factor)
        {
            text.Setters.Add(new Setter(TextBlock.LineHeightProperty, Math.Round(size * factor)));
        }

        if (Knobs.ColourOf(knobs[3], dark) is { } colour)
        {
            text.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(colour)));
        }

        yield return text;

        // List numbers and bullets follow the answer family and size, as in the app.
        var markers = new Style(x => Selectors.Or(
            x.OfType<TextBlock>().Class("ListBlockNumber"),
            x.OfType<TextBlock>().Class("ListBlockBullet")));
        markers.Setters.Add(new Setter(TextBlock.FontFamilyProperty, FontFamily.Parse(AnswerFamily)));
        markers.Setters.Add(new Setter(TextBlock.FontSizeProperty, size));
        if (Knobs.LineFactor(knobs[0]) is { } markerFactor)
        {
            markers.Setters.Add(new Setter(TextBlock.LineHeightProperty, Math.Round(size * markerFactor)));
        }

        if (Knobs.ColourOf(knobs[3], dark) is { } markerColour)
        {
            markers.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(markerColour)));
        }

        yield return markers;

        // Styles/Conversation.axaml: quotes sit on the sidebar colour.
        var quote = new Style(x => x.OfType<Border>().Class("QuoteBlock"));
        quote.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.Parse(dark ? "#1D1E22" : "#F5F5F4"))));
        yield return quote;

        // Bold is a Span with the classes Emphasis and Bold. Its weight is set on the element itself, so a style cannot
        // change it (see ApplyBoldWeight); its colour and background can be styled.
        var bold = new Style(x => x.OfType<Avalonia.Controls.Documents.Span>().Class("Bold"));
        if (Knobs.BoldColourOf(knobs[7], dark) is { } boldColour)
        {
            bold.Setters.Add(new Setter(Avalonia.Controls.Documents.TextElement.ForegroundProperty, new SolidColorBrush(boldColour)));
        }

        if (Knobs.BoldMarkOf(knobs[8], dark) is { } mark)
        {
            bold.Setters.Add(new Setter(Avalonia.Controls.Documents.TextElement.BackgroundProperty, new SolidColorBrush(mark)));
        }

        yield return bold;

        var blocks = new Style(x => x.OfType<StackPanel>().Class("MarkdownDocument"));
        blocks.Setters.Add(new Setter(StackPanel.SpacingProperty, Knobs.GapOf(knobs[1])));
        yield return blocks;
    }

    private Control Page()
    {
        var renderer = new MarkdownRenderer();
        string markdown = App.Args.Contains("--dump") ? DumpExtras + Sample : Sample;
        renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdig.Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline));

        var question = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 560,
            Background = new SolidColorBrush(Color.Parse(dark ? "#26282E" : "#F0EFED")),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 9),
            Child = new SelectableTextBlock
            {
                Text = "行距稍微大一点会不会更好读？给 3 条建议。",
                Foreground = new SolidColorBrush(Color.Parse(dark ? "#E6E8EE" : "#1C1917")),
                TextWrapping = TextWrapping.Wrap,
            },
        };

        return new StackPanel
        {
            Margin = new Thickness(28, 18, 28, 24),
            Spacing = 12,
            MaxWidth = Knobs.WidthOf(knobs[5]),
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { question, renderer },
        };
    }

    /// <summary>Sets the weight of every bold span; in the app this would run after each render.</summary>
    private void ApplyBoldWeight()
    {
        var weight = knobs[6] == 1 ? FontWeight.Medium : FontWeight.Bold;
        foreach (var block in this.GetVisualDescendants().OfType<MarkdownTextBlock>())
        {
            if (block.Inlines is { } inlines)
            {
                SetBold(inlines, weight);
            }
        }
    }

    private static void SetBold(Avalonia.Controls.Documents.InlineCollection inlines, FontWeight weight)
    {
        foreach (var span in inlines.OfType<Avalonia.Controls.Documents.Span>())
        {
            if (span.Classes.Contains("Bold") && span.FontWeight != weight)
            {
                span.FontWeight = weight;
            }

            SetBold(span.Inlines, weight);
        }
    }

    /// <summary>Shows what the first paragraph really got: size, line pitch, gap and colour.</summary>
    private void Measure()
    {
        ApplyBoldWeight();
        var paragraph = this.GetVisualDescendants().OfType<MarkdownTextBlock>()
            .FirstOrDefault(b => b.Classes.Contains("ParagraphBlock") && b.TextLayout.TextLines.Count > 1);
        if (paragraph == null)
        {
            return;
        }

        var lines = paragraph.TextLayout.TextLines;
        string pitch = $"{lines[0].Height:0.#}";
        string colour = paragraph.Foreground is ISolidColorBrush brush ? brush.Color.ToString() : "?";
        string text = $"Measured: size {paragraph.FontSize:0.#} · line pitch {pitch} px · paragraph gap {Knobs.GapOf(knobs[1])} px · colour {colour}";
        if (measured.Text != text)
        {
            measured.Text = text;
        }
    }
}
