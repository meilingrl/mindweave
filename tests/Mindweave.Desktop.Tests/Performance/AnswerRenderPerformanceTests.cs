using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;

namespace Mindweave.Desktop.Tests.Performance;

/// <summary>
/// Rendering budgets for very long answers (docs/design/s2-branching.md “性能”). Real Skia text layout in the
/// headless window. The full-size run is opt-in (MINDWEAVE_PERF=1, see scripts/perf.sh) because it takes tens of
/// seconds; the smoke run keeps the generator and every measured path working on each pass and asserts no times.
/// </summary>
public class AnswerRenderPerformanceTests
{
    /// <summary>Showing a finished answer: parse, build the controls and lay them out once (the answer view alone).</summary>
    private static readonly Dictionary<int, TimeSpan> ShowBudget = new() { [30_000] = TimeSpan.FromMilliseconds(400), [100_000] = TimeSpan.FromMilliseconds(1_000) };

    /// <summary>Opening the conversation from the sidebar until it is placed at its reading position.</summary>
    private static readonly Dictionary<int, TimeSpan> OpenBudget = new() { [30_000] = TimeSpan.FromMilliseconds(500), [100_000] = TimeSpan.FromMilliseconds(1_500) };

    /// <summary>UI-thread work per streamed update (apply the new parse and lay out): within one frame at 60 Hz.</summary>
    private static readonly TimeSpan ChunkUiBudget = TimeSpan.FromMilliseconds(16);

    /// <summary>The background parse per streamed update; above the 50 ms update interval, updates merge rather than queue.</summary>
    private static readonly TimeSpan ChunkParseBudget = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// UI-thread work per streamed chunk in the whole app (view model, page, scrolling included): at most half of the
    /// 50 ms interval at which a running answer's text is refreshed, so the window stays responsive while it streams.
    /// </summary>
    private static readonly TimeSpan AppChunkBudget = TimeSpan.FromMilliseconds(25);

    private const int ChunkCharacters = 40;

    private static bool Full => Environment.GetEnvironmentVariable("MINDWEAVE_PERF") == "1";

    private static ITestOutputHelper? Output => TestContext.Current.TestOutputHelper;

    [AvaloniaFact]
    public async Task Smoke()
    {
        Show(3_000, runs: 1, enforce: false);
        Stream([1_000, 3_000], appends: 2, enforce: false);
        await StreamInAppAsync([3_000], appends: 2, enforce: false);
        await OpenAsync(3_000, runs: 1, enforce: false);
    }

    [AvaloniaFact]
    [Trait("Category", "Performance")]
    public void Showing_a_long_finished_answer_stays_within_budget()
    {
        Assert.SkipUnless(Full, "Run through scripts/perf.sh.");
        Show(3_000, runs: 1, enforce: false); // warm-up: JIT, fonts, styles
        Show(30_000, runs: 5, enforce: true);
        Show(100_000, runs: 5, enforce: true);
    }

    [AvaloniaFact]
    [Trait("Category", "Performance")]
    public async Task Opening_a_conversation_with_a_long_answer_stays_within_budget()
    {
        Assert.SkipUnless(Full, "Run through scripts/perf.sh.");
        await OpenAsync(30_000, runs: 5, enforce: true);
        await OpenAsync(100_000, runs: 5, enforce: true);
    }

    [AvaloniaFact]
    [Trait("Category", "Performance")]
    public async Task Streaming_into_a_long_answer_keeps_each_update_within_budget()
    {
        Assert.SkipUnless(Full, "Run through scripts/perf.sh.");
        Stream([1_000], appends: 3, enforce: false); // warm-up
        Stream([10_000, 30_000, 60_000, 100_000], appends: 9, enforce: true);
        await StreamInAppAsync([30_000, 100_000], appends: 9, enforce: true);
    }

    /// <summary>A finished answer in the answer view alone: parse and build (setting it), then the first layout and frame.</summary>
    private static void Show(int characters, int runs, bool enforce)
    {
        var markdown = LongMarkdown.Generate(characters);
        var message = new Message(MessageId.New(), ConversationId.New(), 1, MessageRole.Assistant, ContentBlocks.FromText(markdown), MessageState.Complete, 1, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var build = new List<TimeSpan>();
        var layout = new List<TimeSpan>();
        var frame = new List<TimeSpan>();
        var blocks = 0;
        for (var run = 0; run < runs; run++)
        {
            var answer = new AnswerView();
            var window = new Window { Width = 1000, Height = 700, Content = new ScrollViewer { Content = answer } };
            window.Show();
            window.UpdateLayout();
            Settle();

            var watch = Stopwatch.StartNew();
            answer.DataContext = new MessageItemViewModel(message, null, new AvaloniaUiDispatcher());
            build.Add(watch.Elapsed);

            watch.Restart();
            window.UpdateLayout();
            layout.Add(watch.Elapsed);

            watch.Restart();
            window.CaptureRenderedFrame()?.Dispose();
            frame.Add(watch.Elapsed);

            blocks = SourceAnchors.Blocks(answer.Renderer).Count;
            Assert.True(answer.Renderer.Bounds.Height > 1000, "The answer was not laid out in full.");
            window.Close();
        }

        var total = Median(build.Zip(layout, (b, l) => b + l));
        Report($"show {Size(characters)} ({blocks} text blocks): parse+build {Ms(Median(build))}, first layout {Ms(Median(layout))}, first frame {Ms(Median(frame))}; parse+build+layout {Ms(total)}");
        if (enforce)
        {
            Assert.True(total <= ShowBudget[characters], $"Showing {Size(characters)} took {Ms(total)} (budget {Ms(ShowBudget[characters])}).");
        }
    }

    /// <summary>
    /// Streams into an answer as the live view does, measured per update at several lengths. Each update is the step
    /// the live builder takes for an append: parse the whole text (on a worker thread in the app), apply it as an
    /// incremental update (only blocks whose source changed are touched), then lay out. Driven synchronously here so
    /// each step is timed alone.
    /// </summary>
    private static void Stream(int[] lengths, int appends, bool enforce)
    {
        var markdown = LongMarkdown.Generate(lengths[^1] + (appends * ChunkCharacters) + 1);
        var renderer = new MarkdownRenderer();
        var window = new Window { Width = 1000, Height = 700, Content = new ScrollViewer { Content = renderer } };
        window.Show();
        var pipeline = MarkdownUpdateProducer.DefaultPipeline;
        MarkdownDocumentUpdate? previous = null;
        var length = 0;
        long version = 0;

        void Apply(int newLength, List<TimeSpan>? parse, List<TimeSpan>? ui)
        {
            var text = markdown[..newLength];
            var watch = Stopwatch.StartNew();
            var document = Markdig.Markdown.Parse(text, pipeline);
            parse?.Add(watch.Elapsed);
            version++;
            var change = new ObservableStringBuilderChangedEventArgs(length, newLength - length, newLength, version);
            watch.Restart();
            MarkdownDocumentUpdate update = previous is null ? new MarkdownDocumentUpdate.Full(document, version) : new MarkdownDocumentUpdate.Incremental(previous, document, in change);
            renderer.DocumentUpdate = update;
            window.UpdateLayout();
            ui?.Add(watch.Elapsed);
            previous = update;
            length = newLength;
        }

        var perLength = new List<(int Length, TimeSpan Parse, TimeSpan Ui)>();
        foreach (var target in lengths)
        {
            Apply(target, null, null);
            var parse = new List<TimeSpan>();
            var ui = new List<TimeSpan>();
            for (var i = 0; i < appends; i++)
            {
                Apply(length + ChunkCharacters, parse, ui);
            }

            perLength.Add((target, Median(parse), Median(ui)));
            Report($"stream at {Size(target)}: per {ChunkCharacters}-character update parse {Ms(Median(parse))} (worker thread), apply+layout {Ms(Median(ui))} (UI thread), max apply+layout {Ms(ui.Max())}");
        }

        Assert.Equal(version, renderer.DocumentUpdate?.Version);
        Assert.True(renderer.Bounds.Height > 1000, "The answer was not laid out in full.");
        window.Close();
        if (enforce)
        {
            foreach (var (target, parse, ui) in perLength)
            {
                Assert.True(ui <= ChunkUiBudget, $"An update at {Size(target)} kept the UI thread {Ms(ui)} (budget {Ms(ChunkUiBudget)}).");
                Assert.True(parse <= ChunkParseBudget, $"Parsing at {Size(target)} took {Ms(parse)} (budget {Ms(ChunkParseBudget)}).");
            }
        }
    }

    /// <summary>
    /// The same through the whole app: a running answer that already holds <c>length</c> characters gets one more
    /// chunk from the service; measured is all UI-thread work until the page shows it laid out (the live answer's
    /// throttled refresh, the view model, the answer view, LiveMarkdown's update, the page's layout and scrolling).
    /// The UI thread is pumped here and sleeps between pumps, so worker threads (the parse, storage) are not counted.
    /// </summary>
    private static async Task StreamInAppAsync(int[] lengths, int appends, bool enforce)
    {
        foreach (var target in lengths)
        {
            await using var app = DesktopHarness.Create();
            var window = new MainWindow { DataContext = app.Root, Width = 1000, Height = 700 };
            window.Show();
            await app.StartWithWorkspaceAsync("性能");
            await app.ConfigureModelAsync();
            var markdown = LongMarkdown.Generate(target + ((appends + 1) * ChunkCharacters));
            var held = app.Client.Hold(markdown[..target]);
            app.ShellPage.Conversations.NewCommand.Execute(null);
            var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
            draft.Composer.Text = "很长的回答";
            await draft.Composer.SendCommand.ExecuteAsync(null);
            var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
            var length = target;
            Shown(window, page, length, TimeSpan.FromSeconds(30));

            var busy = new List<TimeSpan>();
            for (var i = 0; i <= appends; i++)
            {
                held.Push(markdown[length..(length + ChunkCharacters)]);
                length += ChunkCharacters;
                var work = Shown(window, page, length, TimeSpan.FromSeconds(10));
                if (i > 0)
                {
                    busy.Add(work); // the first one warms up
                }
            }

            held.Finish();
            await UiWait.StoredAsync(page, 1, MessageState.Complete, timeoutMs: 30_000);
            window.Close();
            var median = Median(busy);
            Report($"stream in the app at {Size(target)}: UI-thread work per {ChunkCharacters}-character chunk {Ms(median)}, max {Ms(busy.Max())}");
            if (enforce)
            {
                Assert.True(median <= AppChunkBudget, $"A chunk at {Size(target)} kept the UI thread {Ms(median)} (budget {Ms(AppChunkBudget)}).");
            }
        }
    }

    /// <summary>
    /// Pumps the UI thread until the page shows <paramref name="length"/> characters laid out; returns the time spent
    /// in the pumps only (the check itself, a search of the visual tree, is not the app's work and is not counted).
    /// </summary>
    private static TimeSpan Shown(Window window, ConversationViewModel page, int length, TimeSpan timeout)
    {
        var busy = TimeSpan.Zero;
        var deadline = Stopwatch.StartNew();
        AnswerView? answer = null;
        while (true)
        {
            var watch = Stopwatch.StartNew();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            busy += watch.Elapsed;
            if (answer is null || !answer.IsAttachedToVisualTree() || answer.DataContext is not MessageItemViewModel { IsAnswer: true })
            {
                answer = page.Messages.Count == 2
                    ? window.GetVisualDescendants().OfType<AnswerView>().LastOrDefault(a => a.DataContext == page.Messages[1])
                    : null;
            }

            if (answer is { IsLayoutCurrent: true } && answer.Markdown.Length == length)
            {
                return busy;
            }

            Assert.True(deadline.Elapsed < timeout, $"The answer did not show {length} characters within {timeout.TotalSeconds} s.");
            Thread.Sleep(1);
        }
    }

    /// <summary>The whole page: a stored conversation opened from the sidebar until it is placed and laid out.</summary>
    private static async Task OpenAsync(int characters, int runs, bool enforce)
    {
        await using var app = DesktopHarness.Create();
        var window = new MainWindow { DataContext = app.Root, Width = 1000, Height = 700 };
        window.Show();
        await app.StartWithWorkspaceAsync("性能");
        await app.ConfigureModelAsync();

        var markdown = LongMarkdown.Generate(characters);
        var longOne = await AskAsync(app, "很长的回答", markdown);
        var other = await AskAsync(app, "短问题", "短回答。");
        await UiWait.UntilAsync(() => app.ShellPage.Conversations.Items.Count == 2);

        var samples = new List<TimeSpan>();
        for (var run = 0; run <= runs; run++)
        {
            await OpenFromSidebarAsync(app, window, other);
            Settle();
            var watch = Stopwatch.StartNew();
            var page = await OpenFromSidebarAsync(app, window, longOne);
            var elapsed = watch.Elapsed;
            Assert.Equal(markdown, page.Messages[1].Body);
            if (run > 0)
            {
                samples.Add(elapsed); // the first open warms up
            }
        }

        window.Close();
        var median = Median(samples);
        Report($"open {Size(characters)} from the sidebar: {Ms(median)} (median of {samples.Count})");
        if (enforce)
        {
            Assert.True(median <= OpenBudget[characters], $"Opening {Size(characters)} took {Ms(median)} (budget {Ms(OpenBudget[characters])}).");
        }
    }

    private static async Task<ConversationId> AskAsync(DesktopHarness app, string question, string answer)
    {
        app.Client.Reply(answer);
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = question;
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.StoredAsync(page, 1, MessageState.Complete, timeoutMs: 30_000);
        return page.Id;
    }

    private static async Task<ConversationViewModel> OpenFromSidebarAsync(DesktopHarness app, Window window, ConversationId id)
    {
        app.ShellPage.Conversations.OpenCommand.Execute(app.ShellPage.Conversations.Items.Single(i => i.Id == id));
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel p && p.Id == id, 30_000);
        var page = (ConversationViewModel)app.Workbench.CurrentPage!;
        await page.Loaded;
        var view = window.GetVisualDescendants().OfType<ConversationView>().Single();
        await UiWait.UntilAsync(() => view.DataContext == page && view.Restored.IsCompleted, 30_000);
        window.UpdateLayout();
        return page;
    }

    /// <summary>Collects garbage left by earlier runs, so a collection does not land in the next measurement.</summary>
    private static void Settle()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static TimeSpan Median(IEnumerable<TimeSpan> samples)
    {
        var ordered = samples.Order().ToArray();
        return ordered[ordered.Length / 2];
    }

    private static string Ms(TimeSpan value) => value.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) + " ms";

    private static string Size(int characters) => characters >= 1000 ? $"{characters / 1000}k" : characters.ToString(CultureInfo.InvariantCulture);

    private static void Report(string line) => Output?.WriteLine(line);
}

/// <summary>Deterministic long Chinese Markdown with the features answers use: headings, emphasis, lists, code, quotes, tables.</summary>
internal static class LongMarkdown
{
    public static string Generate(int characters)
    {
        var text = new StringBuilder(characters + 2_000);
        for (var section = 1; text.Length < characters; section++)
        {
            text.Append(CultureInfo.InvariantCulture, $"## 第 {section} 节：蒸汽机与铁路的发展\n\n");
            text.Append("瓦特改良的蒸汽机让**动力**摆脱了水力和畜力的限制，工厂不必再建在河边。随后几十年里，工程师们把这种动力装上车轮，");
            text.Append("先是在矿山的轨道上拖运煤炭，后来才用于载客。这一段文字只是为了让回答足够长，模拟模型写出的大段说明。\n\n");
            text.Append("早期机车的几个关键改进：\n\n");
            text.Append("- 多管锅炉：增加受热面积，使蒸汽产生得更快；\n- 鼓风管：用排出的废汽抽风，让炉火更旺；\n- 锻铁轨道：比铸铁轨道更耐用，不易断裂。\n\n");
            text.Append("1. 特里维西克的“便捷号”（1804）\n2. 斯蒂芬森的“火箭号”（1829）\n3. 利物浦—曼彻斯特铁路通车（1830）\n\n");
            text.Append("> 铁路不仅缩短了距离，也改变了人们对时间的理解：统一的铁路时刻表催生了标准时间。\n\n");
            text.Append("```python\ndef speed(distance_km, hours):\n    return distance_km / hours  # 火箭号约 46 km/h\n```\n\n");
            if (section % 3 == 0)
            {
                text.Append("| 年份 | 事件 |\n| --- | --- |\n| 1825 | 斯托克顿—达灵顿铁路通车 |\n| 1830 | 第一条城际客运铁路 |\n\n");
            }

            text.Append("铁路网的扩张带动了钢铁、煤炭和机械制造，也让报纸、邮件和商品以前所未有的速度流通，城市与乡村的联系变得紧密。\n\n");
        }

        return text.ToString(0, characters);
    }
}
