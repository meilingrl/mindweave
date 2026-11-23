using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Desktop.ViewModels.Pages;

// AI-suggested links the user confirms (requirements §10.3.4, 2026-11-16): the toolbar's 建议关联, the dash-dotted lines,
// and the review list in the details (nothing selected) and in the list view.
public sealed partial class SummaryNetworkHomeViewModel
{
    private ILinkSuggestions? _suggestions;
    private SummaryNodeId? _selectedWhenAsked;
    private bool _asked;

    /// <summary>The workspace's pending suggestions with 建议关联, 接受, 忽略 and 全部忽略; null without the service.</summary>
    public LinkSuggestionsPanel? Suggestions { get; private set; }

    /// <summary>The latest reload of the suggestions after a change (tests).</summary>
    internal Task SuggestionsReloaded { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The pending suggestions drawn (requirements §10.3.4): light dash-dot lines between summaries at their positions in
    /// memory. They follow 显示关联: empty while 隐藏关联 is on. Never used by 整理布局, never part of the layout.
    /// </summary>
    public IReadOnlyList<SuggestionEdgeView> SuggestionEdges { get; private set; } = [];

    /// <summary>The suggestion whose line was clicked: its row in the details stands out.</summary>
    public LinkSuggestionId? HighlightedSuggestion { get; private set; }

    /// <summary>The legend explains the dash-dot line only while one is drawn.</summary>
    public bool ShowsSuggestionLegend => SuggestionEdges.Count > 0 && !IsListView;

    /// <summary>The toolbar's 关联 menu: 建议关联, and 显示／隐藏关联 once the workspace has links (one row at 1100 px).</summary>
    public bool ShowsLinksMenu => _links is not null || Suggestions is not null;

    /// <summary>The details (nothing selected) list the suggestions; the list view has its own section instead.</summary>
    public bool ShowsSuggestionsInDetails => Suggestions is { HasItems: true } && !IsListView;

    /// <summary>The list view lists the suggestions under the summaries.</summary>
    public bool ShowsSuggestionsInList => Suggestions is { HasItems: true };

    /// <summary>A click on a suggestion's words: nothing is selected, and its row in the details stands out with 接受 and 忽略.</summary>
    public void SelectSuggestion(LinkSuggestionId id)
    {
        Select(null);
        HighlightedSuggestion = id;
        Suggestions?.Highlight(id);
        OnPropertyChanged(nameof(ShowsDetails));
        OnPropertyChanged(nameof(ShowsDetailsButton));
        ViewChanged?.Invoke(this, false);
    }

    private void AttachSuggestions(ILinkSuggestions? suggestions)
    {
        _suggestions = suggestions;
        if (suggestions is null)
        {
            return;
        }

        Suggestions = new LinkSuggestionsPanel(suggestions, Workspace.Id, JumpFromSuggestion, () => _navigator.Navigate(new Navigation.NavigationRequest(Navigation.MainSection.Settings)));
        Suggestions.Items.CollectionChanged += OnSuggestionItemsChanged;
        Suggestions.PropertyChanged += OnSuggestionsPanelChanged;
        Suggestions.Suggested += OnSuggested;
        suggestions.Changed += OnSuggestionsChanged;
    }

    private void DetachSuggestions()
    {
        if (_suggestions is not null)
        {
            _suggestions.Changed -= OnSuggestionsChanged;
        }

        if (Suggestions is not null)
        {
            Suggestions.Items.CollectionChanged -= OnSuggestionItemsChanged;
            Suggestions.PropertyChanged -= OnSuggestionsPanelChanged;
            Suggestions.Suggested -= OnSuggested;
        }
    }

    /// <summary>A request the user started here stored suggestions: the details show them, unless another summary was picked meanwhile.</summary>
    private void OnSuggested(object? sender, EventArgs e)
    {
        // They were asked for here and can only be read in the details.
        Details.Collapse(false);
        if (Selected?.Id == _selectedWhenAsked && Selected is not null)
        {
            Select(null);
        }

        _asked = false;
    }

    /// <summary>Reads the suggestions again and redraws them (on the UI thread).</summary>
    private Task ReloadSuggestionsAsync()
    {
        if (Suggestions is null)
        {
            return Task.CompletedTask;
        }

        SuggestionsReloaded = ReloadAndBuildAsync(Suggestions);
        return SuggestionsReloaded;
    }

    private async Task ReloadAndBuildAsync(LinkSuggestionsPanel panel)
    {
        await panel.ReloadAsync();
        if (!_disposed)
        {
            BuildSuggestionViews();
            ViewChanged?.Invoke(this, false);
        }
    }

    private void OnSuggestionsChanged(object? sender, WorkspaceId workspace)
    {
        if (workspace != Workspace.Id || _disposed)
        {
            return;
        }

        _dispatcher.Post(() => _ = ReloadSuggestionsAsync());
    }

    private void OnSuggestionItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (HighlightedSuggestion is { } id && Suggestions is { } panel && panel.Items.All(i => i.Id != id))
        {
            HighlightedSuggestion = null;
        }

        BuildSuggestionViews();
        OnPropertyChanged(nameof(ShowsSuggestionsInList));
        ViewChanged?.Invoke(this, false);
    }

    private void OnSuggestionsPanelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // The selection when 建议关联 started: if it is unchanged when the suggestions arrive, the details show them instead.
        if (e.PropertyName == nameof(LinkSuggestionsPanel.IsRunning) && Suggestions is { IsRunning: true } && !_asked)
        {
            _asked = true;
            _selectedWhenAsked = Selected?.Id;
        }

        if (e.PropertyName is nameof(LinkSuggestionsPanel.HasContent) or nameof(LinkSuggestionsPanel.HasItems))
        {
            OnPropertyChanged(nameof(ShowsSuggestionsInDetails));
            OnPropertyChanged(nameof(ShowsSuggestionsInList));
        }
    }

    /// <summary>A summary of a suggestion: selected and brought to the centre (from the list view, the network shows again).</summary>
    private void JumpFromSuggestion(SummaryNodeId node)
    {
        IsListView = false;
        JumpTo(node);
    }

    /// <summary>The suggestion lines between drawn summaries, at their positions in memory; none while links are hidden.</summary>
    private void BuildSuggestionViews()
    {
        var nodes = Nodes.ToDictionary(n => n.Id);
        SuggestionEdges = _linksHidden || Suggestions is null
            ? []
            : [.. Suggestions.Items.Select(i => i.Suggestion).Where(s => nodes.ContainsKey(s.NodeA) && nodes.ContainsKey(s.NodeB)).Select(s => new SuggestionEdgeView(
                s.Id, s.NodeA, s.NodeB, nodes[s.NodeA].X, nodes[s.NodeA].Y, nodes[s.NodeB].X, nodes[s.NodeB].Y, s.Note))];
        OnPropertyChanged(nameof(SuggestionEdges));
        OnPropertyChanged(nameof(ShowsSuggestionLegend));
    }
}
