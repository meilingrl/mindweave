using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Storage;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// 设置 › 工作区 (2026-11-10): every workspace in one place, with what it holds, the open one tagged 当前, and per row
/// 切换到此工作区, 改名 (inline) and 删除工作区… (the existing confirmation dialog, for any workspace). A new workspace can
/// be made here too; it is listed without switching to it, so this page stays in view.
/// </summary>
public sealed partial class WorkspacesViewModel : PageViewModel, IDisposable
{
    private readonly IWorkspaceDirectory _directory;
    private readonly IWorkspaces _workspaces;
    private readonly IWorkspaceDeletion _deletion;
    private readonly IWorkspaceDeletionPrompt? _deletionPrompt;
    private readonly IWorkspaceSwitcher? _switcher;
    private bool _disposed;

    public WorkspacesViewModel(
        IWorkspaceDirectory directory,
        IWorkspaces workspaces,
        IWorkspaceDeletion deletion,
        IWorkspaceDeletionPrompt? deletionPrompt = null,
        IWorkspaceSwitcher? switcher = null)
        : base(WorkspacesRoute.Instance)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;
        _workspaces = workspaces;
        _deletion = deletion;
        _deletionPrompt = deletionPrompt;
        _switcher = switcher;
        NewWorkspace = new WorkspaceNameForm(CreateAsync);
        Sync();
        directory.Workspaces.CollectionChanged += OnWorkspacesChanged;
        if (directory is INotifyPropertyChanged observable)
        {
            observable.PropertyChanged += OnDirectoryChanged;
        }

        Loaded = RefreshCountsAsync();
    }

    public override string Title => UiText.WorkspacesTitle;

    /// <summary>One row per workspace, in the switcher's order (oldest first).</summary>
    public ObservableCollection<WorkspaceRowViewModel> Rows { get; } = [];

    public WorkspaceNameForm NewWorkspace { get; }

    /// <summary>The new-workspace form is open at the top of the list.</summary>
    [ObservableProperty]
    public partial bool IsCreating { get; private set; }

    /// <summary>The last count of every row; tests await it.</summary>
    internal Task Loaded { get; private set; }

    /// <summary>The page left the stacks (tests).</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>The last rename; tests await it.</summary>
    internal Task Renaming { get; private set; } = Task.CompletedTask;

    /// <summary>A row's name turned into a box (the view moves the focus into it), or is about to turn back (the view returns the focus to the row).</summary>
    public event EventHandler<WorkspaceRowViewModel>? RenameStarted;

    public event EventHandler<WorkspaceRowViewModel>? RenameEnded;

    /// <summary>The form opened (the view moves the focus into its box) or closed.</summary>
    public event EventHandler? CreateStarted;

    /// <summary>Counts again (the page is shown again: conversations may have come and gone meanwhile). Old counts stay until the new ones are in.</summary>
    public void Refresh()
    {
        if (!_disposed)
        {
            Loaded = RefreshCountsAsync();
        }
    }

    [RelayCommand]
    private void BeginCreate()
    {
        IsCreating = true;
        CreateStarted?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CancelCreate()
    {
        IsCreating = false;
        NewWorkspace.Reset();
    }

    internal void SwitchTo(WorkspaceRowViewModel row) => _switcher?.SwitchTo(row.Workspace);

    internal void Delete(WorkspaceRowViewModel row) => _deletionPrompt?.Open(row.Workspace);

    internal void BeginRename(WorkspaceRowViewModel row)
    {
        // One row at a time: starting another gives up the first one's unsaved text.
        foreach (var other in Rows.Where(r => r.IsRenaming && !ReferenceEquals(r, row)))
        {
            EndRename(other);
        }

        row.RenameText = row.Workspace.Name.Value;
        row.RenameError = null;
        row.IsRenaming = true;
        RenameStarted?.Invoke(this, row);
    }

    internal void CancelRename(WorkspaceRowViewModel row)
    {
        if (row.IsRenaming && !row.IsSaving)
        {
            EndRename(row);
        }
    }

    internal Task CommitRenameAsync(WorkspaceRowViewModel row)
    {
        Renaming = CommitCoreAsync(row);
        return Renaming;
    }

    private async Task CommitCoreAsync(WorkspaceRowViewModel row)
    {
        if (!row.IsRenaming || row.IsSaving)
        {
            return;
        }

        // The rules and messages of creating a workspace; a name already in use is accepted, as creating accepts it.
        if (!WorkspaceName.TryCreate(row.RenameText, out var name, out var error))
        {
            row.RenameError = UiText.WorkspaceNameError(error!.Value);
            return;
        }

        if (name!.Value == row.Workspace.Name.Value)
        {
            EndRename(row);
            return;
        }

        row.IsSaving = true;
        row.RenameError = null;
        Workspace? renamed;
        try
        {
            renamed = await _workspaces.RenameAsync(row.Workspace.Id, name, CancellationToken.None);
        }
        catch (StorageWriteException)
        {
            row.RenameError = UiText.WorkspaceRenameStorageFailed;
            return;
        }
#pragma warning disable CA1031 // Saving failed: keep the typed name and say why, as creating a workspace does.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            row.RenameError = UiText.SaveFailed(ex.Message);
            return;
        }
        finally
        {
            row.IsSaving = false;
        }

        if (renamed is null)
        {
            row.RenameError = UiText.WorkspaceRenameMissing;
            return;
        }

        _directory.RenameWorkspace(renamed);
        EndRename(row);
    }

    private void EndRename(WorkspaceRowViewModel row)
    {
        // Said while the box is still shown, so the view can tell whether it had the focus.
        RenameEnded?.Invoke(this, row);
        row.IsRenaming = false;
        row.RenameError = null;
    }

    private async Task CreateAsync(WorkspaceName name)
    {
        var workspace = await _workspaces.CreateAsync(name, CancellationToken.None);
        IsCreating = false;
        _directory.Workspaces.Add(workspace);
    }

    private void OnWorkspacesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var added = Sync();
        if (added.Count > 0)
        {
            Loaded = CountAsync(added, Loaded);
        }
    }

    private void OnDirectoryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IWorkspaceDirectory.CurrentWorkspace))
        {
            var current = _directory.CurrentWorkspace?.Id;
            foreach (var row in Rows)
            {
                row.IsCurrent = row.Workspace.Id == current;
            }
        }
    }

    /// <summary>Rows follow the list by identity: a renamed workspace keeps its row (and its counts), so nothing jumps.</summary>
    private List<WorkspaceRowViewModel> Sync()
    {
        var current = _directory.CurrentWorkspace?.Id;
        var byId = Rows.ToDictionary(r => r.Workspace.Id);
        var added = new List<WorkspaceRowViewModel>();
        for (var i = 0; i < _directory.Workspaces.Count; i++)
        {
            var workspace = _directory.Workspaces[i];
            if (!byId.Remove(workspace.Id, out var row))
            {
                row = new WorkspaceRowViewModel(workspace, this);
                Rows.Insert(Math.Min(i, Rows.Count), row);
                added.Add(row);
            }
            else
            {
                row.Workspace = workspace;
                var at = Rows.IndexOf(row);
                if (at != i && i < Rows.Count)
                {
                    Rows.Move(at, i);
                }
            }

            row.IsCurrent = workspace.Id == current;
        }

        foreach (var gone in byId.Values)
        {
            Rows.Remove(gone);
        }

        return added;
    }

    private Task RefreshCountsAsync() => CountAsync([.. Rows], Task.CompletedTask);

    private async Task CountAsync(IReadOnlyList<WorkspaceRowViewModel> rows, Task previous)
    {
        await previous;
        foreach (var row in rows)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var contents = await _deletion.InspectAsync(row.Workspace.Id, CancellationToken.None);
                if (contents.Exists)
                {
                    row.SetCounts(contents.Conversations, contents.Summaries);
                }
            }
#pragma warning disable CA1031 // A count is information only: the row says it could not be read and stays usable.
            catch (Exception)
#pragma warning restore CA1031
            {
                row.CountsFailed();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _directory.Workspaces.CollectionChanged -= OnWorkspacesChanged;
        if (_directory is INotifyPropertyChanged observable)
        {
            observable.PropertyChanged -= OnDirectoryChanged;
        }
    }
}

/// <summary>One workspace on 设置 › 工作区: its name (or the box renaming it), what it holds, and its actions.</summary>
public sealed partial class WorkspaceRowViewModel : ObservableObject
{
    private readonly WorkspacesViewModel _owner;

    internal WorkspaceRowViewModel(Workspace workspace, WorkspacesViewModel owner)
    {
        Workspace = workspace;
        _owner = owner;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(AutomationName), nameof(SwitchName), nameof(RenameName), nameof(DeleteName))]
    public partial Workspace Workspace { get; internal set; }

    public string Name => Workspace.Name.Value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    [NotifyCanExecuteChangedFor(nameof(SwitchCommand))]
    public partial bool IsCurrent { get; internal set; }

    /// <summary>"3 个会话 · 2 个总结", or that it is being counted, or could not be.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string CountsText { get; private set; } = UiText.WorkspaceCountsLoading;

    [ObservableProperty]
    public partial bool IsRenaming { get; internal set; }

    [ObservableProperty]
    public partial string RenameText { get; set; } = string.Empty;

    /// <summary>Why the name was not saved (invalid, storage paused, the write failed); the box keeps what was typed.</summary>
    [ObservableProperty]
    public partial string? RenameError { get; internal set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitRenameCommand), nameof(CancelRenameCommand))]
    public partial bool IsSaving { get; internal set; }

    public string AutomationName => UiText.WorkspaceRowName(Name, IsCurrent, CountsText);

    public string SwitchName => UiText.SwitchToWorkspaceName(Name);

    public string RenameName => UiText.RenameWorkspaceName(Name);

    public string DeleteName => UiText.DeleteWorkspaceName(Name);

    internal void SetCounts(int conversations, int summaries) => CountsText = UiText.WorkspaceCounts(conversations, summaries);

    internal void CountsFailed()
    {
        // A count already shown stays; only a row never counted says it could not be read.
        if (CountsText == UiText.WorkspaceCountsLoading)
        {
            CountsText = UiText.WorkspaceCountsFailed;
        }
    }

    private bool CanSwitch() => !IsCurrent;

    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private void Switch() => _owner.SwitchTo(this);

    [RelayCommand]
    private void BeginRename() => _owner.BeginRename(this);

    private bool NotSaving() => !IsSaving;

    [RelayCommand(CanExecute = nameof(NotSaving))]
    private Task CommitRenameAsync() => _owner.CommitRenameAsync(this);

    [RelayCommand(CanExecute = nameof(NotSaving))]
    private void CancelRename() => _owner.CancelRename(this);

    [RelayCommand]
    private void Delete() => _owner.Delete(this);
}
