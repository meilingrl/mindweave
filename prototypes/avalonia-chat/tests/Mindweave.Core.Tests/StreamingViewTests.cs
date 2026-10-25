using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mindweave.Core;
using Mindweave.Desktop;
using Xunit;

namespace Mindweave.Core.Tests;

public class StreamingViewTests
{
    private static string Text(SelectableTextBlock text) => string.Concat(text.Inlines!.OfType<Run>().Select(x => x.Text));
    private static void Layout() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }

    [AvaloniaFact]
    public async Task Streaming_preserves_older_selection_viewport_and_completed_paragraph_controls()
    {
        var stream = new ControlledReplyStream();
        var vm = new WorkspaceViewModel(replyStream: stream);
        var window = new MainWindow(vm);
        window.Show(); Layout();
        var old = Assert.Single(window.GetVisualDescendants().OfType<SelectableTextBlock>(), x => Text(x).StartsWith("19 世纪初"));
        var scroll = window.FindControl<ScrollViewer>("ChatScroll")!;
        scroll.Offset = new Vector(0, 260); Layout();
        old.SelectionStart = 7; old.SelectionEnd = 10;
        var priorOffset = scroll.Offset.Y;
        var generation = vm.SendAsync("stream-view");
        await Append(vm, stream, "# A heading\n\nA completed paragraph.\n\n**蒸"); Layout();
        var complete = Assert.Single(window.GetVisualDescendants().OfType<SelectableTextBlock>(), x => Text(x) == "A completed paragraph.");
        await Append(vm, stream, "汽机**与[铁路](https://example.org)\n\n```text\n代码"); Layout();
        Assert.Contains(window.GetVisualDescendants(), x => ReferenceEquals(x, old));
        Assert.Contains(window.GetVisualDescendants(), x => ReferenceEquals(x, complete));
        Assert.Equal("蒸汽机", old.SelectedText);
        Assert.InRange(scroll.Offset.Y, priorOffset - 2, priorOffset + 2);
        vm.Generation.Cancel(vm.Current.Id);
        await generation;
        window.Close();
    }

    [AvaloniaFact]
    public void Resizing_restores_the_same_visible_character_in_a_long_paragraph()
    {
        var state = new ExplorationState([new("r", "Resize")]);
        state.Current.Messages.Add(new("answer", false,
            string.Concat(Enumerable.Repeat("A railway connects cities and changes how people travel. ", 180)) + "\n\n" +
            string.Concat(Enumerable.Repeat("More text after the long paragraph.\n\n", 30))));
        var vm = new WorkspaceViewModel(state);
        var window = new MainWindow(vm);
        window.Show(); Layout();
        var scroll = window.FindControl<ScrollViewer>("ChatScroll")!;
        scroll.Offset = new Vector(0, 1100); Layout();
        var saved = vm.Current.Position;
        Assert.NotNull(saved.CharacterIndex);
        var text = window.GetVisualDescendants().OfType<SelectableTextBlock>().First(x => Text(x).StartsWith("A railway"));
        var before = text.TranslatePoint(new Point(0, text.TextLayout.HitTestTextPosition(saved.CharacterIndex!.Value).Y), scroll)!.Value.Y;
        var wideTextWidth = text.Bounds.Width;
        var wideTextHeight = text.TextLayout.Height;
        var wideViewportWidth = scroll.Viewport.Width;
        window.Width = 1000; Layout();
        Assert.True(window.ClientSize.Width <= 1000);
        Assert.True(scroll.Viewport.Width < wideViewportWidth);
        Assert.True(text.Bounds.Width < wideTextWidth, $"text={text.Bounds.Width}; viewport={scroll.Viewport.Width}; client={window.ClientSize.Width}");
        Assert.True(text.TextLayout.Height > wideTextHeight);
        var rightEdge = text.TranslatePoint(new Point(text.Bounds.Width, 0), scroll)!.Value.X;
        Assert.InRange(rightEdge, 0, scroll.Bounds.Width);
        var after = text.TranslatePoint(new Point(0, text.TextLayout.HitTestTextPosition(saved.CharacterIndex.Value).Y), scroll)!.Value.Y;
        Assert.InRange(after, before - 30, before + 30);
        window.Width = 1440; Layout();
        var wide = text.TranslatePoint(new Point(0, text.TextLayout.HitTestTextPosition(saved.CharacterIndex.Value).Y), scroll)!.Value.Y;
        Assert.InRange(wide, before - 30, before + 30);
        window.Close();
    }

    private static async Task Append(WorkspaceViewModel vm, ControlledReplyStream stream, string chunk)
    {
        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Updated(GenerationUpdate update) { if (update.Change == GenerationChange.Appended) next.TrySetResult(); }
        vm.MessageUpdated += Updated;
        await stream.For("stream-view").Writer.WriteAsync(chunk);
        await next.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.MessageUpdated -= Updated;
    }
}
