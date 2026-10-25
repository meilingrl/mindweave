using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Markdig;

namespace LiveMarkdownSpike;

public sealed record SavedSample(int Version, string Markdown, string LiveMarkdown, string SelectedMessage, SelectionAnchor Anchor);

public sealed class SpikeWindow : Window
{
    private readonly MarkdownRenderer _source = new();
    private readonly MarkdownRenderer _live = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _status = new() { Text = "拖选正文文字，再保存或恢复来源。", FontSize = 12, Foreground = Brush.Parse("#7D8475"), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { FontSize = 11, Foreground = Brush.Parse("#999E91"), TextWrapping = TextWrapping.Wrap, MaxHeight = 58 };
    private readonly StackPanel _livePanel;
    private readonly string _dataFile;
    private string _markdown = Samples.Repeat;
    private ObservableStringBuilder _liveBuilder = new();
    private MarkdownRenderer _active;
    private SavedSample? _saved;
    private CancellationTokenSource? _streaming;
    private bool _loadFailed;
    private bool _restoring;
    private sealed record ReadingPin(MarkdownTextBlock Block, int Character, double Y);
    private ReadingPin? _reading;

    public SpikeWindow(string? dataFile = null)
    {
        _active = _source;
        _dataFile = dataFile ?? Environment.GetEnvironmentVariable("MINDWEAVE_LIVEMARKDOWN_DATA_FILE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "MindweaveLiveMarkdownSpike", "anchor.json");
        Title = "Mindweave · LiveMarkdown 小样"; Width = 1160; Height = 820; MinWidth = 790; MinHeight = 620;
        Background = Brush.Parse("#FAFAF8"); FontFamily = new FontFamily("Inter, PingFang SC, Microsoft YaHei, sans-serif");
        var shell = new Grid { ColumnDefinitions = new ColumnDefinitions("216,*") };
        var side = new StackPanel { Margin = new Thickness(20, 32), Spacing = 9 };
        side.Children.Add(new TextBlock { Text = "Mindweave", FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
        side.Children.Add(new TextBlock { Text = "LiveMarkdown 2.5.1", FontSize = 11, Foreground = Brush.Parse("#969C8D"), Margin = new Thickness(0, 0, 0, 26) });
        side.Children.Add(ActionButton("同段重复词", () => Choose(Samples.Repeat)));
        side.Children.Add(ActionButton("跨格式 · 跨两段", () => Choose(Samples.Rich)));
        side.Children.Add(ActionButton("长文阅读", () => Choose(Samples.Long)));
        side.Children.Add(new TextBlock { Text = "技术小样\n\n选区保留区块来源、局部显示范围与正文校验。\n\n不连接 AI，不读取旧聊天原型的数据。", FontSize = 11, LineHeight = 20, Foreground = Brush.Parse("#92998A"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 32, 0, 0) });
        shell.Children.Add(new Border { Background = Brush.Parse("#F0F1EC"), BorderBrush = Brush.Parse("#E2E5DB"), BorderThickness = new Thickness(0, 0, 1, 0), Child = side });
        var main = new Grid { RowDefinitions = new RowDefinitions("66,*,Auto") };
        Grid.SetColumn(main, 1); shell.Children.Add(main);
        main.Children.Add(new TextBlock { Text = "原生 Markdown 选区与来源验证", FontSize = 13, Foreground = Brush.Parse("#78806E"), Margin = new Thickness(32, 26, 0, 0) });
        _livePanel = new StackPanel { IsVisible = false, Margin = new Thickness(0, 30, 0, 0), Children = { new TextBlock { Text = "模拟回复 · 独立消息", FontSize = 11, Foreground = Brush.Parse("#969C8B"), Margin = new Thickness(0, 0, 0, 16) }, _live } };
        var content = new StackPanel { MaxWidth = 736, Margin = new Thickness(38, 18, 38, 32), Children = { _source, _livePanel } };
        _scroll = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(_scroll, 1); main.Children.Add(_scroll);
        var footer = new StackPanel { Spacing = 10, Margin = new Thickness(32, 13, 32, 18) };
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(ActionButton("保存选区", () => _ = SaveAsync()));
        actions.Children.Add(ActionButton("恢复来源", RestoreSaved));
        actions.Children.Add(ActionButton("修改正文试拒绝", ChangeSource));
        actions.Children.Add(ActionButton("追加流式文本", () => _ = StreamAsync()));
        actions.Children.Add(ActionButton("停止", () => _streaming?.Cancel()));
        footer.Children.Add(actions); footer.Children.Add(_status); footer.Children.Add(_detail);
        Grid.SetRow(footer, 2); main.Children.Add(footer); Content = shell;
        foreach (var renderer in new[] { _source, _live })
        {
            renderer.LinkClick += (_, e) => { e.Handled = true; _status.Text = "这是测试链接；小样没有打开外部页面。"; };
            renderer.AddHandler(PointerReleasedEvent, (_, _) => { _active = renderer; ShowCurrentSelection(); }, RoutingStrategies.Bubble, true);
        }
        _scroll.ScrollChanged += (_, _) => { if (!_restoring) _reading = CaptureReading(); };
        SizeChanged += (_, _) =>
        {
            if (_restoring || !IsVisible) return;
            var pin = _reading; _restoring = true;
            Dispatcher.UIThread.Post(() => { RestoreReading(pin); _restoring = false; }, DispatcherPriority.Loaded);
        };
        Load(); ApplySource();
        Opened += (_, _) => { if (_saved != null) Dispatcher.UIThread.Post(RestoreSaved, DispatcherPriority.Loaded); };
        Closed += (_, _) => _streaming?.Cancel();
    }

    private static Button ActionButton(string title, Action action)
    {
        var button = new Button { Content = title, Focusable = false, Background = Brushes.Transparent, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8), Padding = new Thickness(11, 8), Margin = new Thickness(0, 0, 5, 0), FontSize = 12 };
        button.Click += (_, _) => action(); return button;
    }
    private void ApplySource() => _source.DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdown.Parse(_markdown, MarkdownUpdateProducer.DefaultPipeline));
    private void Choose(string source)
    {
        _streaming?.Cancel(); _markdown = source; _active = _source; ApplySource();
        _liveBuilder = new(); _live.MarkdownBuilder = _liveBuilder; _livePanel.IsVisible = false;
        _scroll.Offset = default; _reading = null; _status.Text = "拖选这一份样例的文字，然后保存。"; _detail.Text = "";
    }
    private void ShowCurrentSelection()
    {
        try { Describe(SelectionAnchors.Capture(ActiveMarkdown(), _active)); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { }
    }
    private string ActiveMarkdown() => _active == _source ? _markdown : _liveBuilder.ToString();
    private void Describe(SelectionAnchor anchor)
    {
        _status.Text = "选中：" + string.Join(" / ", anchor.Segments.Select(x => x.Quote));
        _detail.Text = string.Join(" · ", anchor.Segments.Select(x => $"原文块[{x.SourceStart},{x.SourceEnd}] / 显示[{x.Start},{x.Start + x.Length})" + (x.ExactRawStart is { } raw ? $" / 精确原文起点{raw}" : " / 带格式显示锚点")));
    }
    private async Task SaveAsync()
    {
        try
        {
            if (_loadFailed) throw new InvalidDataException("小样文件无法读取，本次不覆盖它；请先手动改名保留。" );
            if (_active == _live && _live.DocumentUpdate?.Version != _liveBuilder.Version) throw new InvalidOperationException("排版仍在更新，请稍后保存。");
            var anchor = SelectionAnchors.Capture(ActiveMarkdown(), _active);
            var saved = new SavedSample(1, _markdown, _liveBuilder.ToString(), _active == _source ? "source" : "live", anchor);
            var json = JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(_dataFile)!);
            var temporary = _dataFile + ".tmp";
            await File.WriteAllTextAsync(temporary, json); File.Move(temporary, _dataFile, true);
            _saved = saved; Describe(anchor); _status.Text = "已保存选区，可关闭重开后恢复。"; _detail.Text += "\n" + _dataFile;
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException) { _status.Text = e.Message; }
    }
    private void Load()
    {
        if (!File.Exists(_dataFile)) return;
        try
        {
            _saved = JsonSerializer.Deserialize<SavedSample>(File.ReadAllText(_dataFile)) ?? throw new InvalidDataException();
            if (_saved.Version != 1 || _saved.Markdown == null || _saved.LiveMarkdown == null || _saved.Anchor?.Segments == null) throw new InvalidDataException();
            _markdown = _saved.Markdown; _liveBuilder = new(_saved.LiveMarkdown);
            _live.DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdown.Parse(_saved.LiveMarkdown, MarkdownUpdateProducer.DefaultPipeline));
            _livePanel.IsVisible = !string.IsNullOrEmpty(_saved.LiveMarkdown);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or ArgumentException) { _loadFailed = true; _saved = null; _status.Text = "无法读取小样存档，原文件保留，本次临时打开。"; }
    }
    private void RestoreSaved()
    {
        try
        {
            if (_saved == null) throw new InvalidOperationException("还没有保存选区。");
            _active = _saved.SelectedMessage == "live" ? _live : _source;
            var resolved = SelectionAnchors.Restore(_saved.Anchor, ActiveMarkdown(), _active);
            _restoring = true; SelectionAnchors.Reveal(resolved[0], _scroll); _restoring = false;
            _reading = CaptureReading(); Describe(_saved.Anchor); _status.Text = "已恢复原来的选区与来源高亮。";
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException) { _restoring = false; _status.Text = "拒绝恢复：" + e.Message; }
    }
    private void ChangeSource()
    {
        _markdown = "（正文已插入新内容）\n\n" + _markdown;
        ApplySource(); _active = _source; _status.Text = "正文已修改；现在点击恢复来源，应明确拒绝。";
    }
    private ReadingPin? CaptureReading()
    {
        foreach (var renderer in new[] { _source, _live })
            foreach (var block in SelectionAnchors.Blocks(renderer))
                if (block.TranslatePoint(default, _scroll) is { } origin && origin.Y <= 24 && origin.Y + block.Bounds.Height > 24)
                {
                    var hit = block.TextLayout.HitTestPoint(new Point(0, Math.Max(0, 24 - origin.Y)));
                    var bounds = block.GetTextRangeBoundsInControl(hit.TextPosition, 1);
                    if (bounds.Count > 0) return new(block, hit.TextPosition, origin.Y + bounds[0].Y);
                }
        return null;
    }
    private void RestoreReading(ReadingPin? pin)
    {
        UpdateLayout();
        if (pin == null || !pin.Block.IsAttachedToVisualTree()) return;
        var bounds = pin.Block.GetTextRangeBoundsInControl(pin.Character, 1);
        if (bounds.Count > 0 && pin.Block.TranslatePoint(bounds[0].TopLeft, _scroll) is { } point)
            _scroll.Offset = new Vector(0, Math.Max(0, _scroll.Offset.Y + point.Y - pin.Y));
    }
    private async Task StreamAsync()
    {
        if (_streaming != null) return;
        var cancellation = new CancellationTokenSource(); _streaming = cancellation;
        _liveBuilder = new(); _live.MarkdownBuilder = _liveBuilder; _livePanel.IsVisible = true;
        _status.Text = "正在下方追加固定回复；旧回答的选区与阅读位置应保持。";
        try
        {
            for (var start = 0; start < Samples.Stream.Length; start += 6)
            {
                await Task.Delay(170, cancellation.Token);
                var pin = CaptureReading(); _liveBuilder.Append(Samples.Stream.Substring(start, Math.Min(6, Samples.Stream.Length - start)));
                Dispatcher.UIThread.Post(() => RestoreReading(pin), DispatcherPriority.Loaded);
            }
            _status.Text = "固定回复追加完成。没有连接 AI。";
        }
        catch (OperationCanceledException) { _status.Text = "已停止追加，已收到文字保留。"; }
        finally { _streaming = null; cancellation.Dispose(); }
    }
}
