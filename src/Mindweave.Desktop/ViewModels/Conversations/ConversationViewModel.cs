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
public sealed partial class ConversationViewModel : PageViewModel, IDisposable, IAttachmentViewerHost
{
    private readonly IConversations _conversations;
    private readonly Application.Resources.IResources? _resources;
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
    private bool _draftLoaded;
    private ITimer? _tick;

    public ConversationViewModel(
        ConversationRoute route,
        PageContext context,
        IConversations conversations,
        IReadingState reading,
        IUiDispatcher dispatcher,
        IStorageStatus storage,
        ISummaries? summaries = null,
        TimeProvider? time = null,
        IConversationDeletion? deletion = null,
        IConversationDeletionPrompt? deletionPrompt = null,
        Application.Resources.IResources? resources = null,
        Application.Models.IModelSettings? models = null,
        WebSearchSwitch? webSearch = null)
        : base(route)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(context);
        Id = route.Id;
        _slot = context.Slot;
        _conversations = conversations;
        _resources = resources;
        _reading = reading;
        _storage = storage;
        _navigator = context.Navigator;
        _dispatcher = dispatcher;
        _time = time ?? TimeProvider.System;
        _load = new SerialRefresh(LoadAsync);
        _reload = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(30), () => _ = _load.Request());
        // Stop sits where send is while an answer runs; the page shows the storage pause once, at the top.
        // Attachments belong to this conversation's draft in this viewer slot (architecture §15.2), known once the workspace is.
        var attachments = resources is null || models is null
            ? null
            : new ComposerAttachmentServices(resources, models, () => _workspace == default ? null : new DraftSlot(_workspace, Id, _slot.Value));
        Composer = new Composer((text, ids) => _conversations.AskAsync(Id, text, ids, Composer?.Options, CancellationToken.None), attachments)
        {
            StopCommand = StopCommand,
            HostShowsStoragePause = true,
            WebSearch = webSearch,
        };
        _conversations.ConversationChanged += OnConversationChanged;
        _conversations.ModelChanged += OnModelChanged;
        // Before the first storage state is taken: a page opened while storage is paused tells the summary part at once.
        Summary = new ConversationSummaryPanel(Id, summaries, _navigator, () => IsStorageBlocked, () => _disposed);
        _storage.Changed += OnStorageStateChanged;
        IsStorageBlocked = _storage.IsBlocked;
        Composer.IsStoragePaused = IsStorageBlocked;
        Lifecycle = new ConversationLifecycleActions(Id, conversations, deletion, deletionPrompt, _navigator, () => _workspace, () => _load.Request(), Summary.SummarizeAsync);
        _summaries = summaries;
        if (_summaries is not null)
        {
            _summaries.Changed += OnSummaryChanged;
        }

        Loaded = LoadFirstAsync(route.SourceOf);
    }

    public ConversationId Id { get; }

    public Composer Composer { get; }

    public AttachmentViewer AttachmentViewer => Composer.Viewer;

    /// <summary>The entry to this conversation's summary, and summarizing it.</summary>
    public ConversationSummaryPanel Summary { get; }

    /// <summary>Archiving, restoring and deleting this conversation, with the questions asked first.</summary>
    public ConversationLifecycleActions Lifecycle { get; }

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
    [NotifyPropertyChangedFor(nameof(ContextUsageText), nameof(ContextIsTight), nameof(ShowsContextLine))]
    public partial ContextUsage? ContextUsage { get; private set; }

    /// <summary>
    /// The context sentence stays as a line under the input only when the conversation no longer fits, since it says what
    /// to do; otherwise the ring beside send carries it on hover (2026-11-17).
    /// </summary>
    public bool ShowsContextLine => ContextUsage is { TooLong: true };

    partial void OnContextUsageChanged(ContextUsage? value) => Composer.ContextUsage = value;

    public string? ContextUsageText => ContextUsage is { } usage ? UiText.ContextUsage(usage) : null;

    /// <summary>Near the limit, compressed, or over it: shown with emphasis rather than as a quiet note.</summary>
    public bool ContextIsTight => ContextUsage is { } usage && (usage.TooLong || usage.Compressed || usage.Fraction >= 0.8);

    /// <summary>
    /// Core storage is failing (requirements §9.3): asking, retrying, branching and summarizing are disabled with the
    /// reason; everything shown stays readable and selectable. Lifts by itself once storage takes writes again.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StorageNotice))]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    public partial bool IsStorageBlocked { get; private set; }

    public string? StorageNotice => IsStorageBlocked ? UiText.StoragePaused : null;

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

    /// <summary>The toolbar's archive entry (a delete that takes branches along offers archiving in its own dialog).</summary>
    public bool ShowArchiveInToolbar => IsOpen;

    [ObservableProperty]
    public partial string RenameText { get; set; } = string.Empty;

    /// <summary>
    /// Whether any answer on this page has branches. Known from the stored data before layout, so the message list's
    /// room for marks in the margin is right on the first layout and does not reflow after the reading position is restored.
    /// </summary>
    [ObservableProperty]
    public partial bool HasBranches { get; private set; }

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
                if (value && _draftLoaded)
                {
                    _ = FollowDraftAsync();
                }
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

    partial void OnIsStorageBlockedChanged(bool value)
    {
        Composer.IsStoragePaused = value;
        foreach (var message in Messages)
        {
            message.IsBranchPaused = value;
        }

        // Refusals given because of the pause that just ended no longer apply.
        Summary.OnStoragePauseChanged(value);
        if (!value && BranchNotice == UiText.BranchRejected(BranchRejection.StorageUnavailable))
        {
            BranchNotice = null;
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
        var outcome = await _conversations.RetryAsync(Id, answer.Id, Composer.Options, CancellationToken.None);
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
        try
        {
            await _conversations.RenameAsync(Id, RenameText, CancellationToken.None);
        }
        catch (StorageWriteException)
        {
            // Nothing was stored (health check 2026-11-12): the typed name stays in the box; the page already says storage is paused.
            IsRenaming = true;
        }
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    /// <summary>A tool's stored result, in the attachment viewer (requirements §10.4.4: 可以打开工具结果查看).</summary>
    private void OpenToolResult(Core.Resources.ResourceId result, string title) => Composer.Open(AttachmentChip.ForToolResult(result, title, Composer.Open));

    /// <summary>A summary a tool found: navigation, like opening it from the network.</summary>
    /// <summary>A 出处 opens the viewer at its passage (requirements §10.4.5); a summary's opens the summary, as from the network (L11).</summary>
    private void OpenCitation(Core.Conversations.CitationBlock citation)
    {
        if (citation.Kind == Core.Conversations.CitationKind.Summary && Guid.TryParseExact(citation.Target, "D", out var node))
        {
            OpenSummary(new Core.Summaries.SummaryNodeId(node));
            return;
        }

        _ = Composer.Viewer.OpenCitationAsync(citation);
    }

    private void OpenSummary(Core.Summaries.SummaryNodeId node) =>
        _navigator.Navigate(new NavigationRequest(MainSection.SummaryNetwork, new SummaryRoute(node)));

    [RelayCommand]
    private void OpenSettings() => _navigator.Navigate(new NavigationRequest(MainSection.Settings));

    /// <summary>The page left every stack; tests check pages of deleted content were released.</summary>
    internal bool IsDisposed => _disposed;

    public void Dispose()
    {
        _disposed = true;
        UpdateTicking();
        _conversations.ConversationChanged -= OnConversationChanged;
        _conversations.ModelChanged -= OnModelChanged;
        Composer.WebSearch?.Dispose();
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
        _draftLoaded = true;
        if (sourceOf is { } child)
        {
            _sourceFocus = await _conversations.GetSourceAsync(child, CancellationToken.None);
        }

        await _load.Request();
    }

    /// <summary>
    /// Shown again (health check 2026-11-12, item 9): another page of this conversation further up the back stack may
    /// have changed the shared draft meanwhile — typed into it, or sent it. The box takes the stored draft, so going back
    /// never hides a newer draft (to be overwritten by the next keystroke) or offers a sent question again. A draft set in
    /// this run is held in memory, so this then completes at once; text typed before a slower read returns is kept.
    /// </summary>
    private async Task FollowDraftAsync()
    {
        var shown = Composer.Text;
        var stored = await _reading.GetDraftAsync(Id, _slot, CancellationToken.None);

        // Typed meanwhile (the read went to the database): what was typed is the draft now, and it is not replaced.
        if (_disposed || stored == Composer.Text || Composer.Text != shown)
        {
            return;
        }

        Composer.PropertyChanged -= OnComposerChanged;
        Composer.Text = stored;
        Composer.PropertyChanged += OnComposerChanged;
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
            _dispatcher.Post(() => _ = RefreshAttachmentCapabilitiesAsync());
        }
    }

    /// <summary>A sent attachment whose original file is gone shows 原件缺失 (requirements §10.4.3); its text still opens.</summary>
    private async Task MarkMissingOriginalsAsync()
    {
        if (_resources is null)
        {
            return;
        }

        foreach (var chip in Messages.SelectMany(m => m.Attachments).Where(c => !c.OriginalChecked && c.Resource is not null).ToList())
        {
            chip.OriginalChecked = true;
            if (await _resources.GetAsync(chip.Resource!.Value, CancellationToken.None) is { } stored)
            {
                chip.MarkOriginal(_resources.HasOriginal(stored.Resource));
            }
        }
    }

    /// <summary>Whether 图片 is offered, and whether images already in the conversation go as a line of text instead.</summary>
    private async Task RefreshAttachmentCapabilitiesAsync()
    {
        await Composer.RefreshCapabilitiesAsync();
        Composer.HistoryImagesUnseen = Composer.CanAttach && !Composer.CanAttachImages && Messages.Any(m => m.Attachments.Any(a => a.IsImage));
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

        var firstLoad = _workspace == default;
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
        MessageListReconciler.Reconcile(Messages, content, branches, archivedBranches, IsArchived, NewItem);
        HasBranches = Messages.Any(m => m.IsAnswer && m.Branches.Count > 0);
        Volatile.Write(ref _related, lineage.Select(l => l.Conversation.Id).Concat(branches.Select(b => b.Child)).ToHashSet());
        UpdateRunning();
        ContextUsage = await _conversations.GetContextUsageAsync(Id, CancellationToken.None);
        if (firstLoad)
        {
            // The draft's attachments come back with the draft (they survive a restart, §10.4.3).
            await Composer.LoadDraftAttachmentsAsync();
        }

        await RefreshAttachmentCapabilitiesAsync();
        await MarkMissingOriginalsAsync();
        await Summary.LoadAsync();
    }

    private MessageItemViewModel NewItem(Message message)
    {
        var item = new MessageItemViewModel(message, _conversations.Observe(message.Id), _dispatcher, _time, Composer.Open, OpenToolResult, OpenSummary, OpenCitation, _conversations.DecideApproval)
        {
            IsBranchPaused = IsStorageBlocked,
        };
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
