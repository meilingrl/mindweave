using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Input;
using LiveMarkdown.Avalonia;
using Markdig;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(LiveMarkdownSpike.Tests.TestAppBuilder))]

namespace LiveMarkdownSpike.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class AnchorTests
{
    [AvaloniaFact]
    public void A_contradictory_exact_raw_range_is_rejected_for_repeated_words()
    {
        const string markdown = "蒸汽机运行；蒸汽机继续运行。";
        var (window, renderer) = View(markdown);
        var block = Assert.Single(SelectionAnchors.Blocks(renderer));
        block.SelectionStart = 6; block.SelectionEnd = 9;
        var saved = SelectionAnchors.Capture(markdown, renderer);
        var invalid = saved with { Segments = [saved.Segments[0] with { ExactRawStart = 0 }] };
        Assert.Throws<InvalidDataException>(() => SelectionAnchors.Restore(invalid, markdown, renderer));
        window.Close();
    }

    [AvaloniaFact]
    public void Native_drag_across_bold_link_and_two_paragraphs_survives_serialization()
    {
        const string markdown = "先看**蒸汽机**与[铁路](https://example.org)。\n\n第二段继续解释内燃机。";
        var (window, renderer) = View(markdown);
        var blocks = renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().ToArray();
        Assert.Equal(2, blocks.Length);
        Assert.Equal("先看蒸汽机与铁路。", blocks[0].LayoutText);
        var first = blocks[0].GetTextRangeBoundsInControl(2, 1)[0];
        var last = blocks[1].GetTextRangeBoundsInControl(5, 1)[0];
        var from = blocks[0].TranslatePoint(new Point(first.X + 1, first.Center.Y), window)!.Value;
        var to = blocks[1].TranslatePoint(new Point(last.X + 1, last.Center.Y), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);
        Layout();
        var saved = SelectionAnchors.Capture(markdown, renderer);
        Assert.Equal(2, saved.Segments.Count);
        Assert.Equal("蒸汽机与铁路。", saved.Segments[0].Quote);
        Assert.Equal("第二段继续", saved.Segments[1].Quote);
        Assert.Null(saved.Segments[0].ExactRawStart);
        var json = JsonSerializer.Serialize(saved);
        window.Close();
        var (reopened, target) = View(markdown);
        var restored = SelectionAnchors.Restore(JsonSerializer.Deserialize<SelectionAnchor>(json)!, markdown, target);
        Assert.Equal(2, restored.Count);
        Assert.All(restored, part => Assert.True(part.Block.Highlights.TryGetValue("saved-source", out _)));
        reopened.Close();
    }

    [AvaloniaFact]
    public void Appending_a_new_block_keeps_the_checked_source_prefix_and_original_range()
    {
        const string markdown = "蒸汽机运行；蒸汽机继续运行。";
        var (window, renderer) = View(markdown);
        var block = Assert.Single(SelectionAnchors.Blocks(renderer));
        var from = block.GetTextRangeBoundsInControl(6, 1)[0];
        var to = block.GetTextRangeBoundsInControl(9, 1)[0];
        window.MouseDown(block.TranslatePoint(new Point(from.X + 1, from.Center.Y), window)!.Value, MouseButton.Left);
        var ending = block.TranslatePoint(new Point(to.X + 1, to.Center.Y), window)!.Value;
        window.MouseMove(ending, RawInputModifiers.LeftMouseButton);
        window.MouseUp(ending, MouseButton.Left);
        Layout();
        var saved = SelectionAnchors.Capture(markdown, renderer);
        window.Close();
        const string appended = markdown + "\n\n追加的新段落。";
        var (reopened, target) = View(appended);
        Assert.Equal(6, Assert.Single(SelectionAnchors.Restore(saved, appended, target)).Start);
        reopened.Close();
    }

    [AvaloniaFact]
    public void Narrowing_reflows_real_text_and_reveals_the_saved_source_line()
    {
        var markdown = string.Concat(Enumerable.Repeat("铁路让城市之间的旅行变得便利。", 120)) + "第二处来源文字。\n\n" + string.Concat(Enumerable.Repeat("后续内容。\n\n", 30));
        var (window, renderer) = View(markdown);
        var block = SelectionAnchors.Blocks(renderer)[0];
        var width = block.Bounds.Width;
        var height = block.TextLayout.Height;
        var start = block.LayoutText.Length - 8;
        block.SelectionStart = start; block.SelectionEnd = start + 7;
        var saved = SelectionAnchors.Capture(markdown, renderer);
        window.Width = 720; Layout();
        Assert.True(block.Bounds.Width < width);
        Assert.True(block.TextLayout.Height > height);
        var restored = Assert.Single(SelectionAnchors.Restore(saved, markdown, renderer));
        var scroll = (ScrollViewer)window.Content!;
        SelectionAnchors.Reveal(restored, scroll);
        Layout();
        var rectangle = block.GetTextRangeBoundsInControl(restored.Start, restored.Length)[0];
        var top = block.TranslatePoint(rectangle.TopLeft, scroll)!.Value;
        Assert.InRange(top.Y, 0, scroll.Viewport.Height);
        Assert.InRange(block.TranslatePoint(new Point(block.Bounds.Width, 0), scroll)!.Value.X, 0, scroll.Bounds.Width);
        window.Close();
    }

    // Catches restoring the first matching word when the user actually selected the second.
    [AvaloniaFact]
    public void Second_repeated_word_retains_its_own_display_and_exact_plain_source_range()
    {
        const string markdown = "蒸汽机运行；蒸汽机继续运行。";
        var (window, renderer) = View(markdown);
        var block = Assert.Single(renderer.GetVisualDescendants().OfType<MarkdownTextBlock>());
        Assert.Equal(markdown, block.LayoutText);
        var firstBounds = block.GetTextRangeBoundsInControl(6, 1)[0];
        var endBounds = block.GetTextRangeBoundsInControl(9, 1)[0];
        window.MouseDown(block.TranslatePoint(new Point(firstBounds.X + 1, firstBounds.Center.Y), window)!.Value, MouseButton.Left);
        var dragEnd = block.TranslatePoint(new Point(endBounds.X + 1, endBounds.Center.Y), window)!.Value;
        window.MouseMove(dragEnd, RawInputModifiers.LeftMouseButton);
        window.MouseUp(dragEnd, MouseButton.Left);
        Layout();
        var saved = SelectionAnchors.Capture(markdown, renderer);
        var json = JsonSerializer.Serialize(saved);
        var segment = Assert.Single(saved.Segments);
        Assert.Equal(6, segment.Start);
        Assert.Equal(6, segment.ExactRawStart);
        Assert.Equal("蒸汽机", segment.Quote);
        window.Close();
        var (reopened, target) = View(markdown);
        var restored = SelectionAnchors.Restore(JsonSerializer.Deserialize<SelectionAnchor>(json)!, markdown, target);
        var resolved = Assert.Single(restored);
        Assert.Equal(6, resolved.Block.SelectionStart);
        Assert.Equal(9, resolved.Block.SelectionEnd);
        reopened.Close();
    }

    [AvaloniaFact]
    public void Changed_markdown_is_rejected_even_if_the_same_quote_still_exists()
    {
        const string original = "蒸汽机运行；蒸汽机继续运行。";
        var (window, renderer) = View(original);
        var block = Assert.Single(renderer.GetVisualDescendants().OfType<MarkdownTextBlock>());
        block.SelectionStart = 6; block.SelectionEnd = 9;
        var saved = SelectionAnchors.Capture(original, renderer);
        window.Close();
        const string changed = "在前面插入正文。蒸汽机运行；蒸汽机继续运行。";
        var (reopened, target) = View(changed);
        Assert.Throws<InvalidDataException>(() => SelectionAnchors.Restore(saved, changed, target));
        Assert.All(target.GetVisualDescendants().OfType<MarkdownTextBlock>(), text => Assert.Equal(text.SelectionStart, text.SelectionEnd));
        reopened.Close();
    }

    public static (Window, MarkdownRenderer) View(string markdown, double width = 1050)
    {
        var renderer = new MarkdownRenderer { DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline)) };
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = renderer };
        renderer.Margin = new Thickness(44, 28);
        var window = new Window { Width = width, Height = 740, Content = scroll };
        window.Show(); Layout();
        return (window, renderer);
    }
    public static void Layout() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
}
