using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>
/// The active workspace's conversations as a branch tree: root conversations newest activity first (activity
/// anywhere in their tree counts), each branch under its parent in creation order. Archived conversations and
/// everything below them are left out; an entry at the bottom leads to them. Kept in sync with storage and generation.
/// </summary>
public sealed partial class ConversationListViewModel : ViewModelBase, IDisposable
{
    private readonly IConversations _conversations;
    private readonly IConversationMarks _marks;
    private readonly Workbench _workbench;
    private readonly SerialRefresh _load;
    private readonly UiThrottle _reload;
    private readonly ConversationDeletionRequests _deletionPrompt;
    private readonly ISummaries _summaries;
    private readonly IStorageStatus _storage;
    private readonly ISharePrompt _share;
    private readonly IUiDispatcher _dispatcher;
    private WorkspaceId? _shown;

    /// <summary>Where a Shift click's range starts: the last row toggled, or the conversation shown.</summary>
    private ConversationId? _anchor;

    public ConversationListViewModel(
        IConversations conversations,
        Workbench workbench,
        IUiDispatcher dispatcher,
        IConversationMarks marks,
        ConversationDeletionRequests deletion,
        ISummaries summaries,
        IStorageStatus storage,
        ISharePrompt share)
    {
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(deletion);
        ArgumentNullException.ThrowIfNull(storage);
        _share = share;
        _conversations = conversations;
        _summaries = summaries;
        _storage = storage;
        _dispatcher = dispatcher;
        IsStoragePaused = storage.IsBlocked;
        storage.Changed += OnStorageChanged;
        _deletionPrompt = deletion;
        deletion.Completed += OnDeletionCompleted;

        _marks = marks;
        _workbench = workbench;
        _load = new SerialRefresh(ReloadAsync);
        _reload = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(100), () => Loaded = _load.Request());
        _conversations.ConversationChanged += OnConversationChanged;
        _conversations.ActivityChanged += OnActivityChanged;
        _marks.Changed += OnMarksChanged;

        _workbench.PropertyChanged += OnWorkbenchChanged;
        _marks.SetViewing(Shown());
    }

    /// <summary>Stops listening to the services; the view it belongs to is being replaced.</summary>
    public void Dispose()
    {
        _storage.Changed -= OnStorageChanged;
        _deletionPrompt.Completed -= OnDeletionCompleted;

        _conversations.ConversationChanged -= OnConversationChanged;
        _conversations.ActivityChanged -= OnActivityChanged;
        _marks.Changed -= OnMarksChanged;

        _workbench.PropertyChanged -= OnWorkbenchChanged;
    }

    /// <summary>Raised on worker threads; the rows' menus follow the pause on the UI thread.</summary>
    private void OnStorageChanged(object? sender, EventArgs e) => _dispatcher.Post(() => IsStoragePaused = _storage.IsBlocked);

    /// <summary>
    /// The dialog deleted or archived the selection (or chose 改为归档): nothing of it is left to act on. A dialog opened
    /// from a conversation page for something else leaves the selection alone.
    /// </summary>
    private void OnDeletionCompleted(object? sender, IReadOnlyList<ConversationId> done)
    {
        if (HasSelection && Selection.Select(i => i.Id).ToHashSet().SetEquals(done))
        {
            ClearSelection();
        }
    }

    private void OnConversationChanged(object? sender, ConversationId e) => _reload.Request();

    private void OnActivityChanged(object? sender, EventArgs e) => _reload.Request();

    private void OnMarksChanged(object? sender, EventArgs e) => _reload.Request();

    public ObservableCollection<ConversationItemViewModel> Items { get; } = [];

    /// <summary>The most recent reload; tests await it.</summary>
    internal Task Loaded { get; private set; } = Task.CompletedTask;

    /// <summary>How many conversations of the workspace the user archived (the "已归档（N）" entry).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArchived), nameof(ArchivedLabel))]
    public partial int ArchivedCount { get; private set; }

    public bool HasArchived => ArchivedCount > 0;

    /// <summary>Some listed conversation needs attention (a failed or interrupted answer); shown on the expand button while the sidebar is collapsed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsUnread))]
    public partial bool HasAttention { get; private set; }

    /// <summary>Some listed conversation has a new answer the user has not seen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsUnread))]
    public partial bool HasUnread { get; private set; }

    /// <summary>The collapsed sidebar's expand button shows one dot: attention (error colour) wins over unread (accent).</summary>
    public bool ShowsUnread => HasUnread && !HasAttention;

    public string ArchivedLabel => UiText.ArchivedEntry(ArchivedCount);

    [RelayCommand]
    private void OpenArchived() => _workbench.Navigate(new NavigationRequest(MainSection.Explore, ArchivedRoute.Instance));

    /// <summary>
    /// A plain click opens the conversation and ends any selection; in the 选择 mode it ticks the row instead (the mode
    /// is for touch and for finding the feature without knowing the modifier keys).
    /// </summary>
    [RelayCommand]
    private void Open(ConversationItemViewModel item)
    {
        if (IsSelecting)
        {
            Toggle(item);
            return;
        }

        ClearSelection();
        _workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(item.Id)));
    }

    // ── Multi-select (requirements v0.12 §8.2) ─────────────────────────────────────────────────────────────────────

    /// <summary>The 选择 mode: every row shows a checkbox and a click ticks it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSelectionBar))]
    public partial bool IsSelecting { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectionLabel), nameof(ShowsSelectionBar))]
    [NotifyCanExecuteChangedFor(nameof(ArchiveSelectedCommand), nameof(DeleteSelectedCommand))]
    public partial int SelectedCount { get; private set; }

    public bool HasSelection => SelectedCount > 0;

    public string SelectionLabel => UiText.SelectionCount(SelectedCount);

    /// <summary>The bar at the bottom of the sidebar: while something is selected, or in the 选择 mode.</summary>
    public bool ShowsSelectionBar => HasSelection || IsSelecting;

    /// <summary>Why archiving part of the selection did not happen.</summary>
    [ObservableProperty]
    public partial string? SelectionNotice { get; private set; }

    /// <summary>The selected rows in the order they are listed.</summary>
    public IReadOnlyList<ConversationItemViewModel> Selection => [.. Items.Where(i => i.IsChecked)];

    /// <summary>The last archive of the selection; tests await it.</summary>
    internal Task Archiving { get; private set; } = Task.CompletedTask;

    /// <summary>Ctrl click (Cmd on macOS): adds the row to the selection, or takes it out.</summary>
    public void Toggle(ConversationItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.IsChecked = !item.IsChecked;
        _anchor = item.Id;
        SelectionChanged();
    }

    /// <summary>
    /// Shift click: selects the rows from the anchor (the last row toggled, else the conversation shown, else this one)
    /// to this one, in the order they are listed, in place of what was selected.
    /// </summary>
    public void SelectRange(ConversationItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var to = Items.IndexOf(item);
        if (to < 0)
        {
            return;
        }

        var anchor = Items.FirstOrDefault(i => i.Id == _anchor) ?? Items.FirstOrDefault(i => i.IsSelected) ?? item;
        _anchor = anchor.Id;
        var from = Items.IndexOf(anchor);
        var (low, high) = from <= to ? (from, to) : (to, from);
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].IsChecked = i >= low && i <= high;
        }

        SelectionChanged();
    }

    /// <summary>Esc, the bar's clear button, a plain click, or the 选择 button again: nothing selected, the mode ends.</summary>
    [RelayCommand]
    public void ClearSelection()
    {
        foreach (var item in Items)
        {
            item.IsChecked = false;
        }

        _anchor = null;
        IsSelecting = false;
        SelectionNotice = null;
        SelectionChanged();
    }

    [RelayCommand]
    private void ToggleSelectionMode()
    {
        if (IsSelecting)
        {
            ClearSelection();
        }
        else
        {
            IsSelecting = true;
        }
    }

    /// <summary>
    /// Archives each selected conversation whose parent is not selected too, with its whole subtree, as a single archive
    /// does. A refusal (work running) is named and those rows stay selected; the rest go.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ArchiveSelectedAsync()
    {
        Archiving = ArchiveSelectionAsync();
        return Archiving;
    }

    private async Task ArchiveSelectionAsync()
    {
        SelectionNotice = null;
        var roots = SelectionRoots();
        var (busy, storageFailed) = await ConversationArchiving.ArchiveAllAsync(_conversations, roots.Select(r => r.Id));
        if (storageFailed)
        {
            SelectionNotice = UiText.ArchiveStorageFailed;
            return;
        }

        if (busy.Count > 0)
        {
            var titles = Items.ToDictionary(i => i.Id, i => i.Title);
            var keep = roots.Where(r => busy.Any(b => b == r.Id || IsBelow(b, r.Id))).Select(r => r.Id).ToHashSet();
            foreach (var item in Items.Where(i => i.IsChecked && !keep.Contains(i.Id) && !keep.Any(k => IsBelow(i.Id, k))))
            {
                item.IsChecked = false;
            }

            SelectionChanged();
            SelectionNotice = UiText.ArchiveRefused([.. busy.Select(b => titles.GetValueOrDefault(b, UiText.NewConversation))]);
            return;
        }

        ClearSelection();
    }

    /// <summary>One confirmation for the union of the selected conversations' scopes (the window's dialog).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelected()
    {
        if (_workbench.CurrentWorkspace is { } workspace && HasSelection)
        {
            SelectionNotice = null;
            _deletionPrompt.Open(workspace.Id, [.. Selection.Select(i => i.Id)]);
        }
    }

    /// <summary>Selected rows with no selected ancestor: each one stands for its subtree.</summary>
    private List<ConversationItemViewModel> SelectionRoots()
    {
        var selected = Selection.Select(i => i.Id).ToHashSet();
        return [.. Selection.Where(i => !Ancestors(i.Id).Any(selected.Contains))];
    }

    private bool IsBelow(ConversationId conversation, ConversationId ancestor) => Ancestors(conversation).Contains(ancestor);

    private IEnumerable<ConversationId> Ancestors(ConversationId conversation)
    {
        var parents = Items.Where(i => i.ParentId is not null).ToDictionary(i => i.Id, i => i.ParentId!.Value);
        var seen = new HashSet<ConversationId>();
        while (parents.TryGetValue(conversation, out var parent) && seen.Add(parent))
        {
            yield return parent;
            conversation = parent;
        }
    }

    private void SelectionChanged() => SelectedCount = Items.Count(i => i.IsChecked);

    // ── A row's context menu (2026-11-10) ──────────────────────────────────────────────────────────────────────────
    // The page toolbar's actions on one row, through the same services and dialogs: renaming in the row itself,
    // summarizing in the background (the row's spinner shows it), archiving as the bar does, deleting through the
    // window's dialog that states the scope and backs up first. Refusals are named in a notice above the sidebar's foot.

    /// <summary>Storage takes no writes; every row's menu disables what writes.</summary>
    [ObservableProperty]
    public partial bool IsStoragePaused { get; private set; }

    partial void OnIsStoragePausedChanged(bool value)
    {
        foreach (var item in Items)
        {
            item.IsStoragePaused = value;
        }
    }

    /// <summary>Why a row's menu action did not happen, named after its conversation.</summary>
    [ObservableProperty]
    public partial string? RowNotice { get; private set; }

    /// <summary>
    /// Whether a row's context menu opens. Not while rows are selected or in the 选择 mode: the bar at the sidebar's foot
    /// acts on the selection, and a menu on one row would leave unclear whether it acts on that row or on the ticked ones.
    /// (A row being renamed shows its box instead, which has the text field's own menu.)
    /// </summary>
    public bool OffersRowMenu => !ShowsSelectionBar;

    /// <summary>The menu's rename started on a row; the view moves the focus into its box.</summary>
    public event EventHandler<ConversationItemViewModel>? RenameStarted;

    /// <summary>A row's rename is being saved or cancelled (raised just before its box goes); the view gives the focus back to the row if the box had it.</summary>
    public event EventHandler<ConversationItemViewModel>? RenameEnded;

    /// <summary>The last action started from a row's menu; tests await it.</summary>
    internal Task RowAction { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private void CloseRowNotice() => RowNotice = null;

    /// <summary>改名: the row's title turns into a box holding it; Enter or leaving the box saves, Esc cancels.</summary>
    [RelayCommand]
    private void BeginRenameRow(ConversationItemViewModel item)
    {
        RowNotice = null;
        if (!item.CanRename)
        {
            return;
        }

        item.RenameText = item.Title;
        item.IsRenaming = true;
        RenameStarted?.Invoke(this, item);
    }

    /// <summary>Saves the name as the page's 保存 does (an empty name changes nothing); once, however the box was left.</summary>
    [RelayCommand]
    private Task CommitRenameRowAsync(ConversationItemViewModel item)
    {
        if (!item.IsRenaming)
        {
            return Task.CompletedTask;
        }

        RenameEnded?.Invoke(this, item);
        item.IsRenaming = false;
        RowAction = RenameAsync(item, item.RenameText);
        return RowAction;
    }

    [RelayCommand]
    private void CancelRenameRow(ConversationItemViewModel item)
    {
        if (item.IsRenaming)
        {
            RenameEnded?.Invoke(this, item);
            item.IsRenaming = false;
        }
    }

    private async Task RenameAsync(ConversationItemViewModel item, string text)
    {
        try
        {
            await _conversations.RenameAsync(item.Id, text, CancellationToken.None);
        }
        catch (StorageWriteException)
        {
            RowNotice = UiText.RowNotice(item.Title, UiText.ArchiveStorageFailed);
        }
    }

    /// <summary>
    /// 总结此会话: starts the summary the page's button starts, in the background, without opening the page. The row's
    /// spinner shows it running; its page shows it, its result and a failure as usual; a refusal is named here.
    /// </summary>
    [RelayCommand]
    private Task SummarizeRowAsync(ConversationItemViewModel item)
    {
        RowNotice = null;
        if (!item.CanSummarize)
        {
            return Task.CompletedTask;
        }

        RowAction = SummarizeAsync(item);
        return RowAction;
    }

    private async Task SummarizeAsync(ConversationItemViewModel item)
    {
        var outcome = await _summaries.SummarizeAsync(item.Id, CancellationToken.None);
        if (outcome.Rejection is { } rejection)
        {
            RowNotice = UiText.RowNotice(item.Title, UiText.SummarizeRejected(rejection));
        }
    }

    /// <summary>新建分支: a branch that continues the conversation from its end, as the page's button makes; it opens.</summary>
    [RelayCommand]
    private Task ContinueInBranchRowAsync(ConversationItemViewModel item)
    {
        RowNotice = null;
        if (!item.CanContinue)
        {
            return Task.CompletedTask;
        }

        RowAction = ContinueInBranchAsync(item);
        return RowAction;
    }

    private async Task ContinueInBranchAsync(ConversationItemViewModel item)
    {
        var outcome = await _conversations.ContinueInBranchAsync(item.Id, CancellationToken.None);
        if (outcome.Rejection is { } rejection)
        {
            RowNotice = UiText.RowNotice(item.Title, UiText.BranchRejected(rejection));
            return;
        }

        _workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(outcome.Child)));
    }

    /// <summary>归档: the conversation with its branches, as the page's button and the bar do; a refusal is named.</summary>
    [RelayCommand]
    private Task ArchiveRowAsync(ConversationItemViewModel item)
    {
        RowNotice = null;
        if (!item.CanArchive)
        {
            return Task.CompletedTask;
        }

        RowAction = ArchiveAsync(item);
        return RowAction;
    }

    private async Task ArchiveAsync(ConversationItemViewModel item)
    {
        var title = item.Title;
        var (busy, storageFailed) = await ConversationArchiving.ArchiveAllAsync(_conversations, [item.Id]);
        if (storageFailed)
        {
            RowNotice = UiText.RowNotice(title, UiText.ArchiveStorageFailed);
        }
        else if (busy.Count > 0 && _workbench.CurrentWorkspace is { } workspace)
        {
            RowNotice = UiText.ArchiveRefused(await ConversationArchiving.TitlesAsync(_conversations, workspace.Id, busy));
        }
    }

    /// <summary>删除…: the window's dialog states what goes, offers archiving instead and backs up first, as for a selection.</summary>
    [RelayCommand]
    private void DeleteRow(ConversationItemViewModel item)
    {
        RowNotice = null;
        if (item.CanDelete && _workbench.CurrentWorkspace is { } workspace)
        {
            _deletionPrompt.Open(workspace.Id, [item.Id]);
        }
    }

    /// <summary>"Share…": the dialog opens with the row's exploration ticked (docs/design/sharing.md S1).</summary>
    [RelayCommand]
    private void ShareRow(ConversationItemViewModel item)
    {
        RowNotice = null;
        if (_workbench.CurrentWorkspace is { } workspace)
        {
            _share.Open(workspace.Id, item.Id);
        }
    }

    [RelayCommand]
    private void New() => _workbench.Navigate(new NavigationRequest(MainSection.Explore, NewConversationRoute.Instance));

    private void OnWorkbenchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Workbench.CurrentWorkspace) && _workbench.CurrentWorkspace?.Id != _shown)
        {
            ClearSelection();
            RowNotice = null;
            Loaded = _load.Request();
        }
        else if (e.PropertyName == nameof(Workbench.CurrentPage))
        {
            // Seen as soon as it is on screen: the row loses its dot now, not at the next reload.
            _marks.SetViewing(Shown());
            UpdateSelection();
            UpdateMarks();
        }
    }

    private async Task ReloadAsync()
    {
        if (_workbench.CurrentWorkspace is not { } workspace)
        {
            return;
        }

        _shown = workspace.Id;
        var summaries = await _conversations.ListAsync(workspace.Id, CancellationToken.None);
        if (_shown != workspace.Id)
        {
            return;
        }

        // Update in place so selection and scroll position in the list are not disturbed.
        ArchivedCount = summaries.Count(s => s.IsArchived);
        var tree = AsTree([.. summaries.Where(s => !s.IsHidden)]);
        var existing = Items.ToDictionary(i => i.Id);
        var ordered = tree.Select(t => existing.GetValueOrDefault(t.Summary.Conversation.Id) ?? new ConversationItemViewModel(t.Summary.Conversation.Id, this) { IsStoragePaused = IsStoragePaused }).ToList();
        for (var i = 0; i < tree.Count; i++)
        {
            ordered[i].Title = UiText.ConversationTitle(tree[i].Summary.Conversation, tree[i].Summary.FirstQuestion, tree[i].Summary.Quote);
            ordered[i].LastAnswerState = tree[i].Summary.LastAnswerState;
            ordered[i].Depth = tree[i].Depth;
            ordered[i].IsUnexplored = tree[i].Summary.IsUnexplored;
            ordered[i].ParentId = tree[i].Summary.Parent;
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            if (i < Items.Count && Items[i] == ordered[i])
            {
                continue;
            }

            if (Items.Contains(ordered[i]))
            {
                Items.Move(Items.IndexOf(ordered[i]), i);
            }
            else
            {
                Items.Insert(i, ordered[i]);
            }
        }

        while (Items.Count > ordered.Count)
        {
            Items[^1].IsChecked = false;
            Items.RemoveAt(Items.Count - 1);
        }

        // Rows that went (deleted, archived, another workspace) leave the selection with them.
        SelectionChanged();

        UpdateMarks();
        UpdateSelection();
    }

    /// <summary>Every row's marker from what is running now, its last answer's stored state and the unread marks.</summary>
    private void UpdateMarks()
    {
        foreach (var item in Items)
        {
            item.Mark = MarkOf(item);
        }

        // What archiving or deleting a row would take along: the listed rows below it (archived ones are not listed and run nothing).
        var busy = Items.Where(i => i.IsGenerating).Select(i => i.Id).ToList();
        var below = busy.SelectMany(Ancestors).ToHashSet();
        foreach (var item in Items)
        {
            item.IsBusyBelow = below.Contains(item.Id);
        }

        HasAttention = Items.Any(i => i.NeedsAttention);
        HasUnread = Items.Any(i => i.IsUnread);
    }

    private ConversationMark MarkOf(ConversationItemViewModel item)
    {
        if (_conversations.HasUnfinishedAnswer(item.Id))
        {
            return ConversationMark.Answering;
        }

        if (_marks.IsSummarizing(item.Id))
        {
            return ConversationMark.Summarizing;
        }

        if (item.LastAnswerState is MessageState.Failed or MessageState.Interrupted)
        {
            return ConversationMark.NeedsAttention;
        }

        return _marks.IsUnread(item.Id) ? ConversationMark.Unread : ConversationMark.None;
    }

    private ConversationId? Shown() => (_workbench.CurrentPage?.Route as ConversationRoute)?.Id;

    /// <summary>Depth-first order of the branch tree; a branch whose parent is missing is shown as a root.</summary>
    internal static List<(ConversationSummary Summary, int Depth)> AsTree(IReadOnlyList<ConversationSummary> summaries)
    {
        var known = summaries.Select(s => s.Conversation.Id).ToHashSet();
        var children = summaries.Where(s => s.Parent is { } p && known.Contains(p))
            .GroupBy(s => s.Parent!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Conversation.CreatedAt).ToList());
        var latest = new Dictionary<ConversationId, DateTimeOffset>();
        var roots = summaries.Where(s => s.Parent is not { } p || !known.Contains(p))
            .Select((s, index) => (Summary: s, Index: index, Latest: Latest(s)))
            .OrderByDescending(r => r.Latest).ThenBy(r => r.Index)
            .Select(r => r.Summary);
        var result = new List<(ConversationSummary, int)>(summaries.Count);
        foreach (var root in roots)
        {
            Visit(root, 0);
        }

        return result;

        DateTimeOffset Latest(ConversationSummary summary)
        {
            if (!latest.TryGetValue(summary.Conversation.Id, out var value))
            {
                value = summary.Conversation.UpdatedAt;
                foreach (var child in children.GetValueOrDefault(summary.Conversation.Id) ?? [])
                {
                    value = value > Latest(child) ? value : Latest(child);
                }

                latest[summary.Conversation.Id] = value;
            }

            return value;
        }

        void Visit(ConversationSummary summary, int depth)
        {
            result.Add((summary, depth));
            foreach (var child in children.GetValueOrDefault(summary.Conversation.Id) ?? [])
            {
                Visit(child, depth + 1);
            }
        }
    }

    private void UpdateSelection()
    {
        var current = Shown();
        foreach (var item in Items)
        {
            item.IsSelected = item.Id == current;
        }
    }
}
