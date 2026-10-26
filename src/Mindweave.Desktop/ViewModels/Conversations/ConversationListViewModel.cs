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
    [NotifyPropertyChangedFor(nameof(HasMarker))]
    public partial string? Marker { get; set; }

    [ObservableProperty]
    public partial string? MarkerHint { get; set; }

    public bool HasMarker => Marker is not null;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>The active workspace's conversations, newest first, kept in sync with storage and generation.</summary>
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
        var existing = Items.ToDictionary(i => i.Id);
        var ordered = summaries.Select(s => existing.GetValueOrDefault(s.Conversation.Id) ?? new ConversationItemViewModel(s.Conversation.Id)).ToList();
        for (var i = 0; i < summaries.Count; i++)
        {
            Apply(ordered[i], summaries[i]);
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
        item.Title = summary.Conversation.Title
            ?? (summary.FirstQuestion is { } q ? ConversationRules.FallbackTitle(q) : UiText.NewConversation);
        var running = _generation.HasUnfinishedAnswer(summary.Conversation.Id);
        (item.Marker, item.MarkerHint) = running
            ? ("●", UiText.MarkerGenerating)
            : summary.LastAnswerState is MessageState.Failed or MessageState.Interrupted
                ? ("!", UiText.MarkerNeedsAttention)
                : (null, null);
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
