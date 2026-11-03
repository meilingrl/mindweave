using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Reading;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.ViewModels.Conversations;

namespace Mindweave.Desktop.Views.Conversations;

/// <summary>
/// Conversation page view. Converts geometry to reading positions and back (architecture §2.2: the view only does
/// geometry and anchor conversion; the view model's service owns the values). On open it shows, in order of
/// precedence: the source passage of a branch (return to source), the slot's reading position, or the end.
/// </summary>
public sealed partial class ConversationView : UserControl
{
    /// <summary>The reading line: the content at this distance below the top of the viewport is "where the reader is".</summary>
    private const double ReadingLine = 8;

    /// <summary>Space left above a revealed source passage so the reader sees what led up to it.</summary>
    private const double SourceContext = 80;

    private readonly DispatcherTimer _capture;
    private TaskCompletionSource _restored = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ConversationViewModel? _page;
    private Window? _window;
    private bool _followBottom = true;
    private bool _restoring = true;

    public ConversationView()
    {
        InitializeComponent();
        _capture = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => CapturePosition());

        // Follow the newest text only while the reader is already at the bottom; never yank them back from
        // something they scrolled up to read.
        Scroller.ScrollChanged += (_, e) =>
        {
            if (_restoring)
            {
                return;
            }

            if (e.ExtentDelta.Y != 0 && _followBottom)
            {
                Scroller.ScrollToEnd();
            }
            else if (e.OffsetDelta.Y != 0)
            {
                _followBottom = IsAtBottom();
            }

            _capture.Stop();
            _capture.Start();
        };
        BranchMarks.Attach(Scroller, MessageList);
        ComposerHost.SettingsRequested += (_, _) => (DataContext as ConversationViewModel)?.OpenSettingsCommand.Execute(null);
    }

    /// <summary>Completes once the page has been placed at its source, reading position or end; tests await it.</summary>
    internal Task Restored => _restored.Task;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_page is not null)
        {
            _page.IsShown = false;
        }

        _page = DataContext as ConversationViewModel;
        UpdateShown();

        _restoring = true;
        if (_restored.Task.IsCompleted)
        {
            _restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        if (_page is not null)
        {
            _ = RestoreAsync(_page);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
        {
            _window.PropertyChanged += OnWindowPropertyChanged;
        }

        UpdateShown();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Off screen: the running answer's live counter stops ticking until the page is shown again.
        if (_window is not null)
        {
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window = null;
        }

        if (_page is not null)
        {
            _page.IsShown = false;
        }

        _capture.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == IsVisibleProperty)
        {
            UpdateShown();
        }
    }

    /// <summary>
    /// The page is shown while it is in a window that is visible and not minimised. A window covered by others still
    /// counts as shown: the platforms report no occlusion to Avalonia.
    /// </summary>
    private void UpdateShown()
    {
        if (_page is not null)
        {
            _page.IsShown = this.IsAttachedToVisualTree() && _window is not { WindowState: WindowState.Minimized } and not { IsVisible: false };
        }
    }

    private async Task RestoreAsync(ConversationViewModel page)
    {
        await page.Loaded;
        var position = await page.CurrentPositionAsync();

        // Let the messages be laid out before measuring anything.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (!ReferenceEquals(page, _page))
        {
            return;
        }

        UpdateLayout();
        if (page.TakeSourceFocus() is { } source)
        {
            RevealSource(page, source);
        }
        else
        {
            ApplyPosition(page, position);
        }

        _followBottom = IsAtBottom();
        _restoring = false;
        _restored.TrySetResult();
    }

    /// <summary>Return to source: select and highlight the passage, or fall back to its answer with a notice (MW-C13).</summary>
    private void RevealSource(ConversationViewModel page, BranchSource source)
    {
        var (container, answer) = Find(page, source.SourceMessage);
        var top = answer?.RevealSource(source, Scroller);
        if (top is null)
        {
            page.SourceNotice = UiText.SourceNotLocated(source.Quote);
            top = container is null ? null : TopOf(container);
        }

        if (top is { } y)
        {
            ScrollBy(y - SourceContext);
        }
    }

    /// <summary>Restores a reading position: same character of the same source block, else the message, else the end.</summary>
    private void ApplyPosition(ConversationViewModel page, ReadingPosition? position)
    {
        var (container, answer) = position is null ? (null, null) : Find(page, position.Message);
        if (container is null)
        {
            Scroller.ScrollToEnd();
            UpdateLayout();
            return;
        }

        // Same character of the same source block, else the same distance into the message, else its top.
        var locator = position!.LocatorVersion == SourceAnchors.LocatorVersion ? SourceAnchors.DeserializeLocator(position.Locator) : null;
        var line = locator is { IsMessageTop: false } && answer is not null ? SourceAnchors.ReadingLineOf(locator, answer.Renderer, Scroller) : null;
        line ??= TopOf(container) + (locator is { IsMessageTop: true } ? locator.Within : 0);
        ScrollBy((line ?? 0) - ReadingLine);
    }

    private void CapturePosition()
    {
        _capture.Stop();
        if (_restoring || _page is not { } page)
        {
            return;
        }

        for (var i = 0; i < page.Messages.Count; i++)
        {
            if (MessageList.ContainerFromIndex(i) is not { } container || TopOf(container) is not { } top || top + container.Bounds.Height <= ReadingLine)
            {
                continue;
            }

            var item = page.Messages[i];
            var answer = item.IsAnswer ? container.GetVisualDescendants().OfType<AnswerView>().FirstOrDefault() : null;
            var locator = (answer is null ? null : SourceAnchors.LocatorAt(answer.Renderer, Scroller, ReadingLine))
                ?? ReadingLocator.MessageTop(ReadingLine - top);
            page.ReportPosition(new ReadingPosition(item.Id, 0, SourceAnchors.Serialize(locator), SourceAnchors.LocatorVersion));
            return;
        }
    }

    private (Control? Container, AnswerView? Answer) Find(ConversationViewModel page, MessageId message)
    {
        var index = page.Messages.ToList().FindIndex(m => m.Id == message);
        if (index < 0 || MessageList.ContainerFromIndex(index) is not { } container)
        {
            return (null, null);
        }

        return (container, page.Messages[index].IsAnswer ? container.GetVisualDescendants().OfType<AnswerView>().FirstOrDefault() : null);
    }

    private double? TopOf(Visual visual) => visual.TranslatePoint(default, Scroller)?.Y;

    private void ScrollBy(double delta)
    {
        Scroller.Offset = new Vector(0, Math.Clamp(Scroller.Offset.Y + delta, 0, Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height)));
        UpdateLayout();
    }

    private bool IsAtBottom() => Scroller.Offset.Y + Scroller.Viewport.Height >= Scroller.Extent.Height - 24;
}
