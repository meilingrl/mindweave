using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Conversations;
using Mindweave.Application.Reading;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Reading;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>An ancestor shown above a branch, so the reader always sees where they are and how they got there; the first has no separator before it.</summary>
public sealed record LineageItem(ConversationId Id, string Title, bool IsFirst = false);

/// <summary>
/// One conversation: its messages (finished ones from storage, unfinished ones live from the coordinator), the
/// branches made from its answers, and — for a branch — its ancestors and source. Reading position and draft are
/// kept per viewer slot by <see cref="ReadingStateService"/>; the view converts geometry to positions and back.
/// An archived conversation (or one below an archived ancestor) opens read-only and offers "restore and continue"
/// (D-021); archiving and deleting start here too.
/// </summary>
public sealed partial class ConversationViewModel : PageViewModel, IDisposable
{
    private readonly IConversations _conversations;
    private readonly IReadingState _reading;
    private readonly IStorageStatus _storage;
    private readonly ISummaries? _summaries;
    private readonly IWorkbenchNavigator _navigator;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ViewerSlotKey _slot;
    private readonly SerialRefresh _load;
    private readonly UiThrottle _reload;
    private BranchSource? _source;
    private Core.Workspaces.WorkspaceId _workspace;
    private IReadOnlySet<ConversationId> _related = new HashSet<ConversationId>();
    private bool _disposed;
    private bool _isShown;
    private ITimer? _tick;

    public ConversationViewModel(ConversationRoute route, PageContext context, IConversations conversations, IReadingState reading, IUiDispatcher dispatcher, IStorageStatus storage, ISummaries? summaries = null, TimeProvider? time = null)
        : base(route)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(context);
        Id = route.Id;
        _slot = context.Slot;
        _conversations = conversations;
        _reading = reading;
        _storage = storage;
        _navigator = context.Navigator;
        _dispatcher = dispatcher;
        _time = time ?? TimeProvider.System;
        _load = new SerialRefresh(LoadAsync);
        _reload = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(30), () => _ = _load.Request());
        // Stop sits where send is while an answer runs; the page shows the storage pause once, at the top.
        Composer = new Composer(text => _conversations.AskAsync(Id, text, CancellationToken.None)) { StopCommand = StopCommand, HostShowsStoragePause = true };
        _conversations.ConversationChanged += OnConversationChanged;
        _conversations.ModelChanged += OnModelChanged;
        _storage.Changed += OnStorageStateChanged;
        IsStorageBlocked = _storage.IsBlocked;
        Composer.IsStoragePaused = IsStorageBlocked;
        _summaries = summaries;
        if (_summaries is not null)
        {
            _summaries.Changed += OnSummaryChanged;
        }

        Loaded = LoadFirstAsync(route.SourceOf);
    }

    public ConversationId Id { get; }

    public Composer Composer { get; }

    public ObservableCollection<MessageItemViewModel> Messages { get; } = [];

    /// <summary>Ancestors from the root to the direct parent; empty for a root conversation.</summary>
    public ObservableCollection<LineageItem> Lineage { get; } = [];

    /// <summary>Completes when the first load finished, including reading position and draft; the view and tests await it.</summary>
    internal Task Loaded { get; }

    private BranchSource? _sourceFocus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string DisplayTitle { get; private set; } = UiText.NewConversation;

    public override string Title => DisplayTitle;

    [ObservableProperty]
    public partial bool IsMissing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBranch))]
    public partial string? SourceQuote { get; private set; }

    public bool IsBranch => SourceQuote is not null;

    /// <summary>Shown when the source passage could not be located exactly (MW-C13); never replaced by a text search.</summary>
    [ObservableProperty]
    public partial string? SourceNotice { get; set; }

    /// <summary>Why creating a branch was refused, if it was.</summary>
    [ObservableProperty]
    public partial string? BranchNotice { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial MessageItemViewModel? RunningAnswer { get; private set; }

    /// <summary>How much of the model's context this conversation takes (null when no model is configured).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextUsageText), nameof(ContextIsTight))]
    public partial ContextUsage? ContextUsage { get; private set; }

    public string? ContextUsageText => ContextUsage is { } usage ? UiText.ContextUsage(usage) : null;

    /// <summary>Near the limit, compressed, or over it: shown with emphasis rather than as a quiet note.</summary>
    public bool ContextIsTight => ContextUsage is { } usage && (usage.TooLong || usage.Compressed || usage.Fraction >= 0.8);

    /// <summary>This conversation's summary node, if it has one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary), nameof(SummarizeLabel))]
    public partial SummaryNodeId? SummaryNode { get; private set; }

    public bool HasSummary => SummaryNode is not null;

    public string SummarizeLabel => HasSummary ? UiText.Resummarize : UiText.Summarize;

    [ObservableProperty]
    public partial string? SummaryEntryText { get; private set; }

    [ObservableProperty]
    public partial string? SummaryMarkersText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopSummaryCommand))]
    public partial GenerationTaskId? RunningSummary { get; private set; }

    /// <summary>Why the last summary attempt left nothing (failed, stopped or interrupted), shown with a retry.</summary>
    [ObservableProperty]
    public partial string? SummaryProblem { get; private set; }

    /// <summary>
    /// Core storage is failing (requirements §9.3): asking, retrying, branching and summarizing are disabled with the
    /// reason; everything shown stays readable and selectable. Lifts by itself once storage takes writes again.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StorageNotice))]
    [NotifyCanExecuteChangedFor(nameof(SummarizeCommand), nameof(RetryCommand))]
    public partial bool IsStorageBlocked { get; private set; }

    public string? StorageNotice => IsStorageBlocked ? UiText.StoragePaused : null;

    /// <summary>Why "summarize" was refused just now.</summary>
    [ObservableProperty]
    public partial string? SummaryNotice { get; private set; }

    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    /// <summary>Hidden by an archive (its own or an ancestor's): readable only; new work waits for a restore.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen), nameof(ShowArchiveInToolbar))]
    public partial bool IsArchived { get; private set; }

    /// <summary>Neither missing nor archived: asking, retrying, branching and summarizing are offered.</summary>
    public bool IsOpen => !IsArchived && !IsMissing;

    /// <summary>The read-only note of an archived page: archived itself, or which ancestors hide it.</summary>
    [ObservableProperty]
    public partial string? ArchiveText { get; private set; }

    /// <summary>Why archiving or deleting did not happen.</summary>
    [ObservableProperty]
    public partial string? ArchiveNotice { get; private set; }

    /// <summary>A delete was refused; archiving is offered instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowArchiveInToolbar))]
    public partial bool OfferArchive { get; private set; }

    /// <summary>The toolbar's archive entry; left out while the refusal notice offers the same action next to its reason.</summary>
    public bool ShowArchiveInToolbar => IsOpen && !OfferArchive;

    /// <summary>A question asked before deleting, or before a restore that brings back archived ancestors.</summary>
    [ObservableProperty]
    public partial PendingConfirmation? Confirmation { get; private set; }

    [ObservableProperty]
    public partial string RenameText { get; set; } = string.Empty;

    /// <summary>Interval of the running answer's live counter.</summary>
    internal static readonly TimeSpan ElapsedTick = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Set by the view while this page is on screen. The running answer's live counter ticks (on the app's clock) only
    /// while the page is shown and something on it is generating; otherwise no timer exists.
    /// </summary>
    public bool IsShown
    {
        get => _isShown;
        set
        {
            if (_isShown != value)
            {
                _isShown = value;
                UpdateTicking();
            }
        }
    }

    /// <summary>Whether the live counter's timer is running now; tests check that an idle page has none.</summary>
    internal bool IsTicking => _tick is not null;

    /// <summary>The view reports where the reader is; kept in memory at once, stored shortly after.</summary>
    public void ReportPosition(ReadingPosition position) => _reading.SetPosition(Id, _slot, position);

    /// <summary>Where the reader last was in this slot (newest in-memory value first), for a view to restore.</summary>
    public Task<ReadingPosition?> CurrentPositionAsync() => _reading.GetPositionAsync(Id, _slot, CancellationToken.None);

    /// <summary>
    /// The branch whose source this page was opened to show (return to source). Taken once: a view built later
    /// for this page (after back) restores the reading position instead.
    /// </summary>
    public BranchSource? TakeSourceFocus() => Interlocked.Exchange(ref _sourceFocus, null);

    /// <summary>Creates a branch from a selection the view captured, then opens it.</summary>
    public async Task CreateBranchAsync(BranchSelection selection)
    {
        var outcome = await _conversations.CreateBranchAsync(Id, selection, CancellationToken.None);
        BranchNotice = outcome.Rejection is { } rejection ? UiText.BranchRejected(rejection) : null;
        if (outcome.Accepted)
        {
            _navigator.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(outcome.Child)));
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => RunningAnswer is { } answer ? _conversations.StopAsync(answer.Id) : Task.CompletedTask;

    private bool CanStop() => RunningAnswer is not null;

    private bool CanRetryNow(MessageItemViewModel? answer) => !IsStorageBlocked;

    private bool CanSummarize() => !IsStorageBlocked;

    partial void OnIsStorageBlockedChanged(bool value)
    {
        Composer.IsStoragePaused = value;
        foreach (var message in Messages)
        {
            message.IsBranchPaused = value;
        }

        if (!value)
        {
            // Refusals given because of the pause that just ended no longer apply.
            if (SummaryNotice == UiText.SummarizeRejected(SummarizeRejection.StorageUnavailable))
            {
                SummaryNotice = null;
            }

            if (BranchNotice == UiText.BranchRejected(BranchRejection.StorageUnavailable))
            {
                BranchNotice = null;
            }
        }
    }

    private void OnStorageStateChanged(object? sender, EventArgs e) =>
        _dispatcher.Post(() =>
        {
            if (!_disposed)
            {
                IsStorageBlocked = _storage.IsBlocked;
            }
        });

    [RelayCommand(CanExecute = nameof(CanRetryNow))]
    private async Task RetryAsync(MessageItemViewModel answer)
    {
        var outcome = await _conversations.RetryAsync(Id, answer.Id, CancellationToken.None);
        Composer.Show(outcome.Rejection);
    }

    /// <summary>Back to the passage this branch was created from; follows the branch relation, not the back history.</summary>
    [RelayCommand]
    private void ReturnToSource()
    {
        if (_source is { } source)
        {
            _navigator.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(source.Parent, SourceOf: Id), NavigationIntent.ReturnToSource));
        }
    }

    /// <summary>Re-entering a branch or an ancestor is plain navigation: it restores that conversation's own place.</summary>
    [RelayCommand]
    private void OpenConversation(ConversationId id) =>
        _navigator.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(id)));

    [RelayCommand]
    private void BeginRename()
    {
        RenameText = DisplayTitle;
        IsRenaming = true;
    }

    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        IsRenaming = false;
        await _conversations.RenameAsync(Id, RenameText, CancellationToken.None);
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private void OpenSettings() => _navigator.Navigate(new NavigationRequest(MainSection.Settings));

    /// <summary>Hides the conversation and its branches; refused (and named) while an answer or summary in them runs.</summary>
    [RelayCommand]
    private async Task ArchiveAsync()
    {
        OfferArchive = false;
        ArchiveOutcome outcome;
        try
        {
            outcome = await _conversations.ArchiveAsync(Id, CancellationToken.None);
        }
        catch (StorageWriteException)
        {
            ArchiveNotice = UiText.ArchiveStorageFailed;
            return;
        }

        ArchiveNotice = outcome.Busy.Count > 0 ? UiText.ArchiveRefused(await TitlesAsync(outcome.Busy)) : null;
        await _load.Request();
    }

    [RelayCommand]
    private Task RestoreAndContinueAsync() => RestoreThenAsync(null);

    /// <summary>An archived conversation is never summarized directly (D-021): restore first, then summarize.</summary>
    [RelayCommand]
    private Task RestoreAndSummarizeAsync() => RestoreThenAsync(SummarizeAsync);

    /// <summary>Asks first: deleting is irreversible and takes unsent drafts with it.</summary>
    [RelayCommand]
    private void Delete()
    {
        ArchiveNotice = null;
        OfferArchive = false;
        Confirmation = new PendingConfirmation(UiText.DeleteConfirm, async () =>
        {
            DeleteResult result;
            try
            {
                result = await _conversations.DeleteAsync(Id, CancellationToken.None);
            }
            catch (StorageWriteException)
            {
                ArchiveNotice = UiText.ArchiveStorageFailed;
                return;
            }

            if (result.Deleted || result.Missing)
            {
                ConversationRemoval.Forget(_navigator, Id, result.Parent);
                return;
            }

            ArchiveNotice = UiText.DeleteRefused(result.Blockers, offerArchive: !IsArchived);
            OfferArchive = !IsArchived;
        }, IsDestructive: true);
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

    /// <summary>Restores this conversation's path, asking first when archived ancestors come back with it, then continues.</summary>
    private async Task RestoreThenAsync(Func<Task>? then)
    {
        ArchiveNotice = null;
        var plan = await _conversations.PlanRestoreAsync(Id, CancellationToken.None);
        async Task Restore()
        {
            if (plan.IsHidden)
            {
                try
                {
                    await _conversations.RestoreAsync(Id, CancellationToken.None);
                }
                catch (StorageWriteException)
                {
                    ArchiveNotice = UiText.ArchiveStorageFailed;
                    return;
                }

                await _load.Request();
            }

            if (then is not null)
            {
                await then();
            }
        }

        if (plan.ArchivedAncestors.Count > 0)
        {
            Confirmation = new PendingConfirmation(UiText.RestoreConfirm(await TitlesAsync(plan.ArchivedAncestors), plan.OthersReappearing), Restore);
        }
        else
        {
            await Restore();
        }
    }

    private async Task<IReadOnlyList<string>> TitlesAsync(IReadOnlyList<ConversationId> ids)
    {
        var rows = (await _conversations.ListAsync(_workspace, CancellationToken.None)).ToDictionary(r => r.Conversation.Id);
        return [.. ids.Select(id => rows.TryGetValue(id, out var row) ? UiText.ConversationTitle(row.Conversation, row.FirstQuestion, row.Quote) : UiText.NewConversation)];
    }

    /// <summary>Summarize the conversation as it is now; it can be done at any stage and chatting continues (§6.4).</summary>
    [RelayCommand(CanExecute = nameof(CanSummarize))]
    private async Task SummarizeAsync()
    {
        if (_summaries is null)
        {
            return;
        }

        var outcome = await _summaries.SummarizeAsync(Id, CancellationToken.None);
        SummaryNotice = outcome.Rejection is { } rejection ? UiText.SummarizeRejected(rejection) : null;
        await LoadSummaryAsync();
    }

    [RelayCommand(CanExecute = nameof(CanStopSummary))]
    private Task StopSummaryAsync() => RunningSummary is { } task && _summaries is not null ? _summaries.StopAsync(task) : Task.CompletedTask;

    private bool CanStopSummary() => RunningSummary is not null;

    [RelayCommand]
    private void OpenSummary()
    {
        if (SummaryNode is { } node)
        {
            _navigator.Navigate(new NavigationRequest(MainSection.Network, new SummaryRoute(node)));
        }
    }

    public void Dispose()
    {
        _disposed = true;
        UpdateTicking();
        _conversations.ConversationChanged -= OnConversationChanged;
        _conversations.ModelChanged -= OnModelChanged;
        _storage.Changed -= OnStorageStateChanged;
        if (_summaries is not null)
        {
            _summaries.Changed -= OnSummaryChanged;
        }

        Composer.PropertyChanged -= OnComposerChanged;
        foreach (var message in Messages)
        {
            message.Dispose();
        }
    }

    private async Task LoadFirstAsync(ConversationId? sourceOf)
    {
        // Page state first, so the draft is in place before the reader can type.
        Composer.Text = await _reading.GetDraftAsync(Id, _slot, CancellationToken.None);
        Composer.PropertyChanged += OnComposerChanged;
        if (sourceOf is { } child)
        {
            _sourceFocus = await _conversations.GetSourceAsync(child, CancellationToken.None);
        }

        await _load.Request();
    }

    private void OnComposerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Composer.Text))
        {
            _reading.SetDraft(Id, _slot, Composer.Text);
        }
    }

    private void OnModelChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            _reload.Request();
        }
    }

    private void OnConversationChanged(object? sender, ConversationId changed)
    {
        // Raised on worker threads; the throttle marshals the reload to the UI thread. Ancestors (titles shown
        // above) and branches (entries under answers) count as this page's content.
        if (!_disposed && (changed == Id || Volatile.Read(ref _related).Contains(changed)))
        {
            _reload.Request();
        }
    }

    private void OnSummaryChanged(object? sender, ConversationId changed)
    {
        if (!_disposed && changed == Id)
        {
            _reload.Request();
        }
    }

    private async Task LoadSummaryAsync()
    {
        if (_summaries is null || _disposed)
        {
            return;
        }

        var entry = await _summaries.ForConversationAsync(Id, CancellationToken.None);
        var task = await _summaries.LatestTaskAsync(Id, CancellationToken.None);
        if (_disposed)
        {
            return;
        }

        SummaryNode = entry?.Node.Id;
        SummaryEntryText = entry is null ? null : UiText.SummaryEntry(entry.Node.DisplayName);
        SummaryMarkersText = entry is null ? null : UiText.Markers(entry.Markers) is { Length: > 0 } markers ? markers : null;
        RunningSummary = task is { State: GenerationTaskState.Queued or GenerationTaskState.Running } ? task.Id : null;
        SummaryProblem = task is { State: GenerationTaskState.Failed or GenerationTaskState.Stopped or GenerationTaskState.Interrupted or GenerationTaskState.Cancelled }
            ? UiText.SummaryFailed(task)
            : null;
        if (RunningSummary is not null)
        {
            SummaryNotice = null;
        }
    }

    private async Task LoadAsync()
    {
        var content = await _conversations.LoadAsync(Id, CancellationToken.None);
        if (_disposed)
        {
            return;
        }

        if (content is null)
        {
            IsMissing = true;
            OnPropertyChanged(nameof(IsOpen));
            OnPropertyChanged(nameof(ShowArchiveInToolbar));
            DisplayTitle = UiText.ConversationMissing;
            return;
        }

        _workspace = content.Conversation.WorkspaceId;
        var lineage = await _conversations.GetLineageAsync(Id, CancellationToken.None);
        var branches = await _conversations.ListBranchesAsync(Id, CancellationToken.None);
        var archivedBranches = await _conversations.ArchivedAmongAsync([.. branches.Select(b => b.Child)], CancellationToken.None);
        var archive = await _conversations.PlanRestoreAsync(Id, CancellationToken.None);
        if (_disposed)
        {
            return;
        }

        _source = lineage.Count > 0 ? lineage[^1].Source : null;
        SourceQuote = _source?.Quote;
        DisplayTitle = UiText.ConversationTitle(content.Conversation, content.Messages.FirstOrDefault(m => m.Role == MessageRole.User)?.Text, _source?.Quote);
        Lineage.Clear();
        foreach (var ancestor in lineage.Take(Math.Max(0, lineage.Count - 1)))
        {
            Lineage.Add(new LineageItem(ancestor.Conversation.Id, UiText.ConversationTitle(ancestor.Conversation, null, ancestor.Source?.Quote), Lineage.Count == 0));
        }

        IsArchived = archive.IsHidden;
        ArchiveText = !archive.IsHidden ? null
            : archive.ArchivedAncestors.Count == 0 ? UiText.ArchivedReadOnly
            : UiText.HiddenByAncestors([.. archive.ArchivedAncestors.Select(a => Lineage.FirstOrDefault(l => l.Id == a)?.Title ?? UiText.NewConversation)]);
        if (IsArchived)
        {
            OfferArchive = false;
        }

        // Update in place: a message view that is still valid keeps its control, so a selection, a highlighted
        // source or the layout being read survives a reload (a title arriving, a branch being made). Only new
        // messages are added, and an answer that was live and is now stored as finished is replaced, so its
        // view switches from the live builder to the stored text that anchors are captured against.
        var last = ConversationRules.LastAnswer(content.Messages);
        var superseded = content.Messages.Where(m => m.Supersedes is not null).Select(m => m.Supersedes!.Value).ToHashSet();
        var stored = content.Messages.Select(m => m.Id).ToHashSet();
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (!stored.Contains(Messages[i].Id))
            {
                Messages[i].Dispose();
                Messages.RemoveAt(i);
            }
        }

        for (var i = 0; i < content.Messages.Count; i++)
        {
            var message = content.Messages[i];
            var item = i < Messages.Count && Messages[i].Id == message.Id ? Messages[i] : null;
            if (item is not null && item.IsLive && message.IsFinished)
            {
                item.Dispose();
                item = null;
                Messages.RemoveAt(i);
            }

            if (item is null)
            {
                item = NewItem(message);
                Messages.Insert(i, item);
            }

            item.IsSuperseded = superseded.Contains(message.Id);
            item.WasCompressed = content.CompressedAnswers?.Contains(message.Id) == true;
            item.IsArchived = IsArchived;
            item.CanRetry = !IsArchived && last?.Id == message.Id && message.IsFinished;
            var entries = branches.Where(b => b.SourceMessage == message.Id).Select(b => new BranchEntry(b.Child, b.Quote, archivedBranches.Contains(b.Child))).ToList();
            if (!entries.SequenceEqual(item.Branches))
            {
                item.Branches.Clear();
                foreach (var entry in entries)
                {
                    item.Branches.Add(entry);
                }
            }
        }

        Volatile.Write(ref _related, lineage.Select(l => l.Conversation.Id).Concat(branches.Select(b => b.Child)).ToHashSet());
        UpdateRunning();
        ContextUsage = await _conversations.GetContextUsageAsync(Id, CancellationToken.None);
        await LoadSummaryAsync();
    }

    private MessageItemViewModel NewItem(Message message)
    {
        var item = new MessageItemViewModel(message, _conversations.Observe(message.Id), _dispatcher, _time) { IsBranchPaused = IsStorageBlocked };
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MessageItemViewModel.State))
            {
                UpdateRunning();
            }
        };
        return item;
    }

    private void UpdateRunning()
    {
        RunningAnswer = Messages.LastOrDefault(m => m.IsAnswer && m.IsInProgress);
        Composer.IsBlocked = RunningAnswer is not null || IsArchived;
        Composer.IsAnswerRunning = RunningAnswer is not null;
        var last = Messages.LastOrDefault();
        if (last is { IsAnswer: true })
        {
            last.CanRetry = !last.IsInProgress && !IsArchived;
        }

        UpdateTicking();
    }

    /// <summary>Starts the one-second tick when a shown page has a running answer, and ends it as soon as either stops.</summary>
    private void UpdateTicking()
    {
        var wanted = _isShown && !_disposed && RunningAnswer is not null;
        if (wanted && _tick is null)
        {
            RefreshElapsed();
            _tick = _time.CreateTimer(_ => _dispatcher.Post(RefreshElapsed), null, ElapsedTick, ElapsedTick);
        }
        else if (!wanted && _tick is not null)
        {
            _tick.Dispose();
            _tick = null;
        }
    }

    private void RefreshElapsed()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var message in Messages)
        {
            message.RefreshElapsed();
        }
    }
}
