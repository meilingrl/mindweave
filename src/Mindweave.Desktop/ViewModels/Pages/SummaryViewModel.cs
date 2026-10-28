using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>One row of the version history.</summary>
public sealed partial class SummaryVersionItem(SummaryVersion version, bool isCurrent, bool isCandidate, bool isRead) : ObservableObject
{
    public SummaryVersion Version { get; } = version;

    public SummaryVersionId Id => Version.Id;

    public bool IsCurrent { get; } = isCurrent;

    public bool IsCandidate { get; } = isCandidate;

    /// <summary>Saved, non-current versions can become current; the candidate is adopted instead.</summary>
    public bool CanSetCurrent => !IsCurrent && !IsCandidate;

    /// <summary>The candidate is discarded, not deleted as a version.</summary>
    public bool CanSelect => !IsCandidate;

    public string Label => UiText.VersionLabel(Version, IsCurrent, IsCandidate) + (isRead ? " · 已读" : string.Empty);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>A question the page asks before an irreversible or replacing step; <see cref="Proceed"/> runs on "confirm".</summary>
public sealed record PendingConfirmation(string Message, Func<Task> Proceed);

/// <summary>
/// A summary node's page (S4): the shown version (current by default), its read state and markers, the candidate
/// with adopt and discard, editing the current version with an autosaved draft, the history with "set current" and
/// deletion, renaming, and the way back to the source conversation. Reading progress comes from the view; a version
/// becomes read past the threshold after enough visible time in this opening (U2, MW-C30).
/// </summary>
public sealed partial class SummaryViewModel : PageViewModel, IPageFlush, IDisposable
{
    private readonly SummaryService _summaries;
    private readonly IWorkbenchNavigator _navigator;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _dispatcher;
    private readonly SerialRefresh _load;
    private readonly UiThrottle _reload;
    private readonly UiThrottle _draftSave;
    private SummaryDetail? _detail;
    private SummaryVersionId? _shownId;
    private long _shownSince;
    private double _reportedProgress;
    private bool _readCheckScheduled;
    private bool _loadingEdit;
    private bool _disposed;
    private long _draftVersion;
    private long _savedDraftVersion;

    public SummaryViewModel(SummaryRoute route, PageContext context, SummaryService summaries, IUiDispatcher dispatcher, TimeProvider time)
        : base(route)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(context);
        Node = route.Node;
        _summaries = summaries;
        _navigator = context.Navigator;
        _time = time;
        _dispatcher = dispatcher;
        _load = new SerialRefresh(LoadAsync);
        _reload = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(30), () => _ = _load.Request());
        _draftSave = new UiThrottle(dispatcher, TimeSpan.FromSeconds(1), () => _ = SaveDraftAsync());
        _summaries.Changed += OnChanged;
        Loaded = _load.Request();
    }

    public SummaryNodeId Node { get; }

    internal Task Loaded { get; }

    public ObservableCollection<SummaryVersionItem> History { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string DisplayName { get; private set; } = string.Empty;

    public override string Title => DisplayName;

    [ObservableProperty]
    public partial bool IsMissing { get; private set; }

    [ObservableProperty]
    public partial SummaryVersion? Shown { get; private set; }

    [ObservableProperty]
    public partial string ShownLabel { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShownIsCurrent { get; private set; }

    [ObservableProperty]
    public partial bool ShownIsCandidate { get; private set; }

    [ObservableProperty]
    public partial bool ShownIsRead { get; private set; }

    [ObservableProperty]
    public partial string MarkersText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasLaterContent { get; private set; }

    [ObservableProperty]
    public partial bool HasCandidate { get; private set; }

    [ObservableProperty]
    public partial string? Notice { get; private set; }

    [ObservableProperty]
    public partial PendingConfirmation? Confirmation { get; private set; }

    [ObservableProperty]
    public partial bool IsEditing { get; private set; }

    [ObservableProperty]
    public partial string EditTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditBody { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    [ObservableProperty]
    public partial string RenameText { get; set; } = string.Empty;

    /// <summary>The shown version's saved reading position, for the view to restore once per version.</summary>
    public string? ShownLocator { get; private set; }

    /// <summary>
    /// The view reports how far the shown version's body has been in view (0–1) and where the reader is. Opening
    /// alone never marks it read: progress must pass the threshold and the version must have been shown long enough.
    /// </summary>
    public async Task ReportProgressAsync(double progress, string? locator)
    {
        if (Shown is not { } version || _disposed)
        {
            return;
        }

        _reportedProgress = Math.Max(_reportedProgress, progress);
        if (ShownIsRead || _reportedProgress < SummaryRules.ReadThreshold)
        {
            await _summaries.RecordProgressAsync(Node, version.Id, progress, locator, TimeSpan.Zero, CancellationToken.None);
            return;
        }

        var visible = _time.GetElapsedTime(_shownSince);
        if (await _summaries.RecordProgressAsync(Node, version.Id, _reportedProgress, locator, visible, CancellationToken.None))
        {
            ShownIsRead = true;
        }
        else if (!_readCheckScheduled)
        {
            // Past the threshold but not yet long enough: check again once the minimum time has passed.
            _readCheckScheduled = true;
            var shownId = version.Id;
            _ = Task.Delay(SummaryRules.MinimumReadTime - visible + TimeSpan.FromMilliseconds(50), _time).ContinueWith(
                _ => _dispatcher.Post(() =>
                {
                    _readCheckScheduled = false;
                    if (_shownId == shownId)
                    {
                        _ = ReportProgressAsync(_reportedProgress, null);
                    }
                }),
                TaskScheduler.Default);
        }
    }

    [RelayCommand]
    private void ReturnToConversation()
    {
        if (_detail is { } detail)
        {
            _navigator.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(detail.Node.Conversation)));
        }
    }

    [RelayCommand]
    private Task ToggleReadAsync() =>
        Shown is { } version ? RunAsync(() => _summaries.SetReadAsync(Node, version.Id, !ShownIsRead, CancellationToken.None)) : Task.CompletedTask;

    [RelayCommand]
    private void ShowVersion(SummaryVersionId version) => Show(version);

    [RelayCommand]
    private void ShowCurrent()
    {
        if (_detail is { } detail)
        {
            Show(detail.Node.Current);
        }
    }

    [RelayCommand]
    private void ShowCandidate()
    {
        if (_detail?.Node.Pending is { } pending)
        {
            Show(pending);
        }
    }

    [RelayCommand]
    private async Task AdoptAsync()
    {
        if (_detail?.Node.Pending is not { } candidate)
        {
            return;
        }

        var check = await _summaries.CheckAdoptAsync(Node, CancellationToken.None);
        Task Adopt() => RunAsync(async () =>
        {
            await _summaries.AdoptAsync(Node, candidate, CancellationToken.None);
            _shownId = candidate;
        });

        if (check is { BaselineChanged: true })
        {
            Confirmation = new PendingConfirmation(UiText.AdoptReplaces(check.Current), Adopt);
        }
        else
        {
            await Adopt();
        }
    }

    [RelayCommand]
    private void Discard()
    {
        if (_detail?.Node.Pending is { } candidate)
        {
            Confirmation = new PendingConfirmation(UiText.DiscardCandidateConfirm, () => RunAsync(async () =>
            {
                await _summaries.DiscardAsync(Node, candidate, CancellationToken.None);
                _shownId = null;
            }));
        }
    }

    [RelayCommand]
    private Task SetCurrentAsync(SummaryVersionId version) =>
        RunAsync(async () =>
        {
            await _summaries.SetCurrentAsync(Node, version, CancellationToken.None);
            _shownId = version;
        });

    [RelayCommand]
    private void DeleteSelected()
    {
        if (_detail is not { } detail)
        {
            return;
        }

        var selected = History.Where(h => h.IsSelected && h.CanSelect).Select(h => h.Id).ToHashSet();
        if (selected.Count == 0)
        {
            return;
        }

        var next = SummaryRules.NextCurrent(detail.Saved, detail.Node.Current, selected);
        var nextVersion = next is { } id ? detail.Versions.Single(v => v.Id == id) : null;
        Confirmation = new PendingConfirmation(UiText.DeleteVersionsConfirm(selected.Count, nextVersion), () => RunAsync(async () =>
        {
            var current = await _summaries.DeleteVersionsAsync(Node, selected, CancellationToken.None);
            if (current is null)
            {
                // The last version went and the node with it; the source conversation remains.
                _navigator.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(detail.Node.Conversation)));
            }
            else if (_shownId is { } shown && selected.Contains(shown))
            {
                _shownId = current;
            }
        }));
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (Confirmation is { } pending)
        {
            Confirmation = null;
            await pending.Proceed();
        }
    }

    [RelayCommand]
    private void CancelConfirmation() => Confirmation = null;

    [RelayCommand]
    private async Task BeginEditAsync()
    {
        if (IsEditing)
        {
            return;
        }

        // Read afresh: the autosaved draft may be newer than what this page loaded.
        await _load.Request();
        if (_detail is not { } detail)
        {
            return;
        }

        // Only the current version is edited; an autosaved draft of it is picked up where it was left.
        _loadingEdit = true;
        var draft = detail.Draft is { } d && d.Baseline == detail.Node.Current ? d : null;
        EditTitle = draft?.Title ?? detail.Current.Title;
        EditBody = draft?.Body ?? detail.Current.Body;
        _loadingEdit = false;
        Show(detail.Node.Current);
        IsEditing = true;
    }

    [RelayCommand]
    private Task SaveEditAsync() =>
        _detail is { } detail
            ? RunAsync(async () =>
            {
                var saved = await _summaries.SaveEditAsync(Node, detail.Node.Current, EditTitle, EditBody, CancellationToken.None);
                IsEditing = false;
                _shownId = saved ?? detail.Node.Current;
            })
            : Task.CompletedTask;

    [RelayCommand]
    private Task DiscardEditAsync() =>
        RunAsync(async () =>
        {
            IsEditing = false;
            await _summaries.DiscardDraftAsync(Node, CancellationToken.None);
        });

    [RelayCommand]
    private void BeginRename()
    {
        RenameText = DisplayName;
        IsRenaming = true;
    }

    [RelayCommand]
    private Task CommitRenameAsync()
    {
        IsRenaming = false;
        return string.IsNullOrWhiteSpace(RenameText) || RenameText.Trim() == DisplayName
            ? Task.CompletedTask
            : RunAsync(() => _summaries.RenameAsync(Node, RenameText, CancellationToken.None));
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    partial void OnEditTitleChanged(string value) => RequestDraftSave();

    partial void OnEditBodyChanged(string value) => RequestDraftSave();

    /// <summary>
    /// Saves an edit draft still waiting in the one-second throttle. The workbench calls it when the page is dropped
    /// and on exit, before the window closes; a failed save keeps the draft pending for the next try.
    /// </summary>
    public Task<bool> FlushAsync() =>
        IsEditing && Volatile.Read(ref _draftVersion) != Volatile.Read(ref _savedDraftVersion) ? SaveDraftAsync() : Task.FromResult(true);

    /// <summary>The workbench flushes the draft before disposing (see <see cref="FlushAsync"/>).</summary>
    public void Dispose()
    {
        _disposed = true;
        _summaries.Changed -= OnChanged;
    }

    private void RequestDraftSave()
    {
        if (IsEditing && !_loadingEdit)
        {
            Interlocked.Increment(ref _draftVersion);
            _draftSave.Request();
        }
    }

    private async Task<bool> SaveDraftAsync()
    {
        if (_detail is not { } detail || !IsEditing)
        {
            return true;
        }

        var version = Volatile.Read(ref _draftVersion);
        try
        {
            await _summaries.SaveDraftAsync(Node, detail.Node.Current, EditTitle, EditBody, CancellationToken.None);
        }
        catch (SummaryRefusedException ex)
        {
            // Nothing left to save the draft against (for example the version is gone); not a storage failure.
            Notice = UiText.SummaryRefused(ex.Reason);
        }
#pragma warning disable CA1031 // A storage failure keeps the draft in the page and pending; the exit reports it.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }

        // A change made during the write stays pending.
        InterlockedMax(ref _savedDraftVersion, version);
        return true;
    }

    private static void InterlockedMax(ref long target, long value)
    {
        long current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private async Task RunAsync(Func<Task> operation)
    {
        try
        {
            Notice = null;
            await operation();
        }
        catch (SummaryRefusedException ex)
        {
            Notice = UiText.SummaryRefused(ex.Reason);
        }

        await _load.Request();
    }

    private void OnChanged(object? sender, ConversationId conversation)
    {
        if (!_disposed && (_detail is null || _detail.Node.Conversation == conversation))
        {
            _reload.Request();
        }
    }

    private async Task LoadAsync()
    {
        var detail = await _summaries.GetAsync(Node, CancellationToken.None);
        if (_disposed)
        {
            return;
        }

        _detail = detail;
        if (detail is null)
        {
            IsMissing = true;
            DisplayName = UiText.SummaryMissing;
            Shown = null;
            History.Clear();
            return;
        }

        DisplayName = detail.Node.DisplayName;
        var markers = detail.Markers;
        MarkersText = UiText.Markers(markers);
        HasLaterContent = markers.HasLaterContent;
        HasCandidate = detail.Node.Pending is not null;
        History.Clear();
        foreach (var version in detail.Versions.OrderByDescending(v => v.Seq))
        {
            History.Add(new SummaryVersionItem(version, version.Id == detail.Node.Current, version.Id == detail.Node.Pending, detail.ReadState(version.Id).IsRead));
        }

        Show(_shownId is { } id && detail.Versions.Any(v => v.Id == id) ? id : detail.Node.Current);
    }

    private void Show(SummaryVersionId id)
    {
        if (_detail is not { } detail)
        {
            return;
        }

        var version = detail.Versions.Single(v => v.Id == id);
        if (_shownId != id)
        {
            // A different version starts its own visible time and progress; each version's read state is its own.
            _shownId = id;
            _shownSince = _time.GetTimestamp();
            _reportedProgress = 0;
            _readCheckScheduled = false;
            ShownLocator = detail.ReadState(id).Locator;
        }

        ShownIsCurrent = id == detail.Node.Current;
        ShownIsCandidate = id == detail.Node.Pending;
        ShownIsRead = detail.ReadState(id).IsRead;
        ShownLabel = UiText.VersionLabel(version, ShownIsCurrent, ShownIsCandidate);
        Shown = version;
    }
}
