using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Export;
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

    public bool IsRead { get; } = isRead;

    public bool IsEdited => Version.Origin == SummaryOrigin.Edited;

    /// <summary>A saved version that is neither current nor the candidate.</summary>
    public bool IsHistory => !IsCurrent && !IsCandidate;

    /// <summary>The row in words, for screen readers; the view shows the same parts in columns.</summary>
    public string Label => UiText.VersionLabel(Version, IsCurrent, IsCandidate) + (IsRead ? " · " + UiText.MarkerRead : string.Empty);

    public string Number => UiText.VersionNumber(Version.Seq);

    public string Role => UiText.VersionRole(IsCurrent, IsCandidate);

    public string Origin => UiText.VersionOrigin(Version.Origin);

    public string Coverage => UiText.VersionCoverage(Version);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>The version on screen above; its row is highlighted and has no "view" action.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; set; }
}

/// <summary>
/// A question the page asks before an irreversible or replacing step; <see cref="Proceed"/> runs on "confirm".
/// <see cref="IsDestructive"/> marks a step that removes something (deleting versions, discarding the candidate), whose
/// confirm button is drawn in the danger style; adopting only replaces the current version, which stays in the history.
/// </summary>
public sealed record PendingConfirmation(string Message, Func<Task> Proceed, bool IsDestructive = false);

/// <summary>
/// A summary node's page (S4): the shown version (current by default), its read state and markers, the candidate
/// with adopt and discard, editing the current version with an autosaved draft, the history with "set current" and
/// deletion, renaming, exporting the shown saved version as Markdown, and the way back to the source conversation. Reading progress comes from the view; a version
/// becomes read past the threshold after enough visible time in this opening (U2, MW-C30).
/// </summary>
public sealed partial class SummaryViewModel : PageViewModel, IDisposable
{
    private readonly ISummaries _summaries;
    private readonly ISummaryExport _exports;
    private readonly ISystemShell _shell;
    private readonly IWorkbenchNavigator _navigator;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _dispatcher;
    private readonly SerialRefresh _load;
    private readonly UiThrottle _reload;
    private SummaryDetail? _detail;
    private SummaryVersionId? _shownId;
    private long _shownSince;
    private double _reportedProgress;
    private bool _readCheckScheduled;
    private bool _loadingEdit;
    private bool _disposed;

    // The edit session (see SummaryDrafts): the draft generation it opened under, the revision it last set, and the
    // version its text is based on. The newest text itself is owned by SummaryDrafts, not by this page.
    private long _draftGeneration;
    private long _draftRevision;
    private SummaryVersionId _draftBaseline;

    public SummaryViewModel(SummaryRoute route, PageContext context, ISummaries summaries, IUiDispatcher dispatcher, TimeProvider time, ISummaryExport exports, ISystemShell shell)
        : base(route)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(context);
        Node = route.Node;
        _summaries = summaries;
        _exports = exports;
        _shell = shell;
        _navigator = context.Navigator;
        _time = time;
        _dispatcher = dispatcher;
        _load = new SerialRefresh(LoadAsync);
        _reload = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(30), () => _ = _load.Request());
        _summaries.Changed += OnChanged;
        Loaded = _load.Request();
    }

    public SummaryNodeId Node { get; }

    internal Task Loaded { get; }

    public ObservableCollection<SummaryVersionItem> History { get; } = [];

    /// <summary>The history's header with the number of versions listed.</summary>
    [ObservableProperty]
    public partial string HistoryHeader { get; private set; } = UiText.HistoryHeading;

    /// <summary>Whether any version is ticked for deletion; the delete button is enabled only then.</summary>
    [ObservableProperty]
    public partial bool HasSelectedVersions { get; private set; }

    /// <summary>The heading above the editor, naming the version the edit starts from.</summary>
    [ObservableProperty]
    public partial string EditingHeading { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string DisplayName { get; private set; } = string.Empty;

    public override string Title => DisplayName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial bool IsMissing { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial SummaryVersion? Shown { get; private set; }

    [ObservableProperty]
    public partial string ShownLabel { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShownIsCurrent { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportHint))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial bool ShownIsCandidate { get; private set; }

    [ObservableProperty]
    public partial bool ShownIsRead { get; private set; }

    [ObservableProperty]
    public partial string MarkersText { get; private set; } = string.Empty;

    /// <summary>The read tag under the title: for the version on screen, worded so it says which version it is about.</summary>
    [ObservableProperty]
    public partial string ShownReadMarker { get; private set; } = string.Empty;

    /// <summary>The tags under the title in one line for screen readers (version read state, candidate, later content).</summary>
    [ObservableProperty]
    public partial string ShownMarkersText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasLaterContent { get; private set; }

    [ObservableProperty]
    public partial bool HasCandidate { get; private set; }

    [ObservableProperty]
    public partial string? Notice { get; private set; }

    /// <summary>Notices are problems unless they report a finished export.</summary>
    [ObservableProperty]
    public partial bool NoticeIsError { get; private set; } = true;

    /// <summary>Export the version without the source-note quote.</summary>
    [ObservableProperty]
    public partial bool ExportOmitSource { get; set; }

    /// <summary>Why export is unavailable for the shown version, or null.</summary>
    public string? ExportHint => ShownIsCandidate ? UiText.ExportCandidateHint : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmationIsDestructive))]
    public partial PendingConfirmation? Confirmation { get; private set; }

    /// <summary>Whether the open question confirms a step that removes something; its confirm button is then a danger one.</summary>
    public bool ConfirmationIsDestructive => Confirmation is { IsDestructive: true };

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
            }), IsDestructive: true);
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
        }), IsDestructive: true);
    }

    private bool CanExport() => Shown is not null && !ShownIsCandidate && !IsMissing;

    /// <summary>
    /// Exports the shown saved version (§8.3). The save dialog's own overwrite question is the only one; the version is
    /// read again by ID when written, so one deleted meanwhile is refused and nothing is written. Not a reading.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (Shown is not { } version || ShownIsCandidate)
        {
            return;
        }

        Notice = null;
        try
        {
            var name = await _exports.SuggestFileNameAsync(Node, version.Id, CancellationToken.None);
            if (await _shell.PickExportFileAsync(name + ".md", CancellationToken.None) is not { } destination)
            {
                return;
            }

            await _exports.ExportAsync(Node, version.Id, ExportOmitSource ? null : SourceName, destination, CancellationToken.None);
            Notice = UiText.Exported(destination.DisplayName);
            NoticeIsError = false;
        }
        catch (ExportRefusedException ex)
        {
            Notice = UiText.ExportRefused(ex.Reason);
            await _load.Request();
        }
        catch (ExportWriteException ex)
        {
            Notice = UiText.ExportFailed(ex.Failure, ex.Detail);
        }
#pragma warning disable CA1031 // Anything else (a failing save dialog, a store already closed by exit) is reported, never thrown at the UI thread.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Notice = UiText.ExportFailed(ExportFailure.Failed, ex.Message);
        }
    }

    private static string SourceName(ConversationSummary row) => UiText.ConversationTitle(row.Conversation, row.FirstQuestion, row.Quote);

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

        // Only the current version is edited; its newest draft (even one whose save failed on a page since left) is
        // picked up where it was left. A draft based on an older version is shown as such, never moved onto this one.
        var read = await _summaries.OpenDraftAsync(Node, detail.Node.Current, CancellationToken.None);
        _loadingEdit = true;
        (_draftBaseline, EditTitle, EditBody) = read switch
        {
            DraftRead.Draft draft => (draft.Baseline, draft.Title, draft.Body),
            DraftRead.Conflict conflict => (conflict.Baseline, conflict.Title, conflict.Body),
            _ => (detail.Node.Current, detail.Current.Title, detail.Current.Body),
        };
        _loadingEdit = false;
        _draftGeneration = read.Generation;
        _draftRevision = read.Revision;
        if (read is DraftRead.Conflict)
        {
            Notice = UiText.DraftConflict;
        }

        Show(detail.Node.Current);
        EditingHeading = UiText.EditingVersion(detail.Current.Seq);
        IsEditing = true;
    }

    [RelayCommand]
    private Task SaveEditAsync() =>
        _detail is { } detail
            ? RunAsync(async () =>
            {
                var saved = await _summaries.SaveEditAsync(Node, _draftBaseline, EditTitle, EditBody, _draftGeneration, _draftRevision, CancellationToken.None);
                IsEditing = false;
                _shownId = saved ?? detail.Node.Current;
            })
            : Task.CompletedTask;

    [RelayCommand]
    private Task DiscardEditAsync() =>
        RunAsync(async () =>
        {
            await _summaries.DiscardDraftAsync(Node, _draftGeneration, _draftRevision, CancellationToken.None);
            IsEditing = false;
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

    partial void OnNoticeChanged(string? value) => NoticeIsError = true;

    partial void OnEditTitleChanged(string value) => RequestDraftSave();

    partial void OnEditBodyChanged(string value) => RequestDraftSave();

    public void Dispose()
    {
        _disposed = true;
        _summaries.Changed -= OnChanged;
    }

    /// <summary>Hands every change to the draft owner, which autosaves it; the page keeps nothing of its own to save.</summary>
    private void RequestDraftSave()
    {
        if (!IsEditing || _loadingEdit)
        {
            return;
        }

        if (_summaries.SetDraft(Node, _draftGeneration, _draftBaseline, EditTitle, EditBody) is { } revision)
        {
            _draftRevision = revision;
        }
        else
        {
            // Saved or discarded on another page since this one opened: this text is not written over that.
            Notice = UiText.DraftStale;
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
        catch (StorageWriteException)
        {
            // Nothing was stored; what the user typed or chose stays on the page (§9.3).
            Notice = UiText.StoragePaused;
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
            var item = new SummaryVersionItem(version, version.Id == detail.Node.Current, version.Id == detail.Node.Pending, detail.ReadState(version.Id).IsRead);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SummaryVersionItem.IsSelected))
                {
                    UpdateHasSelectedVersions();
                }
            };
            History.Add(item);
        }

        HistoryHeader = UiText.HistoryHeadingCount(History.Count);
        UpdateHasSelectedVersions();
        Show(_shownId is { } id && detail.Versions.Any(v => v.Id == id) ? id : detail.Node.Current);
    }

    partial void OnShownIsReadChanged(bool value) => UpdateShownMarkers();

    private void UpdateHasSelectedVersions() => HasSelectedVersions = History.Any(h => h.IsSelected && h.CanSelect);

    private void UpdateShownMarkers()
    {
        ShownReadMarker = UiText.ShownReadMarker(ShownIsRead, ShownIsCurrent);
        ShownMarkersText = UiText.ShownMarkers(ShownIsRead, ShownIsCurrent, HasCandidate, HasLaterContent);
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
        UpdateShownMarkers();
        ShownLabel = UiText.VersionLabel(version, ShownIsCurrent, ShownIsCandidate);
        foreach (var item in History)
        {
            item.IsShown = item.Id == id;
        }

        Shown = version;
    }
}
