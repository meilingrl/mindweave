using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels.Conversations;

namespace Mindweave.Desktop.ViewModels;

/// <summary>Asks for conversations to be deleted with their scope stated; pages and the sidebar hold this, not the dialog.</summary>
public interface IConversationDeletionPrompt
{
    void Open(WorkspaceId workspace, IReadOnlyList<ConversationId> selected);
}

/// <summary>Hands a delete request to the window's dialog, and tells the sidebar when the dialog deleted or archived.</summary>
public sealed class ConversationDeletionRequests : IConversationDeletionPrompt
{
    public event EventHandler<(WorkspaceId Workspace, IReadOnlyList<ConversationId> Selected)>? Requested;

    /// <summary>The dialog deleted or archived what it was opened for; the argument is what it was opened with.</summary>
    public event EventHandler<IReadOnlyList<ConversationId>>? Completed;

    public void Open(WorkspaceId workspace, IReadOnlyList<ConversationId> selected) => Requested?.Invoke(this, (workspace, selected));

    internal void Complete(IReadOnlyList<ConversationId> selected) => Completed?.Invoke(this, selected);
}

/// <summary>
/// The delete confirmation for a conversation that takes branches or summaries along, or for a sidebar selection
/// (requirements v0.12 §8.2): one sentence with the counts, "改为归档" as the alternative, "删除" in the danger colour and
/// "取消" as the default. Work running in the scope is named and keeps "删除" disabled; the counts and that notice follow
/// changes while the dialog is open, and a scope that changes while open is said, as one the transaction finds changed
/// is (refused and shown again); a press right after such a change is ignored. A failed backup is said and nothing is
/// deleted.
/// </summary>
public sealed partial class ConversationDeletionViewModel : ViewModelBase, IDisposable
{
    private readonly IConversationDeletion _deletion;
    private readonly IConversations _conversations;
    private readonly IWorkbenchNavigator _navigator;
    private readonly ConversationDeletionRequests _requests;
    private readonly UiThrottle _recheck;
    private readonly SerialRefresh _rechecks;
    private readonly TimeProvider _time;
    private WorkspaceId _workspace;
    private volatile bool _watching;

    /// <summary>When the counts last changed under an open dialog; a press this soon after is not taken as confirming them.</summary>
    private DateTimeOffset? _changedAt;

    /// <summary>The counts changed while open and no press has confirmed them since.</summary>
    private bool _unconfirmedChange;

    /// <summary>How long after the counts change under the pointer a press is ignored (it may have started before).</summary>
    internal static readonly TimeSpan ChangeHold = TimeSpan.FromMilliseconds(600);

    public ConversationDeletionViewModel(IConversationDeletion deletion, IConversations conversations, Workbench workbench, ConversationDeletionRequests requests, IUiDispatcher dispatcher, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(conversations);
        _deletion = deletion;
        _conversations = conversations;
        _navigator = workbench;
        _requests = requests;
        requests.Requested += OnRequested;

        // While open, work starting or ending and conversations changing (a new branch, a summary saved) re-count the scope.
        _rechecks = new SerialRefresh(RecheckAsync);
        _recheck = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(100), () => Loading = _rechecks.Request());
        conversations.ActivityChanged += OnActivityChanged;
        conversations.ConversationChanged += OnConversationChanged;
    }

    /// <summary>Stops listening to the services; the view it belongs to is being replaced.</summary>
    public void Dispose()
    {
        _requests.Requested -= OnRequested;
        _conversations.ActivityChanged -= OnActivityChanged;
        _conversations.ConversationChanged -= OnConversationChanged;
    }

    private void OnRequested(object? sender, (WorkspaceId Workspace, IReadOnlyList<ConversationId> Selected) request) => Open(request.Workspace, request.Selected);

    private void OnActivityChanged(object? sender, EventArgs e) => Watch();

    private void OnConversationChanged(object? sender, ConversationId e) => Watch();

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>The scope as last counted; what "删除" deletes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete), nameof(CanArchive), nameof(IsCascade))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(ArchiveInsteadCommand))]
    public partial ConversationDeleteScope? Scope { get; private set; }

    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    /// <summary>The one sentence with the counts; null until counted.</summary>
    [ObservableProperty]
    public partial string? Contents { get; private set; }

    /// <summary>Why nothing was (or would now be) deleted.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete), nameof(CanArchive), nameof(DeletingText))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(ArchiveInsteadCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    public bool IsCascade => Scope?.IsCascade == true;

    public string DeletingText => IsCascade ? UiText.WorkspaceDeleting : UiText.ConversationDeleting;

    public bool CanDelete => Scope is { IsEmpty: false, Busy.Count: 0 } && !IsBusy;

    /// <summary>"改为归档" acts on the roots the sidebar still shows; hidden ones are archived already.</summary>
    public bool CanArchive => Scope is { VisibleRoots.Count: > 0 } && !IsBusy;

    /// <summary>The last count; tests await it.</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>The last delete or archive; tests await it.</summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    private IReadOnlyList<ConversationId> _selected = [];

    public void Open(WorkspaceId workspace, IReadOnlyList<ConversationId> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (IsBusy)
        {
            return;
        }

        _workspace = workspace;
        _selected = [.. selected];
        Scope = null;
        Title = string.Empty;
        Contents = null;
        Problem = null;
        _changedAt = null;
        _unconfirmedChange = false;
        IsOpen = true;
        _watching = true;
        Loading = LoadAsync(initial: true);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => Close();

    private bool CanCancel() => !IsBusy;

    private void Close()
    {
        _watching = false;
        IsOpen = false;
        Scope = null;
    }

    private void Watch()
    {
        if (_watching)
        {
            _recheck.Request();
        }
    }

    private Task RecheckAsync() => IsOpen && !IsBusy ? LoadAsync(initial: false) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteAsync()
    {
        Running = RunDeleteAsync();
        return Running;
    }

    [RelayCommand(CanExecute = nameof(CanArchive))]
    private Task ArchiveInsteadAsync()
    {
        Running = RunArchiveAsync();
        return Running;
    }

    private async Task RunDeleteAsync()
    {
        if (Scope is not { } confirmed)
        {
            return;
        }

        if (_changedAt is { } changedAt && _time.GetUtcNow() - changedAt < ChangeHold)
        {
            // The counts changed just now, maybe between press and release: this press did not see them.
            return;
        }

        _unconfirmedChange = false;
        IsBusy = true;
        Problem = null;
        ConversationDeleteOutcome outcome;
        try
        {
            outcome = await _deletion.DeleteAsync(confirmed, CancellationToken.None);
        }
        catch (StorageWriteException)
        {
            Problem = UiText.ArchiveStorageFailed;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        switch (outcome.Status)
        {
            case ConversationDeleteStatus.Deleted:
            case ConversationDeleteStatus.Missing:
                Close();
                ConversationRemoval.Forget(_navigator, outcome.Scope);
                _requests.Complete(_selected);
                break;
            case ConversationDeleteStatus.Busy:
                await ShowAsync(outcome.Scope);
                break;
            case ConversationDeleteStatus.ScopeChanged:
                await ShowAsync(outcome.Scope);
                Problem = UiText.ConversationDeleteScopeChanged;
                break;
            case ConversationDeleteStatus.BackupFailed:
                Problem = UiText.WorkspaceDeleteBackupFailed;
                break;
        }
    }

    private async Task RunArchiveAsync()
    {
        if (Scope is not { } scope)
        {
            return;
        }

        IsBusy = true;
        Problem = null;
        (List<ConversationId> Busy, bool StorageFailed) result;
        try
        {
            result = await ConversationArchiving.ArchiveAllAsync(_conversations, scope.VisibleRoots);
        }
        finally
        {
            IsBusy = false;
        }

        if (result.StorageFailed)
        {
            Problem = UiText.ArchiveStorageFailed;
        }
        else if (result.Busy.Count > 0)
        {
            Problem = UiText.ArchiveRefused(await ConversationArchiving.TitlesAsync(_conversations, _workspace, result.Busy));
        }
        else
        {
            Close();
            _requests.Complete(_selected);
        }
    }

    /// <summary>
    /// Counts the scope and checks what runs: on <see cref="Open"/> (<paramref name="initial"/>) and again on every
    /// change while open. The running-work notice is set or cleared here; other notices stay until the next press.
    /// </summary>
    private async Task LoadAsync(bool initial)
    {
        var workspace = _workspace;
        ConversationDeleteScope scope;
        try
        {
            scope = await _deletion.InspectAsync(_selected, CancellationToken.None);
        }
#pragma warning disable CA1031 // An unreadable scope leaves the delete disabled and says so; cancelling still works.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            if (initial)
            {
                Problem = UiText.ConversationDeleteCountFailed;
            }

            return;
        }

        if (!IsOpen || IsBusy || workspace != _workspace)
        {
            return;
        }

        if (scope.IsEmpty)
        {
            // Gone meanwhile (deleted elsewhere): nothing left to confirm.
            Close();
            return;
        }

        var hadRunning = Scope is { Busy.Count: > 0 };
        _unconfirmedChange |= await ShowAsync(scope, live: !initial);
        if (scope.Busy.Count > 0)
        {
            return;
        }

        // A scope that grew or shrank while open is said, as a change found in the transaction is: no delete of a
        // scope the user has not seen (requirements v0.12 §8.2, architecture §9.1).
        if (_unconfirmedChange)
        {
            Problem = UiText.ConversationDeleteScopeChanged;
        }
        else if (hadRunning || Problem == UiText.ConversationDeleteCountFailed)
        {
            Problem = null;
        }
    }

    /// <summary>
    /// Shows <paramref name="scope"/>: the title, the counts, and who still runs. On a <paramref name="live"/> recount,
    /// returns whether it holds other conversations or summaries than the one shown, and marks when it changed.
    /// </summary>
    private async Task<bool> ShowAsync(ConversationDeleteScope scope, bool live = false)
    {
        var titles = await ConversationArchiving.TitlesAsync(_conversations, _workspace);
        if (!IsOpen)
        {
            return false;
        }

        var changed = live && Scope is { } shown && !scope.SameContents(shown);
        if (changed)
        {
            _changedAt = _time.GetUtcNow();
        }

        Scope = scope;
        // The title counts the rows picked (as the bar's "已选 N" does); the sentence counts every conversation that goes.
        var picked = scope.Selected.Where(scope.RootOf.ContainsKey).Distinct().ToList();
        Title = picked.Count == 1
            ? UiText.ConversationDeleteTitle(titles.GetValueOrDefault(picked[0], UiText.NewConversation))
            : UiText.ConversationDeleteTitle(picked.Count);
        Contents = UiText.ConversationDeleteContents(scope.Conversations.Count, scope.Branches, scope.Summaries.Count, scope.Links, scope.Attachments);
        if (scope.Busy.Count > 0)
        {
            Problem = UiText.DeleteWorkspaceBusy([.. scope.Busy.Select(b => (titles.GetValueOrDefault(b.Conversation, UiText.NewConversation), b.Answer, b.Summary))]);
        }

        return changed;
    }
}
