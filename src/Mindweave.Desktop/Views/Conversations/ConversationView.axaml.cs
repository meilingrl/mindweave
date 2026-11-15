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
    private readonly Dictionary<MessageId, CarriedSelection> _handedOver = [];
    private TaskCompletionSource _restored = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ConversationViewModel? _page;
    private Window? _window;
    private bool _followBottom = true;
    private bool _restoring = true;

    /// <summary>Answers waiting to be rendered while the page opens (health check 2026-11-12, performance-2).</summary>
    private readonly List<AnswerView> _deferred = [];
    private bool _opening = true;
    private TaskCompletionSource _rendered = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    /// <summary>The page's own modifier for shortcuts: ⌘ on macOS, Ctrl elsewhere.</summary>
    private static readonly Avalonia.Input.KeyModifiers Command = OperatingSystem.IsMacOS() ? Avalonia.Input.KeyModifiers.Meta : Avalonia.Input.KeyModifiers.Control;

    /// <summary>⌘/Ctrl+Shift+B: a branch from the answer text selected on this page (health check 2026-11-12, a11y-2).</summary>
    private void OnBranchShortcut(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.B || e.KeyModifiers != (Command | Avalonia.Input.KeyModifiers.Shift))
        {
            return;
        }

        foreach (var answer in this.GetVisualDescendants().OfType<AnswerView>())
        {
            if (answer.TryCreateBranchFromSelection())
            {
                e.Handled = true;
                return;
            }
        }
    }

    /// <summary>A streaming answer's selection, kept while its row is replaced by the stored one (see <see cref="AnswerView"/>).</summary>
    internal void HandOverSelection(MessageId answer, CarriedSelection selection) => _handedOver[answer] = selection;

    /// <summary>The selection handed over for <paramref name="answer"/>, once; null when there is none.</summary>
    internal CarriedSelection? TakeOverSelection(MessageId answer) => _handedOver.Remove(answer, out var selection) ? selection : null;

    /// <summary>Completes once the page has been placed at its source, reading position or end; tests await it.</summary>
    internal Task Restored => _restored.Task;

    /// <summary>A scroll is waiting to be recorded as the reading position (recorded once scrolling has been still for a moment, or on leaving).</summary>
    internal bool IsPositionPending => _capture.IsEnabled;

    /// <summary>When the page was placed (<see cref="System.Diagnostics.Stopwatch"/> ticks); for the open benchmark.</summary>
    internal long RestoredTimestamp { get; private set; }

    /// <summary>The longest single step of rendering the remaining answers, in ticks; for the open benchmark.</summary>
    internal long LongestRenderStep { get; private set; }

    /// <summary>Completes once every answer on the page is rendered (the ones away from the reading position come after <see cref="Restored"/>).</summary>
    internal Task Rendered => _rendered.Task;

    /// <summary>
    /// While the page opens, finished answers wait to be rendered: opening used to lay out every answer of the
    /// conversation at once on the UI thread, about 1.3 s for 60 answers of 2,500 characters on macOS (health check
    /// 2026-11-12, performance-2). The answers around the reading position are rendered before the page is placed, the
    /// rest one at a time afterwards, nearest first.
    /// Only rows made before the page is placed wait; one made afterwards (an answer that just finished) renders at once.
    internal bool DefersRendering => _opening && _restoring;

    internal void Defer(AnswerView answer) => _deferred.Add(answer);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_page is not null)
        {
            _page.IsShown = false;
        }

        _page = DataContext as ConversationViewModel;
        _handedOver.Clear();
        UpdateShown();

        _restoring = true;
        if (_restored.Task.IsCompleted)
        {
            _restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _opening = true;
        _deferred.Clear();
        if (_rendered.Task.IsCompleted)
        {
            _rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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

            // On the window, as the sidebar's ⌘/Ctrl+B is: the shortcut works wherever the focus is while this page is shown.
            _window.AddHandler(KeyDownEvent, OnBranchShortcut, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }

        UpdateShown();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Leaving the page (another page opens, the window closes) within the capture delay of a scroll: record where
        // the reader is now instead of dropping it, or the page reopens where it was before that scroll. The rows are
        // still laid out here (BranchUiTests.A_page_left_right_after_a_scroll_reopens_where_it_was_left).
        if (_capture.IsEnabled)
        {
            CapturePosition();
        }

        // Off screen: the running answer's live counter stops ticking until the page is shown again.
        if (_window is not null)
        {
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window.RemoveHandler(KeyDownEvent, OnBranchShortcut);
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
        var source = page.TakeSourceFocus();
        RenderAround(page, source?.SourceMessage ?? position?.Message);
        if (source is not null)
        {
            RevealSource(page, source);
        }
        else
        {
            ApplyPosition(page, position);
        }

        _followBottom = IsAtBottom();
        _restoring = false;
        RestoredTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        _restored.TrySetResult();
        Dispatcher.UIThread.Post(() => RenderNext(page), DispatcherPriority.Background);
    }

    /// <summary>
    /// Renders the answer the page is placed at (the end when none) and its neighbours, until about two screens around it
    /// are real text: positions are measured on rendered answers only.
    /// </summary>
    private void RenderAround(ConversationViewModel page, MessageId? target)
    {
        var index = target is { } id ? page.Messages.ToList().FindIndex(m => m.Id == id) : -1;
        if (index < 0)
        {
            index = page.Messages.Count - 1;
        }

        var screen = Math.Max(Scroller.Viewport.Height, 400);
        double below = 0, above = 0;
        RenderRow(index);
        for (var step = 1; step < page.Messages.Count && (below < screen * 1.5 || above < screen); step++)
        {
            if (below < screen * 1.5 && index + step < page.Messages.Count)
            {
                below += RenderRow(index + step);
            }

            if (above < screen && index - step >= 0)
            {
                above += RenderRow(index - step);
            }
        }

        UpdateLayout();
    }

    /// <summary>Renders the answer in row <paramref name="index"/>, if it waits; returns the row's height before (its estimate).</summary>
    private double RenderRow(int index)
    {
        if (MessageList.ContainerFromIndex(index) is not { } container)
        {
            return 0;
        }

        var height = container.Bounds.Height;
        foreach (var answer in container.GetVisualDescendants().OfType<AnswerView>())
        {
            answer.RenderNow();
            _deferred.Remove(answer);
        }

        return height;
    }

    /// <summary>
    /// Renders the waiting answer nearest the reading position, then gives the UI thread back. A row above the reading
    /// line grows or shrinks as it renders, so the page is moved by the same amount: what the reader sees stays put.
    /// </summary>
    private void RenderNext(ConversationViewModel page)
    {
        if (!ReferenceEquals(page, _page) || !this.IsAttachedToVisualTree())
        {
            return;
        }

        _deferred.RemoveAll(a => !a.IsAttachedToVisualTree() || a.IsRendered);
        if (_deferred.Count == 0)
        {
            _opening = false;
            _rendered.TrySetResult();
            return;
        }

        var (anchorIndex, anchor) = ReadingRow(page);
        var next = _deferred.MinBy(a => Math.Abs(RowOf(a) - anchorIndex))!;
        var row = RowOf(next);
        var before = anchor is null ? null : TopOf(anchor);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        next.RenderNow();
        _deferred.Remove(next);
        UpdateLayout();
        if (!_followBottom && row < anchorIndex && before is { } was && anchor is not null && TopOf(anchor) is { } now)
        {
            ScrollBy(now - was);
        }

        LongestRenderStep = Math.Max(LongestRenderStep, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        Dispatcher.UIThread.Post(() => RenderNext(page), DispatcherPriority.Background);
    }

    /// <summary>The row at the reading line and its container (the last row when the page ends above it).</summary>
    private (int Index, Control? Container) ReadingRow(ConversationViewModel page)
    {
        for (var i = 0; i < page.Messages.Count; i++)
        {
            if (MessageList.ContainerFromIndex(i) is { } container && TopOf(container) is { } top && top + container.Bounds.Height > ReadingLine)
            {
                return (i, container);
            }
        }

        return (page.Messages.Count - 1, null);
    }

    private int RowOf(AnswerView answer)
    {
        for (Visual? visual = answer; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Control control && MessageList.IndexFromContainer(control) is var index and >= 0)
            {
                return index;
            }
        }

        return int.MaxValue / 2;
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
            var answer = item.IsAnswer ? container.GetVisualDescendants().OfType<AnswerView>().FirstOrDefault(a => a.IsRendered) : null;
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
