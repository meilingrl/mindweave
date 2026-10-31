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

    /// <summary>A light marker, not a permanent status label (requirements §6.1): generating, or needs attention.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMarker), nameof(IsGenerating), nameof(NeedsAttention))]
    public partial string? Marker { get; set; }

    [ObservableProperty]
    public partial string? MarkerHint { get; set; }

    public bool HasMarker => Marker is not null;

    /// <summary>Drawn as a small accent dot.</summary>
    public bool IsGenerating => Marker == GeneratingMarker;

    /// <summary>Drawn as a small round error badge with "!".</summary>
    public bool NeedsAttention => Marker == AttentionMarker;

    internal const string GeneratingMarker = "●";

    internal const string AttentionMarker = "!";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Branch level: 0 for a root conversation. Shown as indentation, not as a separate tree control.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Indent), nameof(IsBranch))]
    public partial int Depth { get; set; }

    /// <summary>A branch, drawn with a small corner glyph so the tree reads as a hierarchy, not only as indentation.</summary>
    public bool IsBranch => Depth > 0;

    public Avalonia.Thickness Indent => new(Math.Min(Depth, MaxIndentedDepth) * 14, 0, 0, 0);

    /// <summary>Deeper levels share the last indentation so long chains stay readable in a narrow sidebar.</summary>
    internal const int MaxIndentedDepth = 6;
}

/// <summary>
/// The active workspace's conversations as a branch tree: root conversations newest activity first (activity
/// anywhere in their tree counts), each branch under its parent in creation order. Archived conversations and
/// everything below them are left out; an entry at the bottom leads to them. Kept in sync with storage and generation.
/// </summary>
public sealed partial class ConversationListViewModel : ViewModelBase
{
    private readonly ConversationService _conversations;
    private readonly GenerationCoordinator _generation;
    private readonly Workbench _workbench;
    private readonly SerialRefresh _load;
    private readonly UiThrottle _reload;
    private WorkspaceId? _shown;

    public ConversationListViewModel(ConversationService conversations, GenerationCoordinator generation, Workbench workbench, IUiDispatcher dispatcher)
    {
        _conversations = conversations;
        _generation = generation;
        _workbench = workbench;
        _load = new SerialRefresh(ReloadAsync);
        _reload = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(100), () => Loaded = _load.Request());
        _conversations.ConversationChanged += (_, _) => _reload.Request();
        _generation.Changed += (_, _) => _reload.Request();
        _workbench.PropertyChanged += OnWorkbenchChanged;
    }

    public ObservableCollection<ConversationItemViewModel> Items { get; } = [];

    /// <summary>The most recent reload; tests await it.</summary>
    internal Task Loaded { get; private set; } = Task.CompletedTask;

    /// <summary>How many conversations of the workspace the user archived (the "已归档（N）" entry).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArchived), nameof(ArchivedLabel))]
    public partial int ArchivedCount { get; private set; }

    public bool HasArchived => ArchivedCount > 0;

    public string ArchivedLabel => UiText.ArchivedEntry(ArchivedCount);

    [RelayCommand]
    private void OpenArchived() => _workbench.Navigate(new NavigationRequest(MainSection.Explore, ArchivedRoute.Instance));

    [RelayCommand]
    private void Open(ConversationItemViewModel item) =>
        _workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(item.Id)));

    [RelayCommand]
    private void New() => _workbench.Navigate(new NavigationRequest(MainSection.Explore, NewConversationRoute.Instance));

    private void OnWorkbenchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Workbench.CurrentWorkspace) && _workbench.CurrentWorkspace?.Id != _shown)
        {
            Loaded = _load.Request();
        }
        else if (e.PropertyName == nameof(Workbench.CurrentPage))
        {
            UpdateSelection();
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
            Apply(ordered[i], tree[i].Summary);
            ordered[i].Depth = tree[i].Depth;
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
            Items.RemoveAt(Items.Count - 1);
        }

        UpdateSelection();
    }

    private void Apply(ConversationItemViewModel item, ConversationSummary summary)
    {
        item.Title = UiText.ConversationTitle(summary.Conversation, summary.FirstQuestion, summary.Quote);
        var running = _generation.HasUnfinishedAnswer(summary.Conversation.Id);
        (item.Marker, item.MarkerHint) = running
            ? (ConversationItemViewModel.GeneratingMarker, UiText.MarkerGenerating)
            : summary.LastAnswerState is MessageState.Failed or MessageState.Interrupted
                ? (ConversationItemViewModel.AttentionMarker, UiText.MarkerNeedsAttention)
                : (null, null);
    }

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
        var current = (_workbench.CurrentPage?.Route as ConversationRoute)?.Id;
        foreach (var item in Items)
        {
            item.IsSelected = item.Id == current;
        }
    }
}
