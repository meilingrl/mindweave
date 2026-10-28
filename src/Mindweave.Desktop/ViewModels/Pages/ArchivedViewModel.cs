using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels.Conversations;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>One hidden conversation on the archived page; <see cref="IsArchived"/> when the user archived it itself.</summary>
public sealed class ArchivedItemViewModel(ConversationId id, string title, int depth, bool isArchived)
{
    public ConversationId Id { get; } = id;

    public string Title { get; } = title;

    public bool IsArchived { get; } = isArchived;

    public string? Marker => IsArchived ? UiText.ArchivedMarker : null;

    public Avalonia.Thickness Indent => new(Math.Min(depth, ConversationItemViewModel.MaxIndentedDepth) * 14, 0, 0, 0);
}

/// <summary>
/// The workspace's hidden conversations (S6, D-021): each archived conversation with the branches it hides, as a
/// tree. A conversation opens read-only; restore brings back its path (asked first when archived ancestors come back
/// too); delete is offered for conversations nothing depends on and says why when refused.
/// </summary>
public sealed partial class ArchivedViewModel : PageViewModel, IDisposable
{
    private readonly WorkspaceId _workspace;
    private readonly ConversationService _conversations;
    private readonly IWorkbenchNavigator _navigator;
    private readonly SerialRefresh _load;
    private readonly UiThrottle _reload;
    private Dictionary<ConversationId, ConversationSummary> _rows = [];
    private bool _disposed;

    public ArchivedViewModel(Workspace workspace, ConversationService conversations, IWorkbenchNavigator navigator, IUiDispatcher dispatcher)
        : base(ArchivedRoute.Instance)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace.Id;
        _conversations = conversations;
        _navigator = navigator;
        _load = new SerialRefresh(LoadAsync);
        _reload = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(30), () => _ = _load.Request());
        _conversations.ConversationChanged += OnConversationChanged;
        Loaded = _load.Request();
    }

    public override string Title => UiText.ArchivedTitle;

    public ObservableCollection<ArchivedItemViewModel> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    /// <summary>The first load; tests await it.</summary>
    internal Task Loaded { get; }

    [ObservableProperty]
    public partial PendingConfirmation? Confirmation { get; private set; }

    /// <summary>Why the last restore or delete did not happen.</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    /// <summary>Reloads now; tests await it.</summary>
    internal Task RefreshAsync() => _load.Request();

    [RelayCommand]
    private void Open(ArchivedItemViewModel item) =>
        _navigator.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(item.Id)));

    [RelayCommand]
    private async Task RestoreAsync(ArchivedItemViewModel item)
    {
        Notice = null;
        var plan = await _conversations.PlanRestoreAsync(item.Id, CancellationToken.None);
        async Task Restore()
        {
            await _conversations.RestoreAsync(item.Id, CancellationToken.None);
            await _load.Request();
        }

        if (plan.ArchivedAncestors.Count > 0)
        {
            Confirmation = new PendingConfirmation(UiText.RestoreConfirm([.. plan.ArchivedAncestors.Select(TitleOf)], plan.OthersReappearing), Restore);
        }
        else
        {
            await Restore();
        }
    }

    [RelayCommand]
    private void Delete(ArchivedItemViewModel item)
    {
        Notice = null;
        Confirmation = new PendingConfirmation(UiText.DeleteConfirm, async () =>
        {
            var result = await _conversations.DeleteAsync(item.Id, CancellationToken.None);
            if (result.Deleted || result.Missing)
            {
                ConversationRemoval.Forget(_navigator, item.Id, result.Parent);
            }
            else
            {
                Notice = UiText.DeleteRefused(result.Blockers);
            }

            await _load.Request();
        });
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

    public void Dispose()
    {
        _disposed = true;
        _conversations.ConversationChanged -= OnConversationChanged;
    }

    private void OnConversationChanged(object? sender, ConversationId e)
    {
        if (!_disposed)
        {
            _reload.Request();
        }
    }

    private string TitleOf(ConversationId id) =>
        _rows.TryGetValue(id, out var row) ? UiText.ConversationTitle(row.Conversation, row.FirstQuestion, row.Quote) : UiText.NewConversation;

    private async Task LoadAsync()
    {
        var all = await _conversations.ListAsync(_workspace, CancellationToken.None);
        if (_disposed)
        {
            return;
        }

        _rows = all.ToDictionary(r => r.Conversation.Id);

        // The roots of this tree are the archived conversations whose parent is visible; a branch archived separately
        // below an archived one is shown in place, marked.
        Items.Clear();
        foreach (var (row, depth) in ConversationListViewModel.AsTree([.. all.Where(r => r.IsHidden)]))
        {
            Items.Add(new ArchivedItemViewModel(row.Conversation.Id, TitleOf(row.Conversation.Id), depth, row.IsArchived));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }
}
