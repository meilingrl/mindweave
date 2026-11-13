using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Network;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels;

/// <summary>Asks for a workspace to be deleted; pages hold this, not the dialog, so the page factory has no cycle.</summary>
public interface IWorkspaceDeletionPrompt
{
    void Open(Workspace workspace);
}

/// <summary>Hands a delete request from a page to the dialog that is listening (the window's one).</summary>
public sealed class WorkspaceDeletionRequests : IWorkspaceDeletionPrompt
{
    public event EventHandler<Workspace>? Requested;

    public void Open(Workspace workspace) => Requested?.Invoke(this, workspace);
}

/// <summary>
/// The delete-a-workspace dialog (S7 round 4; the user chose whole deletion on 2026-11-02, departing from requirements
/// §5.1). It names what goes and asks for the workspace's name to be typed; "删除" stays disabled until it matches and
/// "取消" is the default. Running work, a paused storage or a failed backup are said in the dialog and nothing is
/// deleted. Afterwards the workbench moves to the most recently used workspace, or to the first-workspace flow.
/// </summary>
public sealed partial class WorkspaceDeletionViewModel : ViewModelBase
{
    private readonly IWorkspaceDeletion _deletion;
    private readonly IConversations _conversations;
    private readonly Workbench _workbench;
    private readonly NetworkSessions _networks;
    private readonly UiThrottle _recheck;
    private readonly SerialRefresh _rechecks;
    private volatile bool _watching;

    public WorkspaceDeletionViewModel(IWorkspaceDeletion deletion, IConversations conversations, Workbench workbench, NetworkSessions networks, WorkspaceDeletionRequests requests, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(conversations);
        _deletion = deletion;
        _conversations = conversations;
        _workbench = workbench;
        _networks = networks;
        requests.Requested += (_, workspace) => Open(workspace);

        // While the dialog is open, an answer or summary starting or finishing re-checks what runs (round 4 review):
        // the refusal goes as soon as the work is done, and comes up when work starts with the dialog open.
        _rechecks = new SerialRefresh(RecheckAsync);
        _recheck = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(100), () => Loading = _rechecks.Request());
        conversations.ActivityChanged += (_, _) =>
        {
            if (_watching)
            {
                _recheck.Request();
            }
        };
    }

    /// <summary>No workspace is left after a delete: the first-workspace flow takes over.</summary>
    public event EventHandler? NoWorkspaceLeft;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(TypeNamePrompt), nameof(CanDelete))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    public partial Workspace? Target { get; private set; }

    public string Title => Target is { } w ? UiText.DeleteWorkspaceTitle(w.Name.Value) : string.Empty;

    public string TypeNamePrompt => Target is { } w ? UiText.DeleteWorkspaceTypeName(w.Name.Value) : string.Empty;

    /// <summary>"N 个会话（含分支与已归档）、M 个总结将被永久删除……"; null until counted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    public partial string? Contents { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    public partial string TypedName { get; set; } = string.Empty;

    /// <summary>Why nothing was (or would now be) deleted: running work, paused storage, a failed backup or an unreadable count.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <summary>An answer or summary in the workspace is queued or running, as last checked; <see cref="Problem"/> names them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    public partial bool HasRunningWork { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(CancelCommand))]
    public partial bool IsDeleting { get; private set; }

    /// <summary>The typed name is the workspace's name exactly (as stored, normalized the same way), nothing in it runs and nothing is in progress.</summary>
    public bool CanDelete =>
        Target is { } w && Contents is not null && !IsDeleting && !HasRunningWork
        && string.Equals(TypedName.Normalize(NormalizationForm.FormC), w.Name.Value, StringComparison.Ordinal);

    /// <summary>The count and the running-work check of the last <see cref="Open"/> or re-check; tests await it.</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>The last delete; tests await it.</summary>
    internal Task Deleting { get; private set; } = Task.CompletedTask;

    public void Open(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (IsDeleting)
        {
            return;
        }

        Target = workspace;
        TypedName = string.Empty;
        Contents = null;
        Problem = null;
        HasRunningWork = false;
        IsOpen = true;
        _watching = true;
        Loading = LoadAsync(workspace, initial: true);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => Close();

    private void Close()
    {
        _watching = false;
        IsOpen = false;
        Target = null;
    }

    private Task RecheckAsync() =>
        IsOpen && !IsDeleting && Target is { } workspace ? LoadAsync(workspace, initial: false) : Task.CompletedTask;

    private bool CanCancel() => !IsDeleting;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteAsync()
    {
        Deleting = RunAsync();
        return Deleting;
    }

    private async Task RunAsync()
    {
        if (Target is not { } workspace)
        {
            return;
        }

        IsDeleting = true;
        Problem = null;
        WorkspaceDeleteOutcome outcome;
        try
        {
            outcome = await _deletion.DeleteAsync(workspace.Id, CancellationToken.None);
        }
        catch (StorageWriteException)
        {
            Problem = UiText.ArchiveStorageFailed;
            return;
        }
        finally
        {
            IsDeleting = false;
        }

        switch (outcome.Status)
        {
            case WorkspaceDeleteStatus.Busy:
                Problem = await BusyTextAsync(workspace, outcome.Busy);
                HasRunningWork = true;
                break;
            case WorkspaceDeleteStatus.BackupFailed:
                Problem = UiText.WorkspaceDeleteBackupFailed;
                break;
            default:
                Close();
                Leave(workspace.Id, outcome.Next);
                break;
        }
    }

    /// <summary>
    /// Counts what would go and checks what runs: on <see cref="Open"/> (<paramref name="initial"/>) and again whenever
    /// activity changes while the dialog is open. Only the running-work notice is replaced or cleared here; a backup
    /// or storage notice from the last press stays until the next one.
    /// </summary>
    private async Task LoadAsync(Workspace workspace, bool initial)
    {
        WorkspaceContents contents;
        try
        {
            contents = await _deletion.InspectAsync(workspace.Id, CancellationToken.None);
        }
#pragma warning disable CA1031 // An unreadable count leaves the delete disabled and says so; cancelling still works.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            if (initial && Target?.Id == workspace.Id)
            {
                Problem = UiText.WorkspaceDeleteCountFailed;
            }

            return;
        }

        if (Target?.Id != workspace.Id || IsDeleting)
        {
            return;
        }

        if (!contents.Exists)
        {
            Close();
            Leave(workspace.Id, next: null);
            return;
        }

        Contents = UiText.DeleteWorkspaceContents(contents.Conversations, contents.Summaries);
        if (contents.Busy.Count > 0)
        {
            var text = await BusyTextAsync(workspace, contents.Busy);
            if (Target?.Id == workspace.Id && !IsDeleting)
            {
                Problem = text;
                HasRunningWork = true;
            }
        }
        else if (HasRunningWork || Problem == UiText.WorkspaceDeleteCountFailed)
        {
            Problem = null;
            HasRunningWork = false;
        }
    }

    /// <summary>
    /// The pages of the deleted workspace leave every stack (disposed, so nothing they hold is saved into it), then its
    /// network session goes with any unsaved layout, unasked. Then the most recently used workspace, or onboarding.
    /// </summary>
    private void Leave(WorkspaceId deleted, WorkspaceId? next)
    {
        _workbench.RemoveWorkspace(deleted, next);
        _networks.Remove(deleted);
        if (_workbench.Workspaces.Count == 0)
        {
            NoWorkspaceLeft?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<string> BusyTextAsync(Workspace workspace, IReadOnlyList<BusyConversation> busy)
    {
        var rows = (await _conversations.ListAsync(workspace.Id, CancellationToken.None)).ToDictionary(r => r.Conversation.Id);
        return UiText.DeleteWorkspaceBusy([.. busy.Select(b => (
            rows.TryGetValue(b.Conversation, out var row) ? UiText.ConversationTitle(row.Conversation, row.FirstQuestion, row.Quote) : UiText.NewConversation,
            b.Answer,
            b.Summary))]);
    }
}
