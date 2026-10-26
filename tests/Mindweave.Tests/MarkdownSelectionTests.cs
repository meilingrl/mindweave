using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Markdig;
using Mindweave.Core.Branches;
using Mindweave.Core.Chat;
using Mindweave.Application.Chat;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.ViewModels;
using Xunit;

namespace Mindweave.Tests;

public sealed class MarkdownSelectionTests
{
    private static readonly Guid MessageId = Guid.Parse("30000000-0000-0000-0000-000000000001");

    // A search-based restore incorrectly moves this selection to the first occurrence.
    [AvaloniaFact]
    public void Native_drag_of_second_duplicate_restores_its_UTF16_range_after_serialization()
    {
        const string body = "😀 word; word; end";
        var (window, renderer) = View(body);
        try
        {
            var block = Assert.Single(Blocks(renderer)); Drag(window, block, 9, block, 13);
            var selection = MarkdownSelectionAdapter.Capture(MessageId, 12, body, renderer);
            Assert.Equal("word", selection.Quote); Assert.Equal(12, selection.MessageRevision);
            selection = JsonSerializer.Deserialize<SourceSelection>(JsonSerializer.Serialize(selection))!;
            renderer.ClearSelection();
            var restored = Assert.Single(MarkdownSelectionAdapter.Restore(selection, body, renderer));
            Assert.Equal(9, restored.Start); Assert.Equal(4, restored.Length);
            Assert.Equal(9, restored.Block.SelectionStart); Assert.Equal(13, restored.Block.SelectionEnd);
            Assert.Equal(9, JsonNode.Parse(selection.Anchor.Payload)!["Segments"]![0]!["ExactRawStart"]!.GetValue<int>());
        }
        finally { window.Close(); }
    }

    // Capturing only one text control would lose formatted text and the second paragraph.
    [AvaloniaFact]
    public void Native_drag_across_bold_link_inline_code_and_two_paragraphs_roundtrips()
    {
        const string body = "First **bold** [link](https://example.invalid) `code`.\n\nSecond paragraph.";
        var (window, renderer) = View(body);
        try
        {
            var blocks = Blocks(renderer); Assert.Equal(2, blocks.Count);
            Drag(window, blocks[0], 6, blocks[1], 6);
            var selection = MarkdownSelectionAdapter.Capture(MessageId, 1, body, renderer);
            Assert.Equal("bold link code.\nSecond", selection.Quote);
            renderer.ClearSelection();
            Assert.Equal(2, MarkdownSelectionAdapter.Restore(selection, body, renderer).Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Heading_list_quote_and_fenced_code_all_retain_source_block_identity()
    {
        const string body = "# Heading\n\n- list item\n\n> quoted text\n\n```text\ncode value\n```";
        var (window, renderer) = View(body);
        try
        {
            var blocks = Blocks(renderer); Assert.True(blocks.Count >= 4);
            foreach (var block in blocks) { block.SelectionStart = 0; block.SelectionEnd = block.LayoutText.Length; }
            var saved = MarkdownSelectionAdapter.Capture(MessageId, 1, body, renderer);
            Assert.Contains("Heading", saved.Quote); Assert.Contains("list item", saved.Quote);
            Assert.Contains("quoted text", saved.Quote); Assert.Contains("code value", saved.Quote);
            Assert.Equal(blocks.Count, MarkdownSelectionAdapter.Restore(saved, body, renderer).Count);
        }
        finally { window.Close(); }
    }

    // Validate every fragment before mutating any existing selection/highlight.
    [AvaloniaFact]
    public void Corrupt_envelopes_are_rejected_atomically_even_when_earlier_fragment_is_valid()
    {
        const string body = "first paragraph.\n\nsecond paragraph.";
        var (window, renderer) = View(body);
        try
        {
            foreach (var block in Blocks(renderer)) { block.SelectionStart = 0; block.SelectionEnd = block.LayoutText.Length; }
            var saved = MarkdownSelectionAdapter.Capture(MessageId, 4, body, renderer);
            renderer.ClearSelection();
            foreach (var invalid in new[] {
                saved with { SourceBodyHash = new string('0', 64) },
                saved with { Quote = "other" },
                saved with { Anchor = saved.Anchor with { FormatVersion = 9 } },
                saved with { Anchor = saved.Anchor with { RendererProfile = "other" } },
                Mutate(saved, node => node["Segments"]![1]!["Start"] = int.MaxValue),
                Mutate(saved, node => node["Segments"]![1]!["SourceEnd"] = int.MaxValue),
                Mutate(saved, node => node["Segments"]![1]!["LayoutHash"] = "wrong"),
                Mutate(saved, node => node["Segments"]![0]!["ExactRawStart"] = 8),
                Mutate(saved, node => node["Unexpected"] = true),
                Mutate(saved, node => node["Segments"]![0]!.AsObject().Remove("Ordinal")),
                saved with { Anchor = saved.Anchor with { Payload = "{\"SourceLength\":1,\"SourceLength\":2}" } },
                saved with { Anchor = saved.Anchor with { Payload = "null" } }
            })
            {
                Assert.Throws<InvalidDataException>(() => MarkdownSelectionAdapter.Restore(invalid, body, renderer));
                Assert.All(Blocks(renderer), b => { Assert.Equal(b.SelectionStart, b.SelectionEnd); Assert.False(b.Highlights.TryGetValue("saved-source", out _)); });
            }
            Assert.Throws<InvalidDataException>(() => MarkdownSelectionAdapter.Restore(saved, "x" + body, renderer));
            MarkdownSelectionAdapter.Restore(saved, body, renderer);
            var before = Blocks(renderer).Select(b => (b.SelectionStart, b.SelectionEnd)).ToArray();
            Assert.Throws<InvalidDataException>(() => MarkdownSelectionAdapter.Restore(Mutate(saved, n => n["Segments"]![1]!["Length"] = -1), body, renderer));
            Assert.Equal(before, Blocks(renderer).Select(b => (b.SelectionStart, b.SelectionEnd)).ToArray());
            Assert.All(Blocks(renderer), b => Assert.True(b.Highlights.TryGetValue("saved-source", out _)));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Partial_table_selection_is_explicitly_rejected()
    {
        const string body = "| one | two |\n| --- | --- |\n| red | blue |";
        var (window, renderer) = View(body);
        try
        {
            var block = Blocks(renderer).First(); block.SelectionStart = 0; block.SelectionEnd = 1;
            Assert.Throws<InvalidDataException>(() => MarkdownSelectionAdapter.Capture(MessageId, 1, body, renderer));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Append_and_narrow_reflow_preserve_checked_selection_and_reading_block()
    {
        var body = string.Concat(Enumerable.Repeat("long paragraph of readable text. ", 80)) + "\n\nlast block.";
        var (window, renderer) = View(body);
        try
        {
            var block = Blocks(renderer)[0]; var height = block.Bounds.Height;
            block.SelectionStart = 60; block.SelectionEnd = 69;
            var selection = MarkdownSelectionAdapter.Capture(MessageId, 1, body, renderer);
            var scroll = (ScrollViewer)window.Content!; scroll.Offset = new Vector(0, 100); Layout();
            var reading = MarkdownSelectionAdapter.CaptureReading(MessageId, renderer, scroll);
            Assert.NotNull(reading); Assert.Equal(0, reading.SourceStart);
            var appended = body + "\n\nappended paragraph.";
            renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdig.Markdown.Parse(appended, MarkdownUpdateProducer.DefaultPipeline));
            window.Width = 500; Layout(); Assert.True(Blocks(renderer)[0].Bounds.Height > height);
            Assert.Equal(60, Assert.Single(MarkdownSelectionAdapter.Restore(selection, appended, renderer)).Start);
            Assert.True(MarkdownSelectionAdapter.RestoreReading(reading, MessageId, renderer, scroll));
            MarkdownSelectionAdapter.Reveal(Assert.Single(MarkdownSelectionAdapter.Restore(selection, appended, renderer)), scroll);
            var part = Assert.Single(MarkdownSelectionAdapter.Restore(selection, appended, renderer));
            var point = part.Block.TranslatePoint(part.Block.GetTextRangeBoundsInControl(part.Start, part.Length)[0].TopLeft, scroll)!.Value;
            Assert.InRange(point.Y, 0, scroll.Viewport.Height);
            Assert.False(MarkdownSelectionAdapter.RestoreReading(reading with { SourceStart = int.MaxValue }, MessageId, renderer, scroll));
            Assert.False(MarkdownSelectionAdapter.RestoreReading(reading with { OffsetWithinBlock = double.NaN }, MessageId, renderer, scroll));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Product_message_projects_durable_terminal_identity_and_renders_inert_images()
    {
        var model = new MessageViewModel(new(MessageId, Guid.NewGuid(), 1, MessageRole.Assistant, "old", GenerationStatus.Completed, 7, null, null));
        Assert.True(model.CanCreateBranch); Assert.Equal(7, model.Revision); Assert.Equal(MessageRole.Assistant, model.Role);
        model.Project(new(Guid.NewGuid(), Guid.NewGuid(), MessageId, GenerationKind.Reply, GenerationStatus.Completed, "new", 8, null, true));
        Assert.False(model.CanCreateBranch); Assert.Equal(8, model.Revision);
        model.Project(new(Guid.NewGuid(), Guid.NewGuid(), MessageId, GenerationKind.Reply, GenerationStatus.Completed, "new", 8, null, false));
        Assert.True(model.CanCreateBranch);
        var view = new MarkdownMessageView { MessageId = MessageId, Revision = 8, Body = "hello **bold** ![secret](file:///tmp/should-not-be-opened.png)" };
        var window = new Window { Content = view, Width = 700, Height = 400 };
        try
        {
            window.Show(); Layout();
            Assert.Contains("hello bold", string.Join("", Blocks(view.Renderer).Select(b => b.LayoutText)));
            Assert.Empty(view.Renderer.GetVisualDescendants().OfType<Image>());
            Assert.Contains("图片暂不支持", string.Join("", Blocks(view.Renderer).Select(b => b.LayoutText)));
        }
        finally { window.Close(); }
    }


    [AvaloniaFact]
    public void Product_view_keeps_old_block_selection_on_append_and_reports_changed_block()
    {
        var view = new MarkdownMessageView { MessageId = MessageId, Revision = 1, Body = "word; word.\n\nnext paragraph." };
        var scroll = new ScrollViewer { Content = view, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var window = new Window { Content = scroll, Width = 700, Height = 400 };
        try
        {
            window.Show(); Layout();
            var block = Blocks(view.Renderer)[0]; block.SelectionStart = 6; block.SelectionEnd = 10;
            view.Body += "\n\nappended paragraph.";
            view.Body += "\n\nsecond snapshot before layout."; Layout();
            Assert.Null(view.PreservationFailure);
            var restored = Assert.Single(Blocks(view.Renderer), b => b.SelectionStart != b.SelectionEnd);
            Assert.Equal(6, restored.SelectionStart); Assert.Equal(10, restored.SelectionEnd);
            window.Width = 450; Layout();
            Assert.Equal("word", view.CaptureSelection().Quote);
            view.Body = "changed word; word.\n\nnext paragraph."; Layout();
            Assert.NotNull(view.PreservationFailure);
            Assert.Contains("无法精确定位原句", string.Join("", view.GetVisualDescendants().OfType<TextBlock>().Select(b => b.Text)));
            Assert.All(Blocks(view.Renderer), b => Assert.Equal(b.SelectionStart, b.SelectionEnd));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Embedded_math_selection_is_rejected_without_guessing_a_text_fragment()
    {
        const string body = "before $x^2$ after";
        var (window, renderer) = View(body);
        try
        {
            foreach (var block in Blocks(renderer)) { block.SelectionStart = 0; block.SelectionEnd = block.LayoutText.Length; }
            Assert.Throws<InvalidDataException>(() => MarkdownSelectionAdapter.Capture(MessageId, 1, body, renderer));
        }
        finally { window.Close(); }
    }


    [AvaloniaFact]
    public void Product_view_rejects_another_message_identity_despite_identical_body()
    {
        var view = new MarkdownMessageView { MessageId = MessageId, Revision = 1, Body = "identical body" };
        var window = new Window { Content = view, Width = 700, Height = 400 };
        try
        {
            window.Show(); Layout();
            var block = Assert.Single(Blocks(view.Renderer)); block.SelectionStart = 0; block.SelectionEnd = 9;
            var selection = view.CaptureSelection(); view.Renderer.ClearSelection();
            Assert.Throws<InvalidDataException>(() => view.RestoreSelection(selection with { MessageId = Guid.NewGuid() }));
            Assert.Equal(block.SelectionStart, block.SelectionEnd);
        }
        finally { window.Close(); }
    }


    [AvaloniaFact]
    public void Source_payload_retains_original_hash_when_outer_context_is_recreated_from_storage()
    {
        const string body = "selected phrase.\n\ntail one";
        const string changed = "selected phrase.\n\ntail two";
        var (window, renderer) = View(body);
        try
        {
            var block = Blocks(renderer)[0]; block.SelectionStart = 0; block.SelectionEnd = 8;
            var selection = MarkdownSelectionAdapter.Capture(MessageId, 1, body, renderer);
            renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdig.Markdown.Parse(changed, MarkdownUpdateProducer.DefaultPipeline)); Layout(); renderer.ClearSelection();
            // BranchSource persists the opaque payload. A reopened view may supply the current message body hash.
            var currentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(changed)));
            Assert.Throws<InvalidDataException>(() => MarkdownSelectionAdapter.Restore(selection with { SourceBodyHash = currentHash }, changed, renderer));
            Assert.All(Blocks(renderer), b => Assert.Equal(b.SelectionStart, b.SelectionEnd));
        }
        finally { window.Close(); }
    }


    [AvaloniaFact]
    public void Shared_viewport_append_to_lower_visible_message_keeps_the_current_top_and_offset()
    {
        var (window, scroll, first, lower) = SharedViewport();
        try
        {
            var top = Blocks(first.Renderer)[0];
            Assert.True(Blocks(lower.Renderer)[0].TranslatePoint(default, scroll)!.Value.Y > 50);
            var offset = scroll.Offset.Y; var y = top.TranslatePoint(default, scroll)!.Value.Y;
            lower.Body += "\n\nfirst streamed paragraph.";
            lower.Body += "\n\nsecond snapshot before layout.";
            Layout();
            Assert.InRange(Math.Abs(scroll.Offset.Y - offset), 0, .1);
            Assert.InRange(Math.Abs(top.TranslatePoint(default, scroll)!.Value.Y - y), 0, .1);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Shared_viewport_narrowing_has_one_consistent_top_anchor_across_visible_messages()
    {
        var (window, scroll, first, lower) = SharedViewport();
        try
        {
            var top = Blocks(first.Renderer)[0]; var y = top.TranslatePoint(default, scroll)!.Value.Y;
            var height = top.Bounds.Height;
            Assert.True(Blocks(lower.Renderer)[0].TranslatePoint(default, scroll)!.Value.Y > 50);
            window.Width = 500; Layout();
            Assert.True(top.Bounds.Height > height);
            Assert.InRange(Math.Abs(top.TranslatePoint(default, scroll)!.Value.Y - y), 0, .1);
            var offset = scroll.Offset.Y; Layout();
            Assert.InRange(Math.Abs(scroll.Offset.Y - offset), 0, .1);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Shared_viewport_user_scroll_supersedes_queued_message_preservation()
    {
        var (window, scroll, first, lower) = SharedViewport();
        try
        {
            first.Body += "\n\nqueued snapshot one.";
            first.Body += "\n\nqueued snapshot two.";
            scroll.Offset = new Vector(0, 160);
            Layout();
            Assert.InRange(Math.Abs(scroll.Offset.Y - 160), 0, .1);
            lower.Body += "\n\nanother snapshot.";
            scroll.Offset = new Vector(0, 220); Layout();
            Assert.InRange(Math.Abs(scroll.Offset.Y - 220), 0, .1);
            scroll.Offset = new Vector(0, 120);
            first.Body += "\n\nscroll immediately before snapshot."; Layout();
            Assert.InRange(Math.Abs(scroll.Offset.Y - 120), 0, .1);
        }
        finally { window.Close(); }
    }

    private static (Window Window, ScrollViewer Scroll, MarkdownMessageView First, MarkdownMessageView Lower) SharedViewport()
    {
        var first = new MarkdownMessageView { MessageId = MessageId, Revision = 1, Body = string.Concat(Enumerable.Repeat("shared viewport readable text. ", 16)) };
        var lower = new MarkdownMessageView { MessageId = Guid.Parse("30000000-0000-0000-0000-000000000002"), Revision = 1, Body = "lower visible message." };
        var content = new StackPanel { Children = { new Border { Height = 80 }, first, new Border { Height = 60 }, lower, new Border { Height = 2000 } } };
        var scroll = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var window = new Window { Content = scroll, Width = 800, Height = 600 }; window.Show(); Layout();
        var firstY = Blocks(first.Renderer)[0].TranslatePoint(default, scroll)!.Value.Y;
        scroll.Offset = new Vector(0, firstY + 20); Layout();
        return (window, scroll, first, lower);
    }

    private static SourceSelection Mutate(SourceSelection selection, Action<JsonNode> mutate)
    {
        var node = JsonNode.Parse(selection.Anchor.Payload)!; mutate(node);
        return selection with { Anchor = selection.Anchor with { Payload = node.ToJsonString() } };
    }
    private static IReadOnlyList<MarkdownTextBlock> Blocks(MarkdownRenderer renderer) => renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().Where(b => b.IsVisible).ToArray();
    private static (Window, MarkdownRenderer) View(string markdown)
    {
        var renderer = new MarkdownRenderer { DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdig.Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline)) };
        var scroll = new ScrollViewer { Content = renderer, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var window = new Window { Content = scroll, Width = 850, Height = 500 }; window.Show(); Layout(); return (window, renderer);
    }
    private static void Drag(Window window, MarkdownTextBlock first, int start, MarkdownTextBlock last, int end)
    {
        var a = first.GetTextRangeBoundsInControl(start, 1)[0]; var b = last.GetTextRangeBoundsInControl(end, 1)[0];
        var from = first.TranslatePoint(new Point(a.X + .5, a.Center.Y), window)!.Value;
        var to = last.TranslatePoint(new Point(b.X + .5, b.Center.Y), window)!.Value;
        window.MouseDown(from, MouseButton.Left); window.MouseMove(to, RawInputModifiers.LeftMouseButton); window.MouseUp(to, MouseButton.Left); Layout();
    }
    private static void Layout() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
}
