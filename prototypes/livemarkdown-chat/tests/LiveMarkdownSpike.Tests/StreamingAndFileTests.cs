using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Xunit;

namespace LiveMarkdownSpike.Tests;

public class StreamingAndFileTests
{
    // Characterizes the actual library producer, not a home-made rerender loop.
    [AvaloniaFact]
    public async Task Producer_append_preserves_a_selected_old_block_and_its_viewport_position()
    {
        var markdown = Samples.Long + "\n\n";
        var (window, renderer) = AnchorTests.View(markdown);
        var builder = new ObservableStringBuilder(markdown);
        var previous = renderer.DocumentUpdate;
        renderer.MarkdownBuilder = builder;
        await Rendered(renderer, builder, previous);
        var scroll = (ScrollViewer)window.Content!;
        scroll.Offset = new Vector(0, 1000); AnchorTests.Layout();
        var selected = SelectionAnchors.Blocks(renderer).First(x => x.LayoutText.StartsWith("铁路把") && x.TranslatePoint(default, scroll) is { Y: >= 0 and < 500 });
        selected.SelectionStart = 0; selected.SelectionEnd = 3;
        var source = SelectionAnchors.Capture(markdown, renderer);
        var oldPoint = selected.TranslatePoint(selected.GetTextRangeBoundsInControl(0, 3)[0].TopLeft, scroll)!.Value;
        foreach (var chunk in new[] { "## 新回复\n\n拆开的*", "*粗体**和[链", "接](https://example.org)。\n\n```text\nco", "de\n```\n\n结束。" })
        {
            previous = renderer.DocumentUpdate;
            builder.Append(chunk);
            await Rendered(renderer, builder, previous);
            Assert.Contains(SelectionAnchors.Blocks(renderer), x => ReferenceEquals(x, selected));
            Assert.Equal(0, selected.SelectionStart);
            Assert.Equal(3, selected.SelectionEnd);
            Assert.Equal("铁路把", selected.ActualSelectedText);
            var point = selected.TranslatePoint(selected.GetTextRangeBoundsInControl(0, 3)[0].TopLeft, scroll)!.Value;
            Assert.InRange(point.Y, oldPoint.Y - 1, oldPoint.Y + 1);
        }
        Assert.IsType<MarkdownDocumentUpdate.Incremental>(renderer.DocumentUpdate);
        var restored = Assert.Single(SelectionAnchors.Restore(source, builder.ToString(), renderer));
        Assert.Equal("铁路把", restored.Block.ActualSelectedText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Save_button_writes_the_selected_second_word_and_a_new_window_restores_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), "livemarkdown-spike-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "anchor.json");
        try
        {
            var window = new SpikeWindow(path);
            window.Show(); AnchorTests.Layout();
            var renderer = window.GetVisualDescendants().OfType<MarkdownRenderer>().First();
            var block = SelectionAnchors.Blocks(renderer).Single(x => x.LayoutText == "蒸汽机运行；蒸汽机继续运行。");
            block.SelectionStart = 6; block.SelectionEnd = 9;
            window.GetVisualDescendants().OfType<Button>().Single(x => x.Content?.ToString() == "保存选区").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var attempt = 0; !File.Exists(path) && attempt < 200; attempt++) await Task.Delay(5);
            var saved = JsonSerializer.Deserialize<SavedSample>(await File.ReadAllTextAsync(path))!;
            Assert.Equal(6, Assert.Single(saved.Anchor.Segments).Start);
            Assert.Equal("蒸汽机", saved.Anchor.Segments[0].Quote);
            window.Close();
            var reopened = new SpikeWindow(path);
            reopened.Show(); AnchorTests.Layout();
            var target = reopened.GetVisualDescendants().OfType<MarkdownRenderer>().First();
            var restored = SelectionAnchors.Blocks(target).Single(x => x.LayoutText == "蒸汽机运行；蒸汽机继续运行。");
            Assert.Equal(6, restored.SelectionStart);
            Assert.Equal(9, restored.SelectionEnd);
            Assert.True(restored.Highlights.TryGetValue("saved-source", out _));
            reopened.Close();
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task Rendered(MarkdownRenderer renderer, ObservableStringBuilder builder, MarkdownDocumentUpdate? previous)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (renderer.DocumentUpdate is { } current && !ReferenceEquals(current, previous) && current.Version == builder.Version) { AnchorTests.Layout(); return; }
            await Task.Delay(5);
        }
        throw new TimeoutException("LiveMarkdown producer did not publish the requested version.");
    }
}
