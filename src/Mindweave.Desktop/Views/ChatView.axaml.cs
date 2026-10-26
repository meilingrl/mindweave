using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Views;

/// <summary>Owns transient layout capture/reveal only. Main owns all navigation and storage.</summary>
public partial class ChatView : UserControl
{
    private ChatViewModel? page;
    private readonly ScrollViewer scroll;
    private bool restoring, ready;
    private long layoutVersion;
    public ChatView()
    {
        AvaloniaXamlLoader.Load(this);
        scroll = this.FindControl<ScrollViewer>("MessagesScroll")!;
        scroll.ScrollChanged += (_, _) => { if (ready && !restoring) CaptureReading(); };
        DataContextChanged += (_, _) => AttachPage();
        Loaded += (_, _) => { AttachPage(); ScheduleRestore(); };
    }
    private void AttachPage()
    {
        if (page == DataContext) return;
        Unsubscribe(); page = DataContext as ChatViewModel; ready = false;
        if (page is not null) { page.CaptureReadingRequested += CaptureReading; page.RevealRequested += ScheduleRestore; }
        ScheduleRestore();
    }
    private void Unsubscribe()
    {
        layoutVersion++;
        if (page is not null) { page.CaptureReadingRequested -= CaptureReading; page.RevealRequested -= ScheduleRestore; }
        page = null;
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { Unsubscribe(); ready = false; base.OnDetachedFromVisualTree(e); }
    private IEnumerable<MarkdownMessageView> Messages() => scroll.GetVisualDescendants().OfType<MarkdownMessageView>();
    private void CaptureReading()
    {
        if (!ready || restoring || page is not { IsCurrentPage: true } || DataContext != page) return;
        if (scroll.Offset.Y <= 0) { page.RecordReading(null); return; }
        foreach (var message in Messages())
            if (message.CaptureReading() is { } location) { page.RecordReading(location); return; }
    }
    private void ScheduleRestore()
    {
        var captured = page; var version = ++layoutVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (version != layoutVersion || captured is not { IsCurrentPage: true } || page != captured || DataContext != captured || TopLevel.GetTopLevel(this) is null) return;
            restoring = true;
            try
            {
                scroll.UpdateLayout();
                if (captured.PendingSourceReveal is { } source)
                {
                    var message = Messages().SingleOrDefault(m => m.MessageId == source.Selection.MessageId);
                    try
                    {
                        if (message is null) throw new InvalidDataException("来源消息当前没有可用布局。");
                        var fragments = message.RestoreSelection(source.Selection);
                        MarkdownSelectionAdapter.Reveal(fragments[0], scroll);
                    }
                    catch (InvalidDataException)
                    {
                        if (message is not null) RevealMessage(message);
                        captured.ReportSelectionFailure("无法精确定位原句。已返回来源消息；保存引用：\n" + source.Selection.Quote);
                    }
                    finally { captured.ConsumeSourceReveal(); }
                }
                else if (!ready && captured.ReadState.Location is { } location)
                {
                    var message = Messages().SingleOrDefault(m => m.MessageId == location.MessageId);
                    if (message is not null && !message.RestoreReading(location)) RevealMessage(message);
                }
                ready = true;
            }
            finally { restoring = false; }
        }, DispatcherPriority.Loaded);
    }
    private void RevealMessage(MarkdownMessageView message)
    {
        if (message.TranslatePoint(default, scroll) is { } point)
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + point.Y - 24));
    }
    private async void CreateBranchClicked(object? sender, RoutedEventArgs args)
    {
        if (page is not { IsCurrentPage: true } captured || sender is not Button { DataContext: MessageViewModel message } || !message.CanCreateBranch) return;
        try
        {
            // Capture the immutable selection on the UI thread before Main awaits persistence.
            var selection = Messages().Single(v => v.MessageId == message.Id).CaptureSelection();
            await captured.CreateBranchAsync(selection);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
        { captured.ReportSelectionFailure(error.Message); }
    }
}
