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
/// precedence: the source passage of a branch (return to source), the slot's reading position, or the start of the latest
/// answer.
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
    private bool _restoring = true;

    /// <summary>The answer whose turn the page leaves room for (<see cref="KeepRoomForTurn"/>); null when none was placed.</summary>
    private MessageId? _turn;

    /// <summary>What is at the reading line, and where it is within the list: kept there while other content changes size.</summary>
    private (MessageId Message, bool InText, double Top, double Width)? _held;

    /// <summary>The reading position as last captured: put back when the text is rewrapped to another width.</summary>
    private ReadingPosition? _reading;

    /// <summary>The page has scrolled since <see cref="_reading"/> was captured: it no longer says where the reader is.</summary>
    private bool _scrolledSince;

    /// <summary>Answers waiting to be rendered while the page opens (health check 2026-11-12, performance-2).</summary>
    private readonly List<AnswerView> _deferred = [];
    private bool _opening = true;
    private TaskCompletionSource _rendered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConversationView()
    {
        InitializeComponent();
        _capture = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => CapturePosition());

        // An answer being written never moves the page (the owner, 2026-11-23): sending places its question once
        // (PlaceTurn), and from then on only the reader scrolls. What is read is kept where it is while other content
        // changes size (HoldReadingPlace).
        Scroller.ScrollChanged += (_, e) =>
        {
            if (_restoring)
            {
                return;
            }

            _scrolledSince |= e.OffsetDelta.Y != 0;
            _capture.Stop();
            _capture.Start();
        };
        MessageList.LayoutUpdated += (_, _) => OnListLaidOut();
        BranchMarks.Attach(Scroller, MessageList);
        AttachTextColumn();
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
            _page.PropertyChanged -= OnPageChanged;
        }

        _page = DataContext as ConversationViewModel;
        if (_page is not null)
        {
            _page.PropertyChanged += OnPageChanged;
        }

        FollowTextWidth(_page?.TextWidth);
        _turn = null;
        _held = null;
        _reading = null;
        MessageList.MinHeight = 0;
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

        // An answer still being written when the page opens has its room already, so a position at its question can be restored.
        _turn = page.RunningAnswer?.Id;
        KeepRoomForTurn();
        if (source is not null)
        {
            RevealSource(page, source);
        }
        else
        {
            ApplyPosition(page, position);
        }

        _restoring = false;
        RememberReadingPlace();
        _reading = MeasurePosition(page);
        _scrolledSince = false;
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
    /// line grows or shrinks as it renders; what the reader sees stays put (<see cref="HoldReadingPlace"/>).
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

        var anchorIndex = ReadingRow(page);
        var next = _deferred.MinBy(a => Math.Abs(RowOf(a) - anchorIndex))!;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        next.RenderNow();
        _deferred.Remove(next);
        UpdateLayout();
        LongestRenderStep = Math.Max(LongestRenderStep, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        Dispatcher.UIThread.Post(() => RenderNext(page), DispatcherPriority.Background);
    }

    /// <summary>The row at the reading line (the last row when the page ends above it).</summary>
    private int ReadingRow(ConversationViewModel page)
    {
        for (var i = 0; i < page.Messages.Count; i++)
        {
            if (MessageList.ContainerFromIndex(i) is { } container && TopOf(container) is { } top && top + container.Bounds.Height > ReadingLine)
            {
                return i;
            }
        }

        return page.Messages.Count - 1;
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

    /// <summary>
    /// Restores a reading position: same character of the same source block, else the message, else the end. With no
    /// position (a conversation never read here, such as one opened from its new-answer hint), the latest answer is read
    /// from its start (<see cref="StartOf"/>), not from its end.
    /// </summary>
    private void ApplyPosition(ConversationViewModel page, ReadingPosition? position)
    {
        if (position is null && StartOf(page, LatestAnswer(page)) is { } start && TopOf(start) is { } startTop)
        {
            ScrollBy(startTop - ReadingLine);
            return;
        }

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

    private void OnPageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationViewModel.HasBranches))
        {
            ArrangeTextColumn();
        }

        if (e.PropertyName == nameof(ConversationViewModel.RunningAnswer) && !_restoring && _page is { RunningAnswer: { } running } page && running.Id != _turn)
        {
            PlaceTurn(page, running.Id);
        }
    }

    /// <summary>
    /// An answer started on this page (a question sent, a retry): its start (<see cref="StartOf"/>) goes to the top of the
    /// page, once, wherever the reader was, with room below for the answer to grow into.
    /// </summary>
    private void PlaceTurn(ConversationViewModel page, MessageId answer)
    {
        UpdateLayout();
        _turn = answer;
        KeepRoomForTurn();
        UpdateLayout();
        if (TurnStart(page) is { } start && TopOf(start) is { } top)
        {
            ScrollBy(top - ReadingLine);
        }

        RememberReadingPlace();
    }

    private Control? TurnStart(ConversationViewModel page) =>
        _turn is { } turn ? StartOf(page, page.Messages.ToList().FindIndex(m => m.Id == turn)) : null;

    /// <summary>
    /// The list is at least tall enough for the start of the latest turn to be at the top of the page: a short answer
    /// leaves the rest of the page empty, and one that grows, shrinks or ends does not move its question.
    /// </summary>
    /// <returns>Whether the room changed: the list is laid out again before anything is measured against it.</returns>
    private bool KeepRoomForTurn()
    {
        if (_page is { } page && TurnStart(page) is { } start && TopInList(start) is { } top)
        {
            var room = Math.Max(0, top - ReadingLine + Scroller.Viewport.Height - MessageList.Margin.Top - MessageList.Margin.Bottom);
            if (Math.Abs(room - MessageList.MinHeight) > 0.5)
            {
                MessageList.MinHeight = room;
                return true;
            }
        }

        return false;
    }

    private void OnListLaidOut()
    {
        if (_restoring || _page is null || !this.IsAttachedToVisualTree())
        {
            return;
        }

        if (_held is { } held && held.Width != MessageList.Bounds.Width)
        {
            // Once per rewrap: the width is the new one from here on.
            _held = null;
            Dispatcher.UIThread.Post(KeepReadingThrough, DispatcherPriority.Loaded);
        }

        if (KeepRoomForTurn())
        {
            return;
        }

        HoldReadingPlace();
        RememberReadingPlace();
    }

    /// <summary>
    /// What is at the reading line stays there while content above it comes, goes or changes size (the reasoning fold
    /// going when an answer ends, a step added above the text, an answer above rendered). A rewrap to another width is not
    /// that: the text itself has moved, and <see cref="KeepReadingThrough"/> handles it.
    /// </summary>
    private void HoldReadingPlace()
    {
        if (_held is not { } held || held.Width != MessageList.Bounds.Width || Anchor(held.Message, held.InText) is not { } anchor || TopInList(anchor) is not { } top)
        {
            return;
        }

        if (Math.Abs(top - held.Top) > 0.5)
        {
            Scroller.Offset = new Vector(0, Math.Max(0, Scroller.Offset.Y + top - held.Top));
        }
    }

    /// <summary>
    /// The text was rewrapped to another width (the window or the text width changed): the sentence at the reading line stays
    /// there. A reader who has scrolled since it was captured, or scrolls before this runs, is never pulled back to it.
    /// </summary>
    private void KeepReadingThrough()
    {
        if (!_restoring && !_scrolledSince && _page is { } page && _reading is { } reading && this.IsAttachedToVisualTree())
        {
            UpdateLayout();
            ApplyPosition(page, reading);
            _scrolledSince = false;
            RememberReadingPlace();
        }
    }

    private void RememberReadingPlace()
    {
        if (_restoring || _page is not { } page)
        {
            return;
        }

        _held = null;
        for (var i = 0; i < page.Messages.Count; i++)
        {
            if (MessageList.ContainerFromIndex(i) is not { } container || TopOf(container) is not { } top || top + container.Bounds.Height <= ReadingLine)
            {
                continue;
            }

            var message = page.Messages[i].Id;
            var text = page.Messages[i].IsAnswer ? Anchor(message, inText: true) : null;
            var inText = text is not null && TopOf(text) is { } textTop && textTop <= ReadingLine;
            if (TopInList(inText ? text! : container) is { } inList)
            {
                _held = (message, inText, inList, MessageList.Bounds.Width);
            }

            return;
        }
    }

    /// <summary>The row of <paramref name="message"/>, or the answer's text within it.</summary>
    private Control? Anchor(MessageId message, bool inText)
    {
        if (_page is not { } page)
        {
            return null;
        }

        for (var i = 0; i < page.Messages.Count; i++)
        {
            if (page.Messages[i].Id == message)
            {
                var container = MessageList.ContainerFromIndex(i);
                return inText ? container?.GetVisualDescendants().OfType<AnswerView>().FirstOrDefault() : container;
            }
        }

        return null;
    }

    private double? TopInList(Visual visual) => visual.TranslatePoint(default, MessageList)?.Y;

    private static int LatestAnswer(ConversationViewModel page)
    {
        for (var i = page.Messages.Count - 1; i >= 0; i--)
        {
            if (page.Messages[i].IsAnswer)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Where new content in row <paramref name="answer"/> is read from: its question when that is short (a third of the
    /// page at most), so the reader sees what was asked with the first sentence of the answer below it; else the answer.
    /// </summary>
    private Control? StartOf(ConversationViewModel page, int answer)
    {
        if (answer < 0 || MessageList.ContainerFromIndex(answer) is not { } container)
        {
            return null;
        }

        return answer > 0 && page.Messages[answer - 1].IsUser && MessageList.ContainerFromIndex(answer - 1) is { } question
            && question.Bounds.Height <= Scroller.Viewport.Height / 3
            ? question
            : container;
    }

    private void CapturePosition()
    {
        _capture.Stop();
        if (!_restoring && _page is { } page && MeasurePosition(page) is { } position)
        {
            _reading = position;
            _scrolledSince = false;
            page.ReportPosition(position);
        }
    }

    /// <summary>What is at the reading line now; null when there is nothing to read.</summary>
    private ReadingPosition? MeasurePosition(ConversationViewModel page)
    {
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
            return new ReadingPosition(item.Id, 0, SourceAnchors.Serialize(locator), SourceAnchors.LocatorVersion);
        }

        return null;
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
}
