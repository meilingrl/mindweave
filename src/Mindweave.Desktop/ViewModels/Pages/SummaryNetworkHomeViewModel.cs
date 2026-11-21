using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Conversations;
using Mindweave.Application.SummaryNetwork;
using Mindweave.Application.Recommendations;
using Mindweave.Application.Summaries;
using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// The summary network of the current workspace (requirement §7.3–7.4, architecture §11): nodes with short titles
/// and real source edges, selection with details, search, show all, zoom, explicit arrange and save. State lives in
/// the workspace's <see cref="SummaryNetworkSession"/>, so the viewport and selection survive reading a summary and coming
/// back. A list view of the same summaries stays available for keyboard and screen-reader use.
/// </summary>
public sealed partial class SummaryNetworkHomeViewModel : PageViewModel, IDisposable
{
    private readonly ISummaries _summaries;
    private readonly IConversations _conversations;
    private readonly IWorkbenchNavigator _navigator;
    private readonly SerialRefresh _load;
    private readonly UiThrottle _reload;
    private readonly IRecommendations? _recommendations;
    private readonly Application.Preferences.IRecommendationSettings? _recommendationSettings;
    private readonly IUiDispatcher _dispatcher;
    private readonly SerialRefresh _recommendationLoad;
    private Dictionary<ConversationId, string> _titles = [];
    private NodeOffer _offer = NodeOffer.Off;
    private RecommendationId? _selectedRecommendation;
    private readonly ISummaryLinks? _links;
    private readonly SerialRefresh _linkLoad;
    private IReadOnlyList<SummaryLink> _workspaceLinks = [];
    private bool _linksHidden;
    private bool _linking;
    private bool _linkToolsShown;
    private bool _disposed;

    // Built in place of a page a language switch replaced: its first load is not an opening (review of merge 2, M4).
    private bool _replayed;

    public SummaryNetworkHomeViewModel(
        Workspace workspace,
        SummaryNetworkSession session,
        ISummaries summaries,
        IConversations conversations,
        IWorkbenchNavigator navigator,
        IUiDispatcher dispatcher,
        IRecommendations? recommendations = null,
        Application.Preferences.IRecommendationSettings? recommendationSettings = null,
        ISummaryLinks? links = null,
        ILinkSuggestions? suggestions = null,
        bool replayed = false)
        : base(SummaryNetworkHomeRoute.Instance)
    {
        _replayed = replayed;
        Workspace = workspace;
        Session = session;
        _summaries = summaries;
        _conversations = conversations;
        _navigator = navigator;
        _dispatcher = dispatcher;
        _recommendations = recommendations;
        _recommendationSettings = recommendationSettings;
        _load = new SerialRefresh(LoadAsync);
        _recommendationLoad = new SerialRefresh(ReloadRecommendationsAsync);
        _links = links;
        _linkLoad = new SerialRefresh(ReloadLinksAsync);
        Links = links is null ? null : new SummaryLinksPanel(links, JumpTo, opens: false);
        if (Links is not null)
        {
            Links.PropertyChanged += OnLinksPanelChanged;
        }

        if (_links is not null)
        {
            _links.Changed += OnLinksChanged;
        }

        AttachSuggestions(suggestions);
        _reload = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(30), () => _ = _load.Request());
        _summaries.Changed += OnChanged;
        _conversations.VisibilityChanged += OnVisibilityChanged;

        // Every conversation is a dot in the session network: a new one, a new question (its weight) or a new title shows.
        if (IsSessionNetwork)
        {
            _conversations.ConversationChanged += OnChanged;
        }

        Session.PropertyChanged += OnSessionChanged;
        Session.Discarded += OnDiscarded;
        if (_recommendations is not null)
        {
            _recommendations.Changed += OnRecommendationsChanged;
        }

        if (_recommendationSettings is not null)
        {
            _recommendationSettings.Changed += OnRecommendationSettingsChanged;
        }

        Loaded = _load.Request();
    }

    public Workspace Workspace { get; }

    public SummaryNetworkSession Session { get; }

    /// <summary>"Mindweave" behind the session network's gate, "总结网络" without it.</summary>
    public override string Title => IsSessionNetwork ? UiText.SessionNetworkTitle : UiText.SummaryNetworkTitle;

    /// <summary>The session network is on: every conversation a dot, a 空间 | 时间线 switch, and no layout to arrange or save.</summary>
    public bool IsSessionNetwork => Session.IsSessionNetwork;

    /// <summary>The session network shows the world-line timeline (时间线) rather than the spatial view (空间).</summary>
    public bool IsTimeline => IsSessionNetwork && Session.ViewMode == SummaryNetworkViewMode.Timeline;

    internal Task Loaded { get; }

    public ObservableCollection<SummaryListItem> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    public IReadOnlyList<NodeView> Nodes { get; private set; } = [];

    public IReadOnlyList<EdgeView> Edges { get; private set; } = [];

    public IReadOnlyList<GroupCaption> Captions { get; private set; } = [];

    /// <summary>
    /// The content links drawn (requirements §10.3.3): solid, in the link colour, between two summaries at their positions in
    /// memory. Empty while 隐藏关联 is on. Never used by 整理布局 and never part of the layout.
    /// </summary>
    public IReadOnlyList<LinkEdgeView> LinkEdges { get; private set; } = [];

    /// <summary>The selected summary's links with jump, note editing, delete and the "关联到…" picker; null without the links service.</summary>
    public SummaryLinksPanel? Links { get; }

    /// <summary>The latest reload of the links after a change (tests).</summary>
    internal Task LinksReloaded { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The workspace has links, or hides them: the toolbar offers 显示关联 / 隐藏关联. It appears as "关联到…" starts waiting
    /// for a click and then stays for this page, so the toolbar never gains a row (moving the canvas) under the pointer
    /// right after the click that makes the first link.
    /// </summary>
    public bool ShowsLinkTools => _links is not null && (_linkToolsShown = _linkToolsShown || _workspaceLinks.Count > 0 || _linksHidden || IsPicking);

    /// <summary>隐藏关联 is on for this workspace (remembered across restarts).</summary>
    public bool LinksHidden => _linksHidden;

    public string ToggleLinksLabel => _linksHidden ? UiText.ShowLinks : UiText.HideLinks;

    /// <summary>The legend explains the link line only while one is drawn.</summary>
    public bool ShowsLinkLegend => LinkEdges.Count > 0 && !IsListView;

    /// <summary>"关联到…" is waiting for a click on another summary (Esc or 取消 leaves it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickHint), nameof(ShowsLinkTools))]
    [NotifyCanExecuteChangedFor(nameof(CancelPickCommand))]
    public partial bool IsPicking { get; private set; }

    /// <summary>The line above the canvas while picking: what to do, or why the last click did not link.</summary>
    public string PickHint => PickRefusal ?? UiText.LinkPickHint(Selected?.DisplayName ?? string.Empty);

    /// <summary>Why the last click in pick mode made no link (itself, a recommended node, already linked, a refusal).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickHint), nameof(HasPickRefusal))]
    public partial string? PickRefusal { get; private set; }

    public bool HasPickRefusal => PickRefusal is not null;

    /// <summary>The details show the selected summary (not the keyboard picker, which takes their place while open).</summary>
    public bool ShowsDetail => Selected is not null && Links is not { IsPickerOpen: true };

    /// <summary>The keyboard picker of "关联到…" is open in the details' place.</summary>
    public bool ShowsLinkPicker => Selected is not null && Links is { IsPickerOpen: true };

    /// <summary>
    /// The recommended nodes drawn (requirements §10.3.2): placed beside their basis summaries on every rebuild, never in
    /// the layout. Empty while 隐藏推荐 is on or recommended nodes are turned off.
    /// </summary>
    public IReadOnlyList<RecommendedNodeView> RecommendedNodes { get; private set; } = [];

    /// <summary>The latest reload of the recommended nodes after a change (tests).</summary>
    internal Task RecommendationsReloaded { get; private set; } = Task.CompletedTask;

    /// <summary>The selected recommended node, shown in the details instead of a summary; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial RecommendedNodeView? SelectedRecommendation { get; private set; }

    /// <summary>A summary or a recommended node is selected (the hint in the details panel goes).</summary>
    public bool HasSelection => Selected is not null || SelectedRecommendation is not null;

    /// <summary>Recommended nodes are on and the workspace has a summary they could extend: the toolbar offers them.</summary>
    public bool ShowsRecommendationTools => _offer.Enabled && _offer.SummaryCount > 0;

    /// <summary>隐藏推荐 is on for this workspace (remembered).</summary>
    public bool RecommendationsHidden => _offer.Hidden;

    public string ToggleRecommendationsLabel => _offer.Hidden ? UiText.ShowRecommendedNodes : UiText.HideRecommendedNodes;

    /// <summary>The toolbar button's shorter words for <see cref="ToggleRecommendationsLabel"/> (its accessible name).</summary>
    public string ToggleRecommendationsText => _offer.Hidden ? UiText.ShowRecommendedNodesButton : UiText.HideRecommendedNodesButton;

    /// <summary>The legend explains the dashed ring and the dotted line only while some are drawn.</summary>
    public bool ShowsRecommendationLegend => RecommendedNodes.Count > 0 && !IsListView;

    /// <summary>换一批推荐 (or 重试) runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRecommendationBusy), nameof(RecommendationBusyText))]
    [NotifyCanExecuteChangedFor(nameof(RefreshRecommendationsCommand))]
    public partial bool IsRefreshingRecommendations { get; private set; }

    /// <summary>The spinner line: 换一批推荐 runs, or the background prepares while none is drawn yet.</summary>
    public bool ShowsRecommendationBusy => IsRefreshingRecommendations || (_offer.IsPreparing && !_offer.Hidden && RecommendedNodes.Count == 0);

    public string RecommendationBusyText => IsRefreshingRecommendations ? UiText.RefreshingRecommendedNodes : UiText.PreparingRecommendedNodes;

    /// <summary>Why nothing new came: a manual request explains; a background failure says so quietly.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecommendationNote))]
    public partial string? RecommendationNote { get; private set; }

    [ObservableProperty]
    public partial bool RecommendationNoteCanRetry { get; private set; }

    [ObservableProperty]
    public partial bool RecommendationNoteNeedsSettings { get; private set; }

    public bool HasRecommendationNote => RecommendationNote is not null;

    /// <summary>The selected node and every summarized source up to its exploration's root; empty without a selection.</summary>
    public IReadOnlySet<SummaryNodeId> Trail { get; private set; } = new HashSet<SummaryNodeId>();

    /// <summary>Nodes whose short or full name contains the search text, updated as it is typed.</summary>
    public IReadOnlySet<SummaryNodeId> Matches { get; private set; } = new HashSet<SummaryNodeId>();

    /// <summary>The save button's text: says plainly when the arrangement is not saved.</summary>
    public string SaveLayoutLabel => Session.IsDirty ? UiText.SaveLayoutUnsaved : UiText.LayoutSaved;

    [ObservableProperty]
    public partial bool IsListView { get; set; }

    [ObservableProperty]
    public partial bool WheelZooms { get; set; } = true;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowsDetail), nameof(ShowsLinkPicker), nameof(PickHint), nameof(ShowsLinksSection))]
    public partial NodeDetail? Selected { get; private set; }

    /// <summary>The details offer links and "关联到…": with the links service, and not for a dot without a summary.</summary>
    public bool ShowsLinksSection => Links is not null && Selected is not { HasSummary: false };

    public bool IsDirty => Session.IsDirty;

    public bool IsSaving => Session.IsSaving;

    public bool SaveFailed => Session.SaveFailed;

    /// <summary>The canvas redraws; <c>true</c> when the viewport was reset and should fit everything again.</summary>
    public event EventHandler<bool>? ViewChanged;

    /// <summary>The canvas zooms around its centre by this factor (the + / − buttons).</summary>
    public event EventHandler<double>? ZoomRequested;

    /// <summary>Single click: select and show details. Not reading: the summary's read state does not change.</summary>
    public void Select(SummaryNodeId? node)
    {
        // Selecting another summary ends "关联到…" (its source was the one selected).
        IsPicking = false;
        PickRefusal = null;
        _selectedRecommendation = null;
        SelectedRecommendation = null;
        HighlightedSuggestion = null;
        Suggestions?.Highlight(null);
        Session.Selected = node;
        Selected = node is { } id ? Detail(id) : null;
        Trail = TrailOf(node);
        ShowLinksOfSelected();
        ViewChanged?.Invoke(this, false);
    }

    /// <summary>"关联到…" on the network: the next click on another summary links it to the selected one (D-018: the hint also offers the keyboard picker).</summary>
    [RelayCommand]
    private void BeginLink()
    {
        if (Selected is null || Links is null)
        {
            return;
        }

        Links.ClosePicker();
        Links.Notice = null;
        PickRefusal = null;
        IsPicking = true;
    }

    [RelayCommand(CanExecute = nameof(IsPicking))]
    private void CancelPick()
    {
        IsPicking = false;
        PickRefusal = null;
        Links?.ReturnFocus();
    }

    /// <summary>从列表选择…: leaves pick mode for the searchable list in the details.</summary>
    [RelayCommand]
    private Task PickFromListAsync()
    {
        IsPicking = false;
        PickRefusal = null;
        return Links?.OpenPickerCommand.ExecuteAsync(null) ?? Task.CompletedTask;
    }

    /// <summary>A click on a summary in pick mode: links it, or says why not (the same summary, an already linked one).</summary>
    public async Task PickNodeAsync(SummaryNodeId node)
    {
        if (!IsPicking || _linking || Selected is not { } source || Links is null)
        {
            return;
        }

        var name = Nodes.FirstOrDefault(n => n.Id == node)?.DisplayName ?? string.Empty;
        if (node == source.Id)
        {
            PickRefusal = UiText.LinkPickRefusedSelf;
            return;
        }

        if (_workspaceLinks.Any(l => l.Touches(node) && l.Touches(source.Id)))
        {
            PickRefusal = UiText.LinkPickRefusedLinked(name);
            return;
        }

        // One link at a time: a second click while this one is written would only be refused as a duplicate.
        _linking = true;
        try
        {
            if (await Links.LinkToAsync(node, name))
            {
                IsPicking = false;
                PickRefusal = null;
            }
            else
            {
                PickRefusal = Links.Notice;
            }
        }
        finally
        {
            _linking = false;
        }
    }

    /// <summary>A click on a recommended node in pick mode: it is not a summary yet, so it cannot be linked.</summary>
    public void RefusePickOfRecommendation() => PickRefusal = UiText.LinkPickRefusedRecommended;

    /// <summary>显示关联 / 隐藏关联: display only, remembered for the workspace.</summary>
    [RelayCommand]
    private async Task ToggleLinksAsync()
    {
        if (_links is null)
        {
            return;
        }

        _linksHidden = !_linksHidden;
        BuildLinkViews();
        BuildSuggestionViews();
        RaiseLinkState();
        ViewChanged?.Invoke(this, false);
        try
        {
            await _links.SetHiddenAsync(Workspace.Id, _linksHidden, CancellationToken.None);
        }
#pragma warning disable CA1031 // Applied for this run already; not remembered this time.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
        }
    }

    /// <summary>Jump from a link in the details: the other summary is selected and brought to the centre.</summary>
    private void JumpTo(SummaryNodeId node)
    {
        if (Nodes.FirstOrDefault(n => n.Id == node) is { } target)
        {
            Session.Viewport = new Viewport(target.X, target.Y, Session.Viewport?.Zoom ?? 1);
            Select(target.Id);
        }
    }

    private void ShowLinksOfSelected()
    {
        if (Links is null)
        {
            return;
        }

        if (Selected is null)
        {
            IsPicking = false;
            PickRefusal = null;
        }

        LinksReloaded = Links.ShowAsync(Selected?.Id, Selected?.DisplayName ?? string.Empty);
    }

    private void OnLinksPanelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SummaryLinksPanel.IsPickerOpen))
        {
            OnPropertyChanged(nameof(ShowsDetail));
            OnPropertyChanged(nameof(ShowsLinkPicker));
        }
    }

    private void OnLinksChanged(object? sender, WorkspaceId workspace)
    {
        if (workspace != Workspace.Id || _disposed)
        {
            return;
        }

        _dispatcher.Post(() => LinksReloaded = _linkLoad.Request());
    }

    /// <summary>Reads the workspace's links and whether they are hidden; the views are rebuilt by the caller.</summary>
    private async Task ReadLinksAsync()
    {
        if (_links is null)
        {
            return;
        }

        try
        {
            _workspaceLinks = await _links.ListAsync(Workspace.Id, CancellationToken.None);
            _linksHidden = await _links.AreHiddenAsync(Workspace.Id, CancellationToken.None);
        }
#pragma warning disable CA1031 // Unreadable links are none: the network works as before.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            _workspaceLinks = [];
        }
    }

    private async Task ReloadLinksAsync()
    {
        await ReadLinksAsync();
        if (_disposed)
        {
            return;
        }

        BuildLinkViews();
        BuildSuggestionViews();
        RefreshListLinks();
        RaiseLinkState();
        if (Links is not null)
        {
            await Links.ReloadAsync();
        }

        ViewChanged?.Invoke(this, false);
    }

    private void RaiseLinkState()
    {
        OnPropertyChanged(nameof(ShowsLinkTools));
        OnPropertyChanged(nameof(LinksHidden));
        OnPropertyChanged(nameof(ToggleLinksLabel));
        OnPropertyChanged(nameof(ShowsLinkLegend));
    }

    /// <summary>The link edges between summaries at their positions in memory, so they follow 整理布局 and drags.</summary>
    private void BuildLinkViews()
    {
        var nodes = Nodes.ToDictionary(n => n.Id);
        LinkEdges = _linksHidden
            ? []
            : [.. _workspaceLinks.Where(l => nodes.ContainsKey(l.NodeA) && nodes.ContainsKey(l.NodeB)).Select(l => new LinkEdgeView(
                l.Id, l.NodeA, l.NodeB, nodes[l.NodeA].X, nodes[l.NodeA].Y, nodes[l.NodeB].X, nodes[l.NodeB].Y, l.Note))];
        OnPropertyChanged(nameof(LinkEdges));
        OnPropertyChanged(nameof(ShowsLinkLegend));
    }

    /// <summary>The list view's "关联：…" line of a summary; empty without links.</summary>
    private string LinksLineOf(SummaryNodeId node, IReadOnlyDictionary<SummaryNodeId, string> names)
    {
        var links = _workspaceLinks.Where(l => l.Touches(node)).Select(l => (names.GetValueOrDefault(l.Other(node)) ?? string.Empty, l.Note, l.Origin == SummaryLinkOrigin.Confirmed)).ToList();
        return links.Count == 0 ? string.Empty : UiText.LinksLine(links);
    }

    private void RefreshListLinks()
    {
        var names = Session.Projection.Nodes.ToDictionary(n => n.Id, n => n.DisplayName);
        for (var i = 0; i < Items.Count; i++)
        {
            var line = LinksLineOf(Items[i].Node, names);
            if (line != Items[i].Links)
            {
                Items[i] = Items[i] with { Links = line };
            }
        }
    }

    /// <summary>
    /// Single click on a recommended node (or its row in the list view): its question, reason and basis in the details,
    /// with 开始探索 and 忽略. No summary is selected meanwhile; nothing is read.
    /// </summary>
    [RelayCommand]
    public void SelectRecommendation(RecommendedNodeView? node)
    {
        Session.Selected = null;
        Selected = null;
        Trail = TrailOf(null);
        ShowLinksOfSelected();
        _selectedRecommendation = node?.Id;
        SelectedRecommendation = node;
        ViewChanged?.Invoke(this, false);
    }

    /// <summary>
    /// 开始探索: 新探索 opens with the question in the input, not sent. The root conversation is made only by the first send,
    /// which also uses this recommendation up; no branch source is recorded (a recommendation is not a selection). The
    /// conversation remembers the recommendation, so its first summary is linked to the basis (requirements §10.3.5).
    /// </summary>
    [RelayCommand]
    private void StartExploration()
    {
        if (SelectedRecommendation is { } node)
        {
            _navigator.Navigate(new NavigationRequest(MainSection.Explore, new NewConversationRoute(node.Id, node.Question, node.Basis)));
        }
    }

    /// <summary>忽略: removed and remembered; its slot stays empty until 换一批推荐 or a higher tier (§10.3.2).</summary>
    [RelayCommand]
    private async Task IgnoreRecommendationAsync()
    {
        if (SelectedRecommendation is not { } node || _recommendations is null)
        {
            return;
        }

        SelectRecommendation(null);
        _offer = _offer with { Items = [.. _offer.Items.Where(r => r.Id != node.Id)] };
        BuildRecommendationViews();
        ViewChanged?.Invoke(this, false);
        try
        {
            await _recommendations.IgnoreAsync(node.Recommendation, CancellationToken.None);
        }
#pragma warning disable CA1031 // Not remembered this time: the next load shows it again.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
        }

        await _recommendationLoad.Request();
    }

    /// <summary>换一批推荐: as many new nodes as the tier allows replace the shown ones; the old ones stay until they arrive.</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshRecommendations))]
    private async Task RefreshRecommendationsAsync()
    {
        if (_recommendations is null)
        {
            return;
        }

        IsRefreshingRecommendations = true;
        ClearRecommendationNote();
        RecommendationOutcome outcome;
        try
        {
            outcome = await _recommendations.RefreshNodesAsync(Workspace.Id, CancellationToken.None);
        }
#pragma warning disable CA1031 // A recommendation is a hint: any failure is said beside it and can be retried.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            outcome = RecommendationOutcome.Refused(RecommendationProblem.Failed);
        }

        if (_disposed)
        {
            IsRefreshingRecommendations = false;
            return;
        }

        await _recommendationLoad.Request();
        IsRefreshingRecommendations = false;
        if (outcome.Problem is { } problem and not RecommendationProblem.Cancelled and not RecommendationProblem.Disabled)
        {
            RecommendationNote = UiText.RecommendedNodesProblemText(problem, outcome.Error);
            RecommendationNoteCanRetry = problem is RecommendationProblem.Failed or RecommendationProblem.Empty or RecommendationProblem.StoragePaused;
            RecommendationNoteNeedsSettings = problem is RecommendationProblem.NotConfigured or RecommendationProblem.Credentials;
        }
    }

    private bool CanRefreshRecommendations() => !IsRefreshingRecommendations && !_offer.Hidden;

    /// <summary>隐藏推荐 / 显示推荐: display only, remembered for the workspace.</summary>
    [RelayCommand]
    private async Task ToggleRecommendationsAsync()
    {
        if (_recommendations is null)
        {
            return;
        }

        var hidden = !_offer.Hidden;
        _offer = _offer with { Hidden = hidden };
        if (hidden)
        {
            SelectRecommendation(null);
        }

        BuildRecommendationViews();
        RaiseRecommendationState();
        ViewChanged?.Invoke(this, false);
        try
        {
            await _recommendations.SetNodesHiddenAsync(Workspace.Id, hidden, CancellationToken.None);
        }
#pragma warning disable CA1031 // Applied for this run already; not remembered this time.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
        }
    }

    [RelayCommand]
    private void OpenSettings() => _navigator.Navigate(new NavigationRequest(MainSection.Settings));

    /// <summary>Double click or "open summary"; a session network dot without a summary opens its conversation.</summary>
    [RelayCommand]
    private void Open(SummaryNodeId node)
    {
        if (Session.Projection.Nodes.FirstOrDefault(n => n.Id == node) is { HasSummary: false } dot)
        {
            _navigator.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(dot.Conversation)));
            return;
        }

        _navigator.Navigate(new NavigationRequest(MainSection.SummaryNetwork, new SummaryRoute(node)));
    }

    [RelayCommand]
    private void OpenSelected()
    {
        if (Selected is { } detail)
        {
            Open(detail.Id);
        }
    }

    [RelayCommand]
    private void ReturnToConversation()
    {
        if (Selected is { } detail)
        {
            _navigator.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(detail.Conversation)));
        }
    }

    /// <summary>Title search: selects the first match and brings it to the centre.</summary>
    [RelayCommand]
    private void Find()
    {
        var text = SearchText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var match = Nodes.FirstOrDefault(n => IsMatch(n, text));
        if (match is null)
        {
            return;
        }

        Session.Viewport = new Viewport(match.X, match.Y, Session.Viewport?.Zoom ?? 1);
        Select(match.Id);
    }

    /// <summary>Only the viewport changes; no node moves.</summary>
    [RelayCommand]
    private void ShowAll()
    {
        Session.Viewport = null;
        Session.FitAllRequested = true;
        ViewChanged?.Invoke(this, true);
    }

    [RelayCommand]
    private void ZoomIn() => ZoomRequested?.Invoke(this, 1.25);

    [RelayCommand]
    private void ZoomOut() => ZoomRequested?.Invoke(this, 0.8);

    /// <summary>
    /// 空间 | 时间线: the session recomputes every position, the page rebuilds what it draws and fits it into view. The view
    /// already shown is left alone, so clicking its segment again keeps the reader's pan and zoom.
    /// </summary>
    [RelayCommand]
    private void SetViewMode(SummaryNetworkViewMode mode)
    {
        if (!IsSessionNetwork || mode == Session.ViewMode)
        {
            return;
        }

        Session.SetViewMode(mode);
        BuildViews();
        OnPropertyChanged(nameof(IsTimeline));
        ViewChanged?.Invoke(this, true);
    }

    /// <summary>Explicit re-layout: kept in memory and marked unsaved until saved.</summary>
    [RelayCommand]
    private void Arrange()
    {
        Session.Arrange();
        BuildViews();
        ViewChanged?.Invoke(this, true);
    }

    /// <summary>
    /// A drag ended: the node keeps its new place in memory and the layout is unsaved until saved, as after
    /// <see cref="ArrangeCommand"/>; unlike arranging, the viewport stays where the reader put it.
    /// </summary>
    public void MoveNode(SummaryNodeId node, double x, double y)
    {
        if (!Session.Move(node, new Application.Storage.NodePosition(x, y)))
        {
            return;
        }

        BuildViews();
        ViewChanged?.Invoke(this, false);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task<bool> SaveLayoutAsync() => Session.SaveAsync(CancellationToken.None);

    private bool CanSave() => Session.IsDirty && !Session.IsSaving;

    [RelayCommand]
    private void ToggleView() => IsListView = !IsListView;

    partial void OnIsListViewChanged(bool value)
    {
        // The list has no nodes to click: "关联到…" waiting for one ends (the details still offer it, and its keyboard list).
        if (value)
        {
            IsPicking = false;
            PickRefusal = null;
        }

        OnPropertyChanged(nameof(ShowsRecommendationLegend));
        OnPropertyChanged(nameof(ShowsLinkLegend));
        OnPropertyChanged(nameof(ShowsSuggestionLegend));
        OnPropertyChanged(nameof(ShowsSuggestionsInDetails));
    }

    partial void OnSearchTextChanged(string value)
    {
        Matches = FindMatches();
        ViewChanged?.Invoke(this, false);
    }

    private static bool IsMatch(NodeView node, string text) =>
        node.ShortTitle.Contains(text, StringComparison.CurrentCultureIgnoreCase) || node.DisplayName.Contains(text, StringComparison.CurrentCultureIgnoreCase);

    private HashSet<SummaryNodeId> FindMatches()
    {
        var text = SearchText.Trim();
        return text.Length == 0 ? [] : [.. Nodes.Where(n => IsMatch(n, text)).Select(n => n.Id)];
    }

    /// <summary>The selected node and its sources, following each node's parent edge to the top.</summary>
    private HashSet<SummaryNodeId> TrailOf(SummaryNodeId? node)
    {
        var trail = new HashSet<SummaryNodeId>();
        var parents = Nodes.ToDictionary(n => n.Id, n => n.Parent);
        for (var at = node; at is { } id && parents.ContainsKey(id) && trail.Add(id); at = parents[id])
        {
        }

        return trail;
    }

    public void Dispose()
    {
        _disposed = true;
        _summaries.Changed -= OnChanged;
        _conversations.VisibilityChanged -= OnVisibilityChanged;
        _conversations.ConversationChanged -= OnChanged;
        Session.PropertyChanged -= OnSessionChanged;
        Session.Discarded -= OnDiscarded;
        if (_recommendations is not null)
        {
            _recommendations.Changed -= OnRecommendationsChanged;
        }

        if (_recommendationSettings is not null)
        {
            _recommendationSettings.Changed -= OnRecommendationSettingsChanged;
        }

        if (_links is not null)
        {
            _links.Changed -= OnLinksChanged;
        }

        if (Links is not null)
        {
            Links.PropertyChanged -= OnLinksPanelChanged;
        }

        DetachSuggestions();
    }

    /// <summary>网络推荐节点 or 自动准备推荐 changed in 设置 › 提示 while this page stays open: shown or hidden now, prepared if due.</summary>
    private void OnRecommendationSettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            RecommendationsReloaded = _recommendationLoad.Request();
            _recommendations?.SummaryNetworkOpened(Workspace.Id);
        });
    }

    private void OnRecommendationsChanged(object? sender, WorkspaceId workspace)
    {
        if (workspace != Workspace.Id || _disposed)
        {
            return;
        }

        _dispatcher.Post(() => RecommendationsReloaded = _recommendationLoad.Request());
    }

    /// <summary>Reads what the network shows of its recommended nodes; the views are rebuilt by the caller.</summary>
    private async Task ReadOfferAsync()
    {
        if (_recommendations is null)
        {
            return;
        }

        try
        {
            _offer = await _recommendations.GetNodesAsync(Workspace.Id, CancellationToken.None);
        }
#pragma warning disable CA1031 // Unreadable recommendations are none: the network works as before.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            _offer = NodeOffer.Off;
        }
    }

    private async Task ReloadRecommendationsAsync()
    {
        var before = _offer.Items.Select(r => r.Id).ToList();
        await ReadOfferAsync();
        if (_disposed)
        {
            return;
        }

        // New nodes answer whatever was said before.
        if (_offer.Items.Count > 0 && !IsRefreshingRecommendations && !_offer.Items.Select(r => r.Id).SequenceEqual(before))
        {
            ClearRecommendationNote();
        }

        BuildRecommendationViews();
        RaiseRecommendationState();
        ViewChanged?.Invoke(this, false);
    }

    private void RaiseRecommendationState()
    {
        // A background failure is mentioned quietly, unless the user's own request already said something.
        if (_offer.Failure is not null && !_offer.Hidden && ShowsRecommendationTools)
        {
            if (RecommendationNote is null && !IsRefreshingRecommendations)
            {
                RecommendationNote = UiText.RecommendedNodesQuietFailure;
                RecommendationNoteCanRetry = true;
                RecommendationNoteNeedsSettings = false;
            }
        }
        else if (RecommendationNote == UiText.RecommendedNodesQuietFailure || !ShowsRecommendationTools)
        {
            // Turned off (or nothing left to extend): nothing about recommendations is said any more.
            ClearRecommendationNote();
        }

        OnPropertyChanged(nameof(ShowsRecommendationTools));
        OnPropertyChanged(nameof(RecommendationsHidden));
        OnPropertyChanged(nameof(ToggleRecommendationsLabel));
        OnPropertyChanged(nameof(ToggleRecommendationsText));
        OnPropertyChanged(nameof(ShowsRecommendationLegend));
        OnPropertyChanged(nameof(ShowsRecommendationBusy));
        OnPropertyChanged(nameof(RecommendationBusyText));
        RefreshRecommendationsCommand.NotifyCanExecuteChanged();
    }

    private void ClearRecommendationNote()
    {
        RecommendationNote = null;
        RecommendationNoteCanRetry = false;
        RecommendationNoteNeedsSettings = false;
    }

    /// <summary>
    /// The recommended nodes beside their basis summaries at the positions in memory (so they follow 整理布局 and drags),
    /// clear of every saved node, caption and each other. Recomputed, never stored.
    /// </summary>
    private void BuildRecommendationViews()
    {
        var nodes = Nodes.ToDictionary(n => n.Id);
        var items = _offer.Hidden ? [] : _offer.Items.Where(r => r.Basis is { } b && nodes.ContainsKey(b)).ToList();
        var obstacles = new List<CanvasBox>(Nodes.Select(n => CanvasBox.OfNode(n.X, n.Y, n.LabelWidth)));
        foreach (var caption in Captions)
        {
            var members = caption.Members.Where(nodes.ContainsKey).Select(id => nodes[id]).ToList();
            if (members.Count > 0)
            {
                // The caption sits above the group's topmost node, from its leftmost one (SummaryNetworkCanvas.CaptionGap).
                var top = members.Min(n => n.Y);
                var left = members.Min(n => n.X) - SummaryNetworkLayout.NodeRadius;
                obstacles.Add(new CanvasBox(left, top - SummaryNetworkLayout.CaptionHeadroom, left + SummaryNetworkLayout.LabelWidth(caption.Text), top - (SummaryNetworkLayout.NodeHeight / 2)));
            }
        }

        var labels = items.Select(r => RecommendedLabel(r.Question)).ToList();
        var widths = labels.Select(RecommendedLabelWidth).ToList();
        var places = RecommendationPlacement.Place(obstacles, [.. items.Select((r, i) => (nodes[r.Basis!.Value].X, nodes[r.Basis!.Value].Y, widths[i]))]);
        List<RecommendedNodeView> built = [.. items.Select((r, i) =>
        {
            var basis = nodes[r.Basis!.Value];
            return new RecommendedNodeView(r, basis.Id, r.BasisName ?? basis.DisplayName, places[i].X, places[i].Y, basis.X, basis.Y, labels[i], widths[i]);
        })];

        // Unchanged (a reload for another reason): the same list stays, so the list view keeps its rows and their focus.
        if (!built.SequenceEqual(RecommendedNodes))
        {
            RecommendedNodes = built;
            OnPropertyChanged(nameof(RecommendedNodes));
            OnPropertyChanged(nameof(ShowsRecommendationLegend));
        }

        if (_selectedRecommendation is { } selected)
        {
            var again = RecommendedNodes.FirstOrDefault(n => n.Id == selected);
            if (again is null)
            {
                _selectedRecommendation = null;
            }

            SelectedRecommendation = again;
        }
    }

    /// <summary>The question as drawn beside the node: its first <see cref="UiText.RecommendedNodeLabelLength"/> characters.</summary>
    internal static string RecommendedLabel(string question) => ConversationRules.Shorten(question, UiText.RecommendedNodeLabelLength);

    /// <summary>The room the label needs: the "推荐" tag (with its frame) and the question after it.</summary>
    internal static double RecommendedLabelWidth(string label) => SummaryNetworkLayout.LabelWidth(UiText.RecommendedTag) + RecommendedTagExtra + SummaryNetworkLayout.LabelWidth(label);

    /// <summary>The tag's frame and the gap after it, in canvas units.</summary>
    internal const double RecommendedTagExtra = 14;

    private void OnDiscarded(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            _reload.Request();
        }
    }

    private void OnChanged(object? sender, ConversationId e)
    {
        if (!_disposed)
        {
            _reload.Request();
        }
    }

    /// <summary>A source was archived, restored or deleted: its node's "source archived" marker may have changed.</summary>
    private void OnVisibilityChanged(object? sender, IReadOnlyList<ConversationId> e)
    {
        if (!_disposed)
        {
            _reload.Request();
        }
    }

    private void OnSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(SaveLayoutLabel));
        OnPropertyChanged(nameof(IsSaving));
        OnPropertyChanged(nameof(SaveFailed));
        SaveLayoutCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadAsync()
    {
        await Session.LoadAsync(CancellationToken.None);
        var conversations = await _conversations.ListAsync(Workspace.Id, CancellationToken.None);
        if (_disposed)
        {
            return;
        }

        _titles = conversations.ToDictionary(c => c.Conversation.Id, c => UiText.ConversationTitle(c.Conversation, c.FirstQuestion, c.Quote));
        await ReadLinksAsync();
        await ReadOfferAsync();
        if (_disposed)
        {
            return;
        }

        Suggestions?.SetSummaryCount(Session.Projection.Nodes.Count);
        await (Suggestions?.ReloadAsync() ?? Task.CompletedTask);
        if (_disposed)
        {
            return;
        }

        BuildViews();
        RaiseRecommendationState();
        RaiseLinkState();
        Items.Clear();
        var names = Session.Projection.Nodes.ToDictionary(n => n.Id, n => n.DisplayName);
        foreach (var node in Session.Projection.Nodes)
        {
            // A session network dot without a summary has no markers: it is a conversation, never read or unread.
            if (!node.HasSummary)
            {
                Items.Add(new SummaryListItem(node.Id, node.DisplayName, string.Empty, false) { HasSummary = false, Links = LinksLineOf(node.Id, names) });
                continue;
            }

            var markers = string.Join(" · ", new[] { UiText.Markers(node.Markers), node.SourceArchived ? UiText.SourceArchived : string.Empty }.Where(m => m.Length > 0));
            var other = string.Join(" · ", new[] { node.Markers.HasLaterContent ? UiText.MarkerLater : string.Empty, node.SourceArchived ? UiText.SourceArchived : string.Empty }.Where(m => m.Length > 0));
            Items.Add(new SummaryListItem(node.Id, node.DisplayName, markers, markers.Length > 0, node.Markers.Unread, node.Markers.HasCandidate, other) { Links = LinksLineOf(node.Id, names) });
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsTimeline));
        Selected = Session.Selected is { } selected && Session.Projection.Nodes.Any(n => n.Id == selected) ? Detail(selected) : null;
        Trail = TrailOf(Selected?.Id);
        ShowLinksOfSelected();
        Matches = FindMatches();
        ViewChanged?.Invoke(this, Session.Viewport is null);

        // Opened, or its summaries changed: the recommendations look whether §10.3.2 asks for new ones. A page that
        // replaces one after a language switch was not opened; its first load tells nothing.
        if (_replayed)
        {
            _replayed = false;
            return;
        }

        _recommendations?.SummaryNetworkOpened(Workspace.Id);
    }

    private void BuildViews()
    {
        var positions = Session.Positions;

        // A node hangs from its branch parent; the time thread between explorations is not a source.
        var parents = Session.Projection.Edges.Where(e => e.Kind == SummaryNetworkEdgeKind.Branch).ToDictionary(e => e.To, e => e.From);
        var timeline = Session.IsSessionNetwork && Session.ViewMode == SummaryNetworkViewMode.Timeline;

        Nodes = [.. Session.Projection.Nodes.Where(n => positions.ContainsKey(n.Id)).Select(n => new NodeView(
            n.Id, n.ShortTitle, n.DisplayName, positions[n.Id].X, positions[n.Id].Y, n.Markers.Unread, n.Markers.HasCandidate, SummaryNetworkLayout.LabelWidth(SummaryNetworkLayout.Label(n.ShortTitle, n.Markers.HasCandidate)),
            parents.TryGetValue(n.Id, out var parent) && positions.ContainsKey(parent) ? parent : null, n.Conversation == n.Root, n.HasSummary, n.Weight, n.CreatedAt))];
        Edges = [.. Session.Projection.Edges.Where(e => positions.ContainsKey(e.From) && positions.ContainsKey(e.To)).Select(e => new EdgeView(
            e.From, e.To, positions[e.From].X, positions[e.From].Y, positions[e.To].X, positions[e.To].Y, e.IsDirect, e.IsDirect ? null : UiText.ViaCount(e.Via.Count),
            e.Kind, timeline && e.Kind == SummaryNetworkEdgeKind.Branch && positions[e.From].Y != positions[e.To].Y))];
        // The session network dates its days on the timeline only; the spatial view shows time on hover and in the details.
        Captions = Session.IsSessionNetwork ? (timeline ? DayCaptions() : []) : [.. Session.Projection.Nodes.Where(n => positions.ContainsKey(n.Id)).GroupBy(n => n.Root).Select(g =>
        {
            // Over the root's own summary; when the root conversation has none, over the group's top-left node.
            var anchor = g.FirstOrDefault(n => n.Conversation == g.Key) ?? g.OrderBy(n => positions[n.Id].Y).ThenBy(n => positions[n.Id].X).First();
            return new GroupCaption(UiText.ExplorationCaption(_titles.GetValueOrDefault(g.Key)), anchor.Id, [.. g.Select(n => n.Id)]);
        })];
        Trail = TrailOf(Session.Selected);
        BuildLinkViews();
        BuildSuggestionViews();
        BuildRecommendationViews();
    }

    /// <summary>The session network's captions: the local day over the first exploration of each day ("11月20日").</summary>
    private List<GroupCaption> DayCaptions()
    {
        var today = SummaryNetworkSession.LocalDay(DateTimeOffset.Now);
        var nodes = Session.Projection.Nodes.ToDictionary(n => n.Id);
        return [.. Session.DayStarts.Where(id => Session.Positions.ContainsKey(id) && nodes.ContainsKey(id)).Select(id =>
            new GroupCaption(UiText.DayLabel(SummaryNetworkSession.LocalDay(nodes[id].CreatedAt), today), id, [id]))];
    }

    private NodeDetail? Detail(SummaryNodeId id)
    {
        if (Session.Projection.Nodes.FirstOrDefault(n => n.Id == id) is not { } node)
        {
            return null;
        }

        var edge = Session.Projection.Edges.FirstOrDefault(e => e.To == id && !e.IsDirect);
        var steps = PathOf(node);
        var gated = IsSessionNetwork;

        // A session dot is headed by its title, never its question; a summary keeps its own display name.
        var heading = gated && !node.HasSummary ? _titles.GetValueOrDefault(node.Conversation) ?? node.ShortTitle : node.DisplayName;
        return new NodeDetail(
            node.Id,
            node.Conversation,
            heading,
            node.ShortTitle,
            node.HasSummary ? UiText.Markers(node.Markers) : string.Empty,
            UiText.NodeSource(_titles.GetValueOrDefault(node.Conversation) ?? UiText.NewConversation, node.SourceArchived),
            gated ? UiText.SessionStarted(node.CreatedAt) : UiText.NodeCreated(node.CreatedAt),
            edge is null ? null : UiText.ViaUnsummarized([.. edge.Via.Select(c => _titles.GetValueOrDefault(c) ?? UiText.NewConversation)]),
            node.Markers.Unread,
            node.Markers.HasCandidate,
            node.SourceArchived,
            UiText.SourcePath([.. steps.Select(step => step.Text)]),
            steps,
            node.HasSummary,
            gated ? node.FirstQuestion : null);
    }

    /// <summary>
    /// Every conversation from the exploration's root to this node, root first: summarized ones by short name, the
    /// unsummarized ones by title and marked, also when no summarized ancestor exists at all.
    /// </summary>
    private List<PathStep> PathOf(SummaryNetworkNode node)
    {
        // In the session network every conversation is a dot; only those with a summary count as summarized here.
        var summarized = Session.Projection.Nodes.Where(n => n.HasSummary).ToDictionary(n => n.Conversation, n => n.ShortTitle);
        var chain = node.Ancestors.Append(node.Conversation).ToList();
        return [.. chain.Select((conversation, i) => summarized.TryGetValue(conversation, out var shortTitle)
            ? new PathStep(shortTitle, true, i == 0)
            : new PathStep(_titles.GetValueOrDefault(conversation) ?? UiText.NewConversation, false, i == 0))];
    }
}
