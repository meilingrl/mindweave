using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Network;
using Mindweave.Desktop.Network;

namespace Mindweave.Desktop.ViewModels;

/// <summary>A page adapter; the Main-owned session survives opening a summary and returning.</summary>
public sealed class NetworkViewModel : ContentViewModel, IDisposable
{
    private string search = "";
    private NetworkGraphSnapshot? displayedGraph;
    private bool disposed;
    public NetworkViewModel(NetworkSession session, Func<Guid, Task> openSummary)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(openSummary);
        Session = session;
        OpenSummaryCommand = new AsyncRelayCommand(() => SelectedNode is { } node ? openSummary(node.Id) : Task.CompletedTask,
            () => !Session.IsQuiesced && SelectedNode is not null);
        ArrangeCommand = new AsyncRelayCommand(Session.ArrangeAsync, () => !Session.IsQuiesced && !Session.IsArranging && Nodes.Count > 0);
        SaveLayoutCommand = new AsyncRelayCommand(async () => { await Session.SaveAsync(); }, () => !Session.IsQuiesced && !Session.IsSaving && Session.IsDirty);
        ShowAllCommand = new RelayCommand(Session.ShowAll, () => !Session.IsQuiesced);
        ZoomInCommand = new RelayCommand(() => Session.Zoom(1), () => !Session.IsQuiesced);
        ZoomOutCommand = new RelayCommand(() => Session.Zoom(-1), () => !Session.IsQuiesced);
        FindNextCommand = new RelayCommand(FindNext, () => !Session.IsQuiesced && MatchCount > 0);
        Session.PropertyChanged += SessionChanged;
        RefreshNodes();
    }
    public NetworkSession Session { get; }
    public ObservableCollection<NetworkNodeViewModel> Nodes { get; } = [];
    public ImmutableArray<NetworkEdge> Edges => Session.Graph.Edges;
    public NetworkNodeViewModel? SelectedNode
    {
        get => Nodes.FirstOrDefault(n => n.Id == Session.SelectedNodeId);
        set => Session.SelectedNodeId = value?.Id;
    }
    public string Search
    {
        get => search;
        set { if (SetProperty(ref search, value ?? "")) UpdateMatches(); }
    }
    public bool IsEmpty => Nodes.Count == 0;
    public int MatchCount => Nodes.Count(n => n.IsSearchMatch);
    public string? ErrorMessage => Session.ErrorMessage;
    public string StatusText => Session.IsLoading ? "正在读取网络…" : Session.IsSaving ? "正在保存布局…" : Session.IsArranging ? "正在整理布局…" :
        $"{Nodes.Count} 条总结" + (Session.IsDirty ? " · 布局待保存" : "");
    public string SearchStatusText => string.IsNullOrWhiteSpace(Search) ? "按显示名或基础标题查找" : $"{MatchCount} 个匹配";
    public IAsyncRelayCommand OpenSummaryCommand { get; }
    public IAsyncRelayCommand ArrangeCommand { get; }
    public IAsyncRelayCommand SaveLayoutCommand { get; }
    public IRelayCommand ShowAllCommand { get; }
    public IRelayCommand ZoomInCommand { get; }
    public IRelayCommand ZoomOutCommand { get; }
    public IRelayCommand FindNextCommand { get; }
    public Task ReloadAsync(bool fitAll = false) => Session.ReloadAsync(fitAll);
    public override Task WaitForPendingOperationsAsync() => Session.WaitForPendingOperationsAsync();

    private void SessionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(NetworkSession.Graph)) RefreshNodes();
        if (args.PropertyName == nameof(NetworkSession.Positions))
            foreach (var node in Nodes) node.Position = Session.Positions.GetValueOrDefault(node.Id);
        if (args.PropertyName == nameof(NetworkSession.SelectedNodeId)) UpdateSelection();
        OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(ErrorMessage));
        UpdateCommands();
    }
    private void RefreshNodes()
    {
        if (ReferenceEquals(displayedGraph, Session.Graph)) return;
        displayedGraph = Session.Graph;
        Nodes.Clear();
        foreach (var node in Session.Graph.Nodes) Nodes.Add(new(node, Session.Graph.Edges, Session.Positions.GetValueOrDefault(node.Metadata.Id)));
        UpdateMatches(); UpdateSelection();
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(Edges)); OnPropertyChanged(nameof(StatusText));
        UpdateCommands();
    }
    private void UpdateMatches()
    {
        var query = Search.Trim();
        foreach (var node in Nodes) node.IsSearchMatch = query.Length > 0 &&
            (node.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) || node.BaseTitle.Contains(query, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(MatchCount)); OnPropertyChanged(nameof(SearchStatusText)); FindNextCommand.NotifyCanExecuteChanged();
    }
    private void UpdateSelection()
    {
        foreach (var node in Nodes) node.IsSelected = node.Id == Session.SelectedNodeId;
        OnPropertyChanged(nameof(SelectedNode)); OpenSummaryCommand.NotifyCanExecuteChanged();
    }
    private void FindNext()
    {
        var matches = Nodes.Where(n => n.IsSearchMatch).ToArray();
        if (matches.Length == 0) return;
        var index = Array.FindIndex(matches, n => n.Id == Session.SelectedNodeId);
        Session.FocusNode(matches[(index + 1) % matches.Length].Id);
    }
    private void UpdateCommands()
    {
        OpenSummaryCommand.NotifyCanExecuteChanged(); ArrangeCommand.NotifyCanExecuteChanged(); SaveLayoutCommand.NotifyCanExecuteChanged();
        ShowAllCommand.NotifyCanExecuteChanged(); ZoomInCommand.NotifyCanExecuteChanged(); ZoomOutCommand.NotifyCanExecuteChanged(); FindNextCommand.NotifyCanExecuteChanged();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Session.PropertyChanged -= SessionChanged;
    }
}
