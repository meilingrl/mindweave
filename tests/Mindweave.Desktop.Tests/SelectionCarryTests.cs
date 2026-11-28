using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using LiveMarkdown.Avalonia;
using Mindweave.Desktop.Markdown;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Carrying a reader's selection from a streaming answer's view to the finished answer's view (tests-10 in
/// docs/design/diagnostics-and-upkeep.md). The streaming flows that use it are in <see cref="StreamingSelectionTests"/>;
/// these name its own rules: found again by block and text, at the nearest place when the text moved, and nothing
/// selected rather than a guess.
/// </summary>
public class SelectionCarryTests
{
    [Fact]
    public void A_part_still_at_its_offset_is_found_there()
    {
        Assert.Equal(2, SelectionCarry.Locate("正在增长的回答", new CarriedSegment(0, 0, 2, "增长")));
    }

    /// <summary>A closing <c>**</c> removes its markers from the shown text, so what follows moves two places left.</summary>
    [Fact]
    public void A_part_whose_text_moved_is_found_at_the_nearest_place_showing_it()
    {
        Assert.Equal(8, SelectionCarry.Locate("前面未闭合的文字和后面", new CarriedSegment(0, 0, 10, "和后")));
        // Two places show it: the one nearer to where it was wins, before or after.
        Assert.Equal(6, SelectionCarry.Locate("文字，文字，文字", new CarriedSegment(0, 0, 5, "文字")));
        Assert.Equal(0, SelectionCarry.Locate("文字，文字，文字", new CarriedSegment(0, 0, 1, "文字")));
    }

    [Fact]
    public void A_part_whose_text_is_gone_is_not_found()
    {
        Assert.Null(SelectionCarry.Locate("前面的文字", new CarriedSegment(0, 0, 2, "后面")));
        Assert.Null(SelectionCarry.Locate("短", new CarriedSegment(0, 0, 0, "短文字")));
    }

    [Fact]
    public void Two_selections_are_the_same_only_with_the_same_parts()
    {
        CarriedSegment[] one = [new(0, 0, 2, "增长"), new(12, 0, 0, "第二")];
        Assert.True(SelectionCarry.Same(null, null));
        Assert.True(SelectionCarry.Same(one, [new(0, 0, 2, "增长"), new(12, 0, 0, "第二")]));
        Assert.False(SelectionCarry.Same(one, null));
        Assert.False(SelectionCarry.Same(null, one));
        Assert.False(SelectionCarry.Same(one, [new(0, 0, 2, "增长")]));
        Assert.False(SelectionCarry.Same(one, [new(0, 0, 3, "增长"), new(12, 0, 0, "第二")]));
    }

    [AvaloniaFact]
    public void Nothing_selected_captures_nothing()
    {
        var (window, renderer) = Render("第一段文字。\n\n第二段文字。");
        Assert.Null(SelectionCarry.Capture(renderer));
        window.Close();
    }

    /// <summary>
    /// A selection over two paragraphs of the streaming text is selected again in the finished text, where a closing
    /// <c>**</c> moved the second part and a new paragraph followed.
    /// </summary>
    [AvaloniaFact]
    public void A_selection_is_carried_to_the_finished_text_block_by_block()
    {
        var (streamingWindow, streaming) = Render("第一段文字。\n\n前面**未闭合的文字和后面");
        var shown = SourceAnchors.Blocks(streaming);
        Select(shown[0], 3, 2);
        Select(shown[1], shown[1].LayoutText.IndexOf("和后", StringComparison.Ordinal), 2);
        var carried = SelectionCarry.Capture(streaming);
        streamingWindow.Close();

        Assert.NotNull(carried);
        Assert.Equal(["文字", "和后"], carried.Select(s => s.Quote));
        var (window, finished) = Render("第一段文字。\n\n前面**未闭合**的文字和后面还有。\n\n新的一段。");
        Assert.True(SelectionCarry.Apply(finished, carried));

        var blocks = SourceAnchors.Blocks(finished);
        Assert.Equal("文字", Selected(blocks[0]));
        Assert.Equal("和后", Selected(blocks[1]));
        Assert.Equal(blocks[1].LayoutText.IndexOf("和后", StringComparison.Ordinal), blocks[1].SelectionStart);
        Assert.Equal(blocks[2].SelectionStart, blocks[2].SelectionEnd);
        window.Close();
    }

    /// <summary>When one part is no longer shown, no part is selected: a partial selection would be a guess.</summary>
    [AvaloniaFact]
    public void A_selection_with_a_part_no_longer_shown_selects_nothing()
    {
        var (streamingWindow, streaming) = Render("第一段文字。\n\n第二段的草稿");
        var shown = SourceAnchors.Blocks(streaming);
        Select(shown[0], 3, 2);
        Select(shown[1], 4, 2);
        var carried = SelectionCarry.Capture(streaming)!;
        streamingWindow.Close();

        var (window, finished) = Render("第一段文字。\n\n第二段改写了。");
        Assert.False(SelectionCarry.Apply(finished, carried));
        Assert.All(SourceAnchors.Blocks(finished), b => Assert.Equal(b.SelectionStart, b.SelectionEnd));

        // A block that is gone altogether is the same.
        Assert.False(SelectionCarry.Apply(finished, [new CarriedSegment(SourceStart: 500, Ordinal: 0, Start: 0, Quote: "文字")]));
        Assert.All(SourceAnchors.Blocks(finished), b => Assert.Equal(b.SelectionStart, b.SelectionEnd));
        window.Close();
    }

    private static void Select(MarkdownTextBlock block, int start, int length)
    {
        block.SelectionStart = start;
        block.SelectionEnd = start + length;
    }

    private static string Selected(MarkdownTextBlock block) =>
        block.LayoutText[Math.Min(block.SelectionStart, block.SelectionEnd)..Math.Max(block.SelectionStart, block.SelectionEnd)];

    private static (Window Window, MarkdownRenderer Renderer) Render(string markdown)
    {
        MarkdownSetup.EnsureRegistered();
        var renderer = new MarkdownRenderer();
        var window = new Window { Content = new Border { Padding = new Thickness(20), Child = renderer }, Width = 800, Height = 600 };
        window.Show();
        renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdig.Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline));
        window.UpdateLayout();
        return (window, renderer);
    }
}
