using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Conversations;
using Mindweave.Application.Notifications;
using Mindweave.Application.Preferences;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels;

/// <summary>How the pop-up hints for other workspaces behave; tests give their own clock and duration.</summary>
public sealed record WorkspaceNewsOptions
{
    public static readonly TimeSpan DefaultToastDuration = TimeSpan.FromSeconds(8);

    /// <summary>A hint closes itself after this long unless the pointer is on it.</summary>
    public TimeSpan ToastDuration { get; init; } = DefaultToastDuration;

    /// <summary>At most this many hints at once (one per workspace); a further workspace pushes out the oldest.</summary>
    public int MaxToasts { get; init; } = 3;

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// New answers in the workspaces that are not open (2026-11-10). The sidebar marks conversations of the open workspace
/// only; the workspace picker shows a badge with how many conversations elsewhere have one, a dot beside each such
/// workspace in its list, and its tooltip names them. An answer that ends in another workspace while the application
/// runs also pops up a hint (<see cref="Toasts"/>; the user's feedback the same day: the dot alone was too easy to miss),
/// with the system's alert sound when the user chose it. The open workspace never counts on the badge: its own sidebar
/// already shows its marks. An answer that ends in a conversation of the open workspace that is not on screen pops up a
/// hint for that conversation too, unless the user turned that off (accepted the same day).
/// </summary>
public sealed partial class WorkspaceNewsViewModel : ObservableObject, IDisposable
{
    private readonly Workbench _workbench;
    private readonly IConversationMarks _marks;
    private readonly IUiDispatcher _dispatcher;
    private readonly INewsAlerts? _alerts;
    private readonly IAlertSound? _sound;
    private readonly IConversations? _conversations;
    private readonly WorkspaceNewsOptions _options;
    private readonly SerialRefresh _load;
    private readonly UiThrottle _reload;

    /// <summary>
    /// Conversations whose answer just ended off screen, with the workspace open then and whether that answer made the
    /// conversation unread; taken by the next refresh.
    /// </summary>
    private readonly List<(ConversationId Conversation, WorkspaceId? OpenThen, bool NewlyUnread)> _justMarked = [];

    /// <summary>Marked just now and not yet announced as answered (any thread, under its own lock).</summary>
    private readonly HashSet<ConversationId> _newlyUnread = [];

    /// <summary>A refresh is running (UI thread only).</summary>
    private bool _refreshing;

    /// <summary>The open workspace, readable from the thread an answer ends on (set on the UI thread).</summary>
    private volatile Workspace? _open;

    private int _marksNoted;

    public WorkspaceNewsViewModel(
        Workbench workbench,
        IConversationMarks marks,
        IUiDispatcher dispatcher,
        INewsAlerts? alerts = null,
        IAlertSound? sound = null,
        WorkspaceNewsOptions? options = null,
        IConversations? conversations = null)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _workbench = workbench;
        _marks = marks;
        _dispatcher = dispatcher;
        _alerts = alerts;
        _sound = sound;
        _conversations = conversations;
        _options = options ?? new WorkspaceNewsOptions();
        _open = workbench.CurrentWorkspace;
        _load = new SerialRefresh(RefreshAsync);
        _reload = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(100), () => Loaded = _load.Request());
        _marks.Marked += OnMarked;
        _marks.Answered += OnAnswered;
        _marks.Changed += OnMarksChanged;
        if (conversations is not null)
        {
            conversations.ConversationChanged += OnConversationChanged;
        }

        _workbench.Workspaces.CollectionChanged += OnWorkspacesChanged;
        _workbench.PropertyChanged += OnWorkbenchChanged;
        Loaded = _load.Request();
    }

    /// <summary>Stops listening and stops the hints' timers; the view it belongs to is being replaced.</summary>
    public void Dispose()
    {
        _marks.Marked -= OnMarked;
        _marks.Answered -= OnAnswered;
        _marks.Changed -= OnMarksChanged;
        if (_conversations is not null)
        {
            _conversations.ConversationChanged -= OnConversationChanged;
        }

        _workbench.Workspaces.CollectionChanged -= OnWorkspacesChanged;
        _workbench.PropertyChanged -= OnWorkbenchChanged;
        foreach (var toast in Toasts)
        {
            StopTimer(toast);
        }
    }

    /// <summary>The workspaces other than the open one with a new answer; replaced, never changed in place, so bindings see it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNews), nameof(Hint))]
    public partial IReadOnlySet<WorkspaceId> Workspaces { get; private set; } = new HashSet<WorkspaceId>();

    /// <summary>How many conversations in the other workspaces have a new answer: the closed picker's badge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeText))]
    public partial int Count { get; private set; }

    public string BadgeText => UiText.NewsBadge(Count);

    /// <summary>
    /// The count grew because answers just ended in other workspaces (the ones that pop up a hint): the badge pulses once.
    /// Not for marks restored at startup, nor when the count grows only because the workspace just left now counts as
    /// another one.
    /// </summary>
    public event EventHandler? CountRose;

    public bool HasNews => Workspaces.Count > 0;

    /// <summary>The closed picker's tooltip and automation help: which workspaces have a new answer, in the picker's order.</summary>
    public string? Hint => HasNews
        ? UiText.OtherWorkspacesNews(_workbench.Workspaces.Where(w => Workspaces.Contains(w.Id)).Select(w => w.Name.Value))
        : null;

    /// <summary>The pop-up hints, oldest first; never more than <see cref="WorkspaceNewsOptions.MaxToasts"/>.</summary>
    public ObservableCollection<WorkspaceToastViewModel> Toasts { get; } = [];

    /// <summary>Completes when the newest refresh has run (tests).</summary>
    public Task Loaded { get; private set; }

    /// <summary>How many answers that just ended off screen have been noted with the workspace open then (tests).</summary>
    internal int MarksNoted => Volatile.Read(ref _marksNoted);

    /// <summary>The last check of the sound setting and the sound it played, if any (tests).</summary>
    internal Task Alerting { get; private set; } = Task.CompletedTask;

    /// <summary>The pointer is on the hint (true) or left it (false): it stays while held, and gets its full time again after.</summary>
    public void Hold(WorkspaceToastViewModel toast, bool held)
    {
        ArgumentNullException.ThrowIfNull(toast);
        if (toast.IsHeld == held || !Toasts.Contains(toast))
        {
            return;
        }

        toast.IsHeld = held;
        StartTimer(toast);
    }

    /// <summary>Takes the hint away (its × or its timer); the badge and the dots stay.</summary>
    public void Dismiss(WorkspaceToastViewModel toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        StopTimer(toast);
        Toasts.Remove(toast);
    }

    /// <summary>Any thread: raised just before <see cref="OnAnswered"/> when the answer made the conversation unread.</summary>
    private void OnMarked(object? sender, ConversationId conversation)
    {
        lock (_newlyUnread)
        {
            _newlyUnread.Add(conversation);
        }
    }

    private void OnAnswered(object? sender, ConversationId conversation)
    {
        // Any thread, as the answer ends: what is open is taken now, not when the UI thread gets to it (the user may have
        // switched meanwhile, and then saw that workspace when the answer ended). Counted on the UI thread.
        var openThen = _open?.Id;
        bool newlyUnread;
        lock (_newlyUnread)
        {
            newlyUnread = _newlyUnread.Remove(conversation);
        }

        Interlocked.Increment(ref _marksNoted);
        _dispatcher.Post(() =>
        {
            _justMarked.Add((conversation, openThen, newlyUnread));
            _reload.Request();
        });
    }

    private void OnWorkspacesChanged(object? sender, NotifyCollectionChangedEventArgs e) => _reload.Request();

    private void OnMarksChanged(object? sender, EventArgs e) => _reload.Request();

    /// <summary>
    /// A conversation's hint follows its title (renamed, or named automatically after its first answer), and goes when it
    /// is archived or deleted.
    /// </summary>
    private void OnConversationChanged(object? sender, ConversationId changed) => _dispatcher.Post(() => ReloadIfShown(changed));

    /// <summary>
    /// On the UI thread: a conversation with a hint up changed (renamed, archived): the hint follows. During a refresh any
    /// change counts, since the hint it is making may have read the title just before.
    /// </summary>
    private void ReloadIfShown(ConversationId changed)
    {
        if (_refreshing || Toasts.Any(t => t.Conversation == changed))
        {
            _reload.Request();
        }
    }

    private void OnWorkbenchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Workbench.CurrentWorkspace))
        {
            _open = _workbench.CurrentWorkspace;

            // However the workspace was opened, its hint has done its job.
            if (_workbench.CurrentWorkspace is { } open && Toasts.FirstOrDefault(t => t.Conversation is null && t.Workspace == open.Id) is { } shown)
            {
                Dismiss(shown);
            }

            _reload.Request();
        }
        else if (e.PropertyName == nameof(Workbench.CurrentPage)
            && OnScreen() is { } conversation
            && Toasts.FirstOrDefault(t => t.Conversation == conversation) is { } hint)
        {
            // However the conversation was opened (its hint, the sidebar, a link), its hint goes as it shows.
            Dismiss(hint);
        }
    }

    private ConversationId? OnScreen() => (_workbench.CurrentPage?.Route as ConversationRoute)?.Id;

    private async Task RefreshAsync()
    {
        _refreshing = true;
        try
        {
            await RefreshCoreAsync();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshCoreAsync()
    {
        // Taken before the marks are read, so every conversation noted here is among them unless it was seen since.
        List<(ConversationId Conversation, WorkspaceId? OpenThen, bool NewlyUnread)> justMarked = [.. _justMarked];
        _justMarked.Clear();
        var current = _workbench.CurrentWorkspace?.Id;
        WorkspaceId[] others = [.. _workbench.Workspaces.Select(w => w.Id).Where(id => id != current)];
        IReadOnlyDictionary<WorkspaceId, IReadOnlyList<ConversationId>> found;
        IReadOnlyDictionary<ConversationId, (WorkspaceId Workspace, string Title)> titles;
        bool inWorkspace;
        try
        {
            found = others.Length == 0
                ? new Dictionary<WorkspaceId, IReadOnlyList<ConversationId>>()
                : await _marks.UnreadByWorkspaceAsync(others, CancellationToken.None);

            // For the conversations' hints: those up already, and the answers that may have ended in the workspace open
            // then (not those just found in a different one).
            WorkspaceId[] openThen = [.. justMarked
                .Where(m => m.OpenThen is { } then && !found.Any(p => p.Key != then && p.Value.Contains(m.Conversation)))
                .Select(m => m.OpenThen!.Value)];
            inWorkspace = openThen.Length > 0 && await InWorkspaceAsync();
            titles = await TitlesAsync(Toasts.Where(t => t.Conversation is not null).Select(t => t.Workspace).Concat(inWorkspace ? openThen : []));
        }
#pragma warning disable CA1031 // A hint is never worth an error: keep the last one shown.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            return;
        }

        // The open workspace may have changed while reading: it never gets the hint.
        var open = _workbench.CurrentWorkspace?.Id;
        var shown = found.Where(p => p.Key != open && _workbench.Workspaces.Any(w => w.Id == p.Key)).ToDictionary(p => p.Key, p => p.Value);
        var workspaces = shown.Keys.ToHashSet();
        if (!workspaces.SetEquals(Workspaces))
        {
            Workspaces = workspaces;
        }
        else
        {
            OnPropertyChanged(nameof(Hint));
        }

        Count = shown.Values.Sum(c => c.Count);
        UpdateToasts(shown, titles);

        // A workspace's hint pops up only for an answer that ended here, in a workspace that was not open then and is not now.
        var alertedMarks = justMarked
            .Select(m => (m.OpenThen, m.NewlyUnread, Workspace: shown.Where(p => p.Value.Contains(m.Conversation)).Select(p => (WorkspaceId?)p.Key).FirstOrDefault()))
            .Where(m => m.Workspace is not null && m.Workspace != m.OpenThen)
            .ToList();
        var alerted = alertedMarks.Select(m => m.Workspace!.Value).Distinct().ToList();

        // The count rose because of these answers when one of them made its conversation unread. Not judged by comparing
        // counts: another refresh may already have counted the conversation, between its mark and this answer's notice.
        var rose = alertedMarks.Any(m => m.NewlyUnread);
        foreach (var workspace in alerted)
        {
            ShowToast(workspace, shown[workspace]);
        }

        // A conversation's hint, for an answer that ended in the workspace open then, unless the user has seen it since.
        var onScreen = OnScreen();
        var answered = justMarked
            .Where(m => inWorkspace
                && m.OpenThen is { } then
                && titles.TryGetValue(m.Conversation, out var listed) && listed.Workspace == then
                && m.Conversation != onScreen
                && _marks.IsUnread(m.Conversation))
            .Select(m => m.Conversation)
            .Distinct()
            .ToList();
        foreach (var conversation in answered)
        {
            ShowToast(conversation, titles[conversation]);
        }

        if (alerted.Count > 0 || answered.Count > 0)
        {
            // One sound for everything that popped up together.
            Alerting = PlaySoundAsync();
            if (alerted.Count > 0 && rose)
            {
                CountRose?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Hints already up follow what they show. A workspace's follows the counts and closes when its workspace has nothing
    /// new left (or is gone); a conversation's follows the sidebar's title and closes once the conversation has been seen,
    /// is on screen, or was archived or deleted.
    /// </summary>
    private void UpdateToasts(
        Dictionary<WorkspaceId, IReadOnlyList<ConversationId>> shown,
        IReadOnlyDictionary<ConversationId, (WorkspaceId Workspace, string Title)> titles)
    {
        var onScreen = OnScreen();
        foreach (var toast in Toasts.ToList())
        {
            if (toast.Conversation is { } conversation)
            {
                if (conversation == onScreen || !_marks.IsUnread(conversation) || !titles.TryGetValue(conversation, out var listed))
                {
                    Dismiss(toast);
                }
                else
                {
                    toast.Name = listed.Title;
                }

                continue;
            }

            if (!shown.TryGetValue(toast.Workspace, out var conversations))
            {
                Dismiss(toast);
                continue;
            }

            toast.Conversations = conversations;
            if (_workbench.Workspaces.FirstOrDefault(w => w.Id == toast.Workspace) is { } workspace)
            {
                toast.Name = workspace.Name.Value;
            }
        }
    }

    /// <summary>A new hint for the workspace, or its hint again with its new count and its full time.</summary>
    private void ShowToast(WorkspaceId workspace, IReadOnlyList<ConversationId> conversations)
    {
        var toast = Toasts.FirstOrDefault(t => t.Conversation is null && t.Workspace == workspace)
            ?? Add(new WorkspaceToastViewModel(workspace, _workbench.Workspaces.First(w => w.Id == workspace).Name.Value, conversations, Dismiss));
        toast.Conversations = conversations;
        StartTimer(toast);
    }

    /// <summary>A new hint for the conversation, or its hint again with its title as now shown and its full time.</summary>
    private void ShowToast(ConversationId conversation, (WorkspaceId Workspace, string Title) listed)
    {
        var toast = Toasts.FirstOrDefault(t => t.Conversation == conversation)
            ?? Add(new WorkspaceToastViewModel(listed.Workspace, conversation, listed.Title, Dismiss));
        toast.Name = listed.Title;
        StartTimer(toast);
    }

    /// <summary>Adds a hint below the others; at most <see cref="WorkspaceNewsOptions.MaxToasts"/> of both kinds, the oldest goes.</summary>
    private WorkspaceToastViewModel Add(WorkspaceToastViewModel toast)
    {
        while (Toasts.Count >= Math.Max(1, _options.MaxToasts))
        {
            Dismiss(Toasts[0]);
        }

        Toasts.Add(toast);
        return toast;
    }

    /// <summary>Each listed conversation of these workspaces, with its workspace and its title as the sidebar shows it.</summary>
    private async Task<IReadOnlyDictionary<ConversationId, (WorkspaceId Workspace, string Title)>> TitlesAsync(IEnumerable<WorkspaceId> workspaces)
    {
        var titles = new Dictionary<ConversationId, (WorkspaceId, string)>();
        if (_conversations is null)
        {
            return titles;
        }

        foreach (var workspace in workspaces.Distinct().ToList())
        {
            foreach (var summary in await _conversations.ListAsync(workspace, CancellationToken.None))
            {
                if (!summary.IsHidden)
                {
                    titles[summary.Conversation.Id] = (workspace, UiText.ConversationTitle(summary.Conversation, summary.FirstQuestion, summary.Quote));
                }
            }
        }

        return titles;
    }

    /// <summary>Whether the open workspace's conversations pop up hints: on by default, and when the setting cannot be read.</summary>
    private async Task<bool> InWorkspaceAsync()
    {
        if (_alerts is null)
        {
            return true;
        }

        try
        {
            return await _alerts.GetInWorkspaceAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // Unreadable means the default.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            return true;
        }
    }

    private void StartTimer(WorkspaceToastViewModel toast)
    {
        StopTimer(toast);
        if (toast.IsHeld)
        {
            return;
        }

        var generation = toast.Generation;
        toast.Timer = _options.Time.CreateTimer(
            _ => _dispatcher.Post(() =>
            {
                if (toast.Generation == generation && Toasts.Contains(toast))
                {
                    Dismiss(toast);
                }
            }),
            null,
            _options.ToastDuration,
            Timeout.InfiniteTimeSpan);
    }

    private static void StopTimer(WorkspaceToastViewModel toast)
    {
        toast.Generation++;
        toast.Timer?.Dispose();
        toast.Timer = null;
    }

    /// <summary>
    /// Once each time hints pop up (or come up again), when the user chose the sound; hints that pop up together sound
    /// once, not once each. An unreadable setting means none.
    /// </summary>
    private async Task PlaySoundAsync()
    {
        if (_alerts is null || _sound is null)
        {
            return;
        }

        bool on;
        try
        {
            on = await _alerts.GetSoundAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // The visible hint is up already; without the setting it stays silent.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            return;
        }

        if (on)
        {
            _sound.Play();
        }
    }
}
