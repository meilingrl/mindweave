using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Conversations;

public sealed partial class ConversationItemViewModel(ConversationId id) : ViewModelBase
{
    public ConversationId Id { get; } = id;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>
    /// The one light marker the row shows (requirements §6.1: no permanent status label), most urgent first: running,
    /// needs attention, unread.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMarker), nameof(IsGenerating), nameof(NeedsAttention), nameof(IsUnread), nameof(MarkerHint))]
    public partial ConversationMark Mark { get; set; }

    /// <summary>Tooltip and accessible name of the marker; null without one.</summary>
    public string? MarkerHint => Mark switch
    {
        ConversationMark.Answering => UiText.MarkerGenerating,
        ConversationMark.Summarizing => UiText.MarkerSummarizing,
        ConversationMark.NeedsAttention => UiText.MarkerNeedsAttention,
        ConversationMark.Unread => UiText.MarkerNewAnswer,
        _ => null,
    };

    public bool HasMarker => Mark != ConversationMark.None;

    /// <summary>Drawn as a small accent spinner: an answer is queued or being written, or a summary is.</summary>
    public bool IsGenerating => Mark is ConversationMark.Answering or ConversationMark.Summarizing;

    /// <summary>Drawn as a small round error badge with "!".</summary>
    public bool NeedsAttention => Mark == ConversationMark.NeedsAttention;

    /// <summary>Drawn as a small accent dot: an answer finished while the conversation was not on screen.</summary>
    public bool IsUnread => Mark == ConversationMark.Unread;

    /// <summary>The stored state of the last answer, kept so the marker can be worked out again without a reload.</summary>
    internal MessageState? LastAnswerState { get; set; }

    /// <summary>The conversation shown in the content area (the row's "current" look), not the multi-selection.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Part of the sidebar's multi-selection (Ctrl/Cmd or Shift click, or the 选择 mode's checkbox).</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>The conversation it was branched from, when that one is listed too.</summary>
    internal ConversationId? ParentId { get; set; }

    /// <summary>Branch level: 0 for a root conversation. The view shows it as indentation (<c>Views.TreeIndent</c>), not as a separate tree control.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBranch))]
    public partial int Depth { get; set; }

    /// <summary>A branch, drawn with a small corner glyph so the tree reads as a hierarchy, not only as indentation.</summary>
    public bool IsBranch => Depth > 0;

}

public enum ConversationMark
{
    None,
    Answering,
    Summarizing,
    NeedsAttention,
    Unread,
}

/// <summary>
/// The active workspace's conversations as a branch tree: root conversations newest activity first (activity
/// anywhere in their tree counts), each branch under its parent in creation order. Archived conversations and
/// everything below them are left out; an entry at the bottom leads to them. Kept in sync with storage and generation.
/// </summary>
public sealed partial class ConversationListViewModel : ViewModelBase
{
    private readonly IConversations _conversations;
    private readonly IConversationMarks? _marks;
    private readonly Workbench _workbench;
    private readonly SerialRefresh _load;
    private readonly UiThrottle _reload;
    private readonly ConversationDeletionRequests? _deletionPrompt;
    private WorkspaceId? _shown;

    /// <summary>Where a Shift click's range starts: the last row toggled, or the conversation shown.</summary>
    private ConversationId? _anchor;

    public ConversationListViewModel(IConversations conversations, Workbench workbench, IUiDispatcher dispatcher, IConversationMarks? marks = null, ConversationDeletionRequests? deletion = null)
    {
        _conversations = conversations;
        _deletionPrompt = deletion;
        if (deletion is not null)
        {
            // The dialog deleted or archived the selection (or chose 改为归档): nothing of it is left to act on.
            deletion.Completed += (_, _) => ClearSelection();
        }

        _marks = marks;
        _workbench = workbench;
        _load = new SerialRefresh(ReloadAsync);
        _reload = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(100), () => Loaded = _load.Request());
        _conversations.ConversationChanged += (_, _) => _reload.Request();
        _conversations.ActivityChanged += (_, _) => _reload.Request();
        if (_marks is not null)
        {
            _marks.Changed += (_, _) => _reload.Request();
        }

        _workbench.PropertyChanged += OnWorkbenchChanged;
        _marks?.SetViewing(Shown());
    }

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
            _deletionPrompt?.Open(workspace.Id, [.. Selection.Select(i => i.Id)]);
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

    [RelayCommand]
    private void New() => _workbench.Navigate(new NavigationRequest(MainSection.Explore, NewConversationRoute.Instance));

    private void OnWorkbenchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Workbench.CurrentWorkspace) && _workbench.CurrentWorkspace?.Id != _shown)
        {
            ClearSelection();
            Loaded = _load.Request();
        }
        else if (e.PropertyName == nameof(Workbench.CurrentPage))
        {
            // Seen as soon as it is on screen: the row loses its dot now, not at the next reload.
            _marks?.SetViewing(Shown());
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
        var ordered = tree.Select(t => existing.GetValueOrDefault(t.Summary.Conversation.Id) ?? new ConversationItemViewModel(t.Summary.Conversation.Id)).ToList();
        for (var i = 0; i < tree.Count; i++)
        {
            ordered[i].Title = UiText.ConversationTitle(tree[i].Summary.Conversation, tree[i].Summary.FirstQuestion, tree[i].Summary.Quote);
            ordered[i].LastAnswerState = tree[i].Summary.LastAnswerState;
            ordered[i].Depth = tree[i].Depth;
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

        HasAttention = Items.Any(i => i.NeedsAttention);
        HasUnread = Items.Any(i => i.IsUnread);
    }

    private ConversationMark MarkOf(ConversationItemViewModel item)
    {
        if (_conversations.HasUnfinishedAnswer(item.Id))
        {
            return ConversationMark.Answering;
        }

        if (_marks?.IsSummarizing(item.Id) == true)
        {
            return ConversationMark.Summarizing;
        }

        if (item.LastAnswerState is MessageState.Failed or MessageState.Interrupted)
        {
            return ConversationMark.NeedsAttention;
        }

        return _marks?.IsUnread(item.Id) == true ? ConversationMark.Unread : ConversationMark.None;
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
