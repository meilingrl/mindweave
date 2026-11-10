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
/// with the system's alert sound when the user chose it. The open workspace never counts here: its own sidebar already
/// shows its marks.
/// </summary>
public sealed partial class WorkspaceNewsViewModel : ObservableObject
{
    private readonly Workbench _workbench;
    private readonly IConversationMarks _marks;
    private readonly IUiDispatcher _dispatcher;
    private readonly INewsAlerts? _alerts;
    private readonly IAlertSound? _sound;
    private readonly WorkspaceNewsOptions _options;
    private readonly SerialRefresh _load;
    private readonly UiThrottle _reload;

    /// <summary>Conversations an answer just marked, with the workspace open when it ended; taken by the next refresh.</summary>
    private readonly List<(ConversationId Conversation, WorkspaceId? OpenThen)> _justMarked = [];

    /// <summary>The open workspace, readable from the thread an answer ends on (set on the UI thread).</summary>
    private volatile Workspace? _open;

    private int _marksNoted;

    public WorkspaceNewsViewModel(
        Workbench workbench,
        IConversationMarks marks,
        IUiDispatcher dispatcher,
        INewsAlerts? alerts = null,
        IAlertSound? sound = null,
        WorkspaceNewsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _workbench = workbench;
        _marks = marks;
        _dispatcher = dispatcher;
        _alerts = alerts;
        _sound = sound;
        _options = options ?? new WorkspaceNewsOptions();
        _open = workbench.CurrentWorkspace;
        _load = new SerialRefresh(RefreshAsync);
        _reload = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(100), () => Loaded = _load.Request());
        _marks.Marked += OnMarked;
        _marks.Changed += (_, _) => _reload.Request();
        _workbench.Workspaces.CollectionChanged += OnWorkspacesChanged;
        _workbench.PropertyChanged += OnWorkbenchChanged;
        Loaded = _load.Request();
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

    public bool HasNews => Workspaces.Count > 0;

    /// <summary>The closed picker's tooltip and automation help: which workspaces have a new answer, in the picker's order.</summary>
    public string? Hint => HasNews
        ? UiText.OtherWorkspacesNews(_workbench.Workspaces.Where(w => Workspaces.Contains(w.Id)).Select(w => w.Name.Value))
        : null;

    /// <summary>The pop-up hints, oldest first; never more than <see cref="WorkspaceNewsOptions.MaxToasts"/>.</summary>
    public ObservableCollection<WorkspaceToastViewModel> Toasts { get; } = [];

    /// <summary>Completes when the newest refresh has run (tests).</summary>
    public Task Loaded { get; private set; }

    /// <summary>How many marks from answers that just ended have been noted with the workspace open then (tests).</summary>
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

    private void OnMarked(object? sender, ConversationId conversation)
    {
        // Any thread, as the answer ends: what is open is taken now, not when the UI thread gets to it (the user may have
        // switched meanwhile, and then saw that workspace when the answer ended). Counted on the UI thread.
        var openThen = _open?.Id;
        Interlocked.Increment(ref _marksNoted);
        _dispatcher.Post(() =>
        {
            _justMarked.Add((conversation, openThen));
            _reload.Request();
        });
    }

    private void OnWorkspacesChanged(object? sender, NotifyCollectionChangedEventArgs e) => _reload.Request();

    private void OnWorkbenchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Workbench.CurrentWorkspace))
        {
            _open = _workbench.CurrentWorkspace;

            // However the workspace was opened, its hint has done its job.
            if (_workbench.CurrentWorkspace is { } open && Toasts.FirstOrDefault(t => t.Workspace == open.Id) is { } shown)
            {
                Dismiss(shown);
            }

            _reload.Request();
        }
    }

    private async Task RefreshAsync()
    {
        // Taken before the marks are read, so every conversation noted here is among them unless it was seen since.
        List<(ConversationId Conversation, WorkspaceId? OpenThen)> justMarked = [.. _justMarked];
        _justMarked.Clear();
        var current = _workbench.CurrentWorkspace?.Id;
        WorkspaceId[] others = [.. _workbench.Workspaces.Select(w => w.Id).Where(id => id != current)];
        IReadOnlyDictionary<WorkspaceId, IReadOnlyList<ConversationId>> found;
        try
        {
            found = others.Length == 0
                ? new Dictionary<WorkspaceId, IReadOnlyList<ConversationId>>()
                : await _marks.UnreadByWorkspaceAsync(others, CancellationToken.None);
        }
#pragma warning disable CA1031 // A hint is never worth an error: keep the last one shown.
        catch (Exception)
#pragma warning restore CA1031
        {
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
        UpdateToasts(shown);

        // A hint pops up only for an answer that ended here, in a workspace that was not open then and is not now.
        var alerted = justMarked
            .Select(m => (m.OpenThen, Workspace: shown.Where(p => p.Value.Contains(m.Conversation)).Select(p => (WorkspaceId?)p.Key).FirstOrDefault()))
            .Where(m => m.Workspace is not null && m.Workspace != m.OpenThen)
            .Select(m => m.Workspace!.Value)
            .Distinct()
            .ToList();
        foreach (var workspace in alerted)
        {
            ShowToast(workspace, shown[workspace]);
        }

        if (alerted.Count > 0)
        {
            Alerting = PlaySoundAsync();
        }
    }

    /// <summary>Hints already up follow the counts; one whose workspace has nothing new left (or is gone) closes.</summary>
    private void UpdateToasts(Dictionary<WorkspaceId, IReadOnlyList<ConversationId>> shown)
    {
        foreach (var toast in Toasts.ToList())
        {
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

    /// <summary>A new hint, or the workspace's hint again with its new count and its full time.</summary>
    private void ShowToast(WorkspaceId workspace, IReadOnlyList<ConversationId> conversations)
    {
        var toast = Toasts.FirstOrDefault(t => t.Workspace == workspace);
        if (toast is null)
        {
            var name = _workbench.Workspaces.First(w => w.Id == workspace).Name.Value;
            toast = new WorkspaceToastViewModel(workspace, name, conversations, Dismiss);
            while (Toasts.Count >= Math.Max(1, _options.MaxToasts))
            {
                Dismiss(Toasts[0]);
            }

            Toasts.Add(toast);
        }

        toast.Conversations = conversations;
        StartTimer(toast);
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
        catch (Exception)
#pragma warning restore CA1031
        {
            return;
        }

        if (on)
        {
            _sound.Play();
        }
    }
}
