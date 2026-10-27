using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.ViewModels;

public sealed record SummaryListItem(SummaryNodeSnapshot Snapshot)
{
    public Guid Id => Snapshot.State.Node.Id;
    public string DisplayName => Snapshot.State.Node.DisplayName;
    public string BaseTitle => Snapshot.State.Node.BaseTitle;
    public bool IsUnread => !Snapshot.CurrentIsRead;
    public bool HasCandidate => Snapshot.State.Node.CandidateVersionId is not null;
    public bool HasLaterContent => Snapshot.HasLaterContent;
}

public sealed partial class SummaryListViewModel : ContentViewModel
{
    private readonly Guid workspace;
    private readonly ISummaryVersionStore store;
    private IReadOnlyList<SummaryListItem> all = [];
    private long reload;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private SummaryListItem? selectedItem;
    [ObservableProperty] private string? errorMessage;
    public bool IsEmpty => Items.Count == 0;
    public ObservableCollection<SummaryListItem> Items { get; } = [];
    public IAsyncRelayCommand<SummaryListItem> ViewSummaryCommand { get; }
    public SummaryListViewModel(Guid workspace, ISummaryVersionStore store, Func<Guid, Task> open)
    {
        this.workspace = workspace; this.store = store;
        ViewSummaryCommand = new AsyncRelayCommand<SummaryListItem>(item => item is null ? Task.CompletedTask : open(item.Id));
    }
    public async Task ReloadAsync()
    {
        var stamp = ++reload;
        try
        {
            var rows = await store.ListSummaryNodesAsync(workspace);
            if (stamp != reload) return;
            all = rows.Select(s => new SummaryListItem(s)).ToArray(); Filter(); ErrorMessage = null;
        }
        catch { if (stamp == reload) ErrorMessage = "总结列表读取失败，请重新打开网络。"; }
    }
    partial void OnSearchChanged(string value) => Filter();
    private void Filter()
    {
        var selected = SelectedItem?.Id; Items.Clear();
        foreach (var item in all.Where(i => i.DisplayName.Contains(Search, StringComparison.OrdinalIgnoreCase) || i.BaseTitle.Contains(Search, StringComparison.OrdinalIgnoreCase))) Items.Add(item);
        SelectedItem = Items.FirstOrDefault(i => i.Id == selected); OnPropertyChanged(nameof(IsEmpty));
    }
}
