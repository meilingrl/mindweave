using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Storage;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Conversations;
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
    private readonly IWorkspaceNameSuggestions? _suggestions;
    private readonly IMenuNavigator? _navigator;
    private readonly IWorkspaceNameCache? _names;
    private bool _disposed;

    public WorkspacesViewModel(
        IWorkspaceDirectory directory,
        IWorkspaces workspaces,
        IWorkspaceDeletion deletion,
        IWorkspaceDeletionPrompt? deletionPrompt = null,
        IWorkspaceSwitcher? switcher = null,
        IWorkspaceNameSuggestions? suggestions = null,
        IMenuNavigator? navigator = null,
        IWorkspaceNameCache? names = null)
        : base(WorkspacesRoute.Instance)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;
        _workspaces = workspaces;
        _deletion = deletion;
        _deletionPrompt = deletionPrompt;
        _switcher = switcher;
        _suggestions = suggestions;
        _navigator = navigator;
        _names = names;
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

    /// <summary>The last request for suggested names; tests await it.</summary>
    internal Task Suggesting { get; private set; } = Task.CompletedTask;

    /// <summary>The kept names of the last rename started, read; tests await it.</summary>
    internal Task KeptNamesRead { get; private set; } = Task.CompletedTask;

    private bool _shown;

    /// <summary>Suggested names can be asked for (the page was given the service).</summary>
    public bool CanSuggestNames => _suggestions is not null;

    /// <summary>A suggested name was put into the row's box (the view moves the focus back into it, the caret at the end).</summary>
    public event EventHandler<WorkspaceRowViewModel>? SuggestionPicked;

    /// <summary>A row's suggestions started, arrived, failed or were stopped by the request itself (the view may move the focus).</summary>
    public event EventHandler<WorkspaceRowViewModel>? SuggestionStateChanged;

    /// <summary>A row's name turned into a box (the view moves the focus into it), or is about to turn back (the view returns the focus to the row).</summary>
    public event EventHandler<WorkspaceRowViewModel>? RenameStarted;

    public event EventHandler<WorkspaceRowViewModel>? RenameEnded;

    /// <summary>The form opened (the view moves the focus into its box) or closed.</summary>
    public event EventHandler? CreateStarted;

    /// <summary>Counts again (the page is shown again: conversations may have come and gone meanwhile). Old counts stay until the new ones are in.</summary>
    public void Refresh()
    {
        if (!_shown)
        {
            _shown = true;
            return;
        }

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
        row.StopSuggesting();
        row.ClearNames();
        row.IsRenaming = true;
        RenameStarted?.Invoke(this, row);
        KeptNamesRead = ShowKeptNamesAsync(row, row.RenameText);
    }

    /// <summary>
    /// The names kept for the workspace (made earlier, asked for or in the background, 2026-11-11) are offered in the box at
    /// once, unless the user has typed meanwhile or asked for new ones.
    /// </summary>
    private async Task ShowKeptNamesAsync(WorkspaceRowViewModel row, string started)
    {
        if (_names is null)
        {
            return;
        }

        PreparedNames? kept;
        try
        {
            kept = await _names.GetAsync(row.Workspace.Id, row.Workspace.Name, CancellationToken.None);
        }
#pragma warning disable CA1031 // Kept names are a convenience: none is shown, the rename works as before.
        catch (Exception)
#pragma warning restore CA1031
        {
            return;
        }

        if (kept is { Names.Count: > 0 } && row.IsRenaming && !_disposed && row.RenameText == started && !row.HasNames && !row.IsSuggesting)
        {
            row.SetNames(kept.Names.Select(n => n.Value));
        }
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

        // A request still running is stopped; whatever it would have said belongs to this rename only.
        row.StopSuggesting();
        row.ClearNames();
        row.IsRenaming = false;
        row.RenameError = null;
    }

    /// <summary>
    /// Asks the model for names drawn from the row's conversations (2026-11-11; 换一批 once names are there). Nothing is
    /// renamed: a name is offered after the text in the box (Tab takes it) or picked from 更多建议, and saving stays the
    /// explicit Enter or 保存. A result that arrives after the request was stopped, restarted or its rename ended is
    /// dropped, so it never shows in another rename or another row.
    /// </summary>
    internal Task SuggestNamesAsync(WorkspaceRowViewModel row)
    {
        Suggesting = SuggestCoreAsync(row);
        return Suggesting;
    }

    private async Task SuggestCoreAsync(WorkspaceRowViewModel row)
    {
        if (_suggestions is null || _disposed || !row.IsRenaming)
        {
            return;
        }

        var request = row.StartSuggesting();
        SuggestionStateChanged?.Invoke(this, row);
        NameSuggestions result;
        try
        {
            result = await _suggestions.SuggestAsync(row.Workspace.Id, row.Workspace.Name, request.Token);
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user, the rename's end or leaving the page (the state is already reset), or by an exit,
            // which leaves the request current: then the row goes back to its button.
            if (row.Owns(request))
            {
                row.StopSuggesting();
                SuggestionStateChanged?.Invoke(this, row);
            }

            return;
        }
#pragma warning disable CA1031 // The workspace could not be read: say so beside the button, and the rename stays usable.
        catch (Exception)
#pragma warning restore CA1031
        {
            if (row.Owns(request))
            {
                row.ShowSuggestionProblem(UiText.SuggestionsFailed(null), retry: true);
                SuggestionStateChanged?.Invoke(this, row);
            }

            return;
        }

        if (!row.Owns(request) || !row.IsRenaming || _disposed)
        {
            return;
        }

        row.ShowSuggestions(result);
        SuggestionStateChanged?.Invoke(this, row);
    }

    /// <summary>Leaving the page stops every request; the renames themselves stay open.</summary>
    public void StopAllSuggestions()
    {
        foreach (var row in Rows)
        {
            row.StopSuggesting();
        }
    }

    /// <summary>Fills the box with a suggested name, still editable; nothing is saved.</summary>
    internal void PickSuggestion(WorkspaceRowViewModel row, string name)
    {
        if (!row.IsRenaming || row.IsSaving)
        {
            return;
        }

        row.RenameText = name;
        row.RenameError = null;
        SuggestionPicked?.Invoke(this, row);
    }

    internal void OpenModelSettings() => _navigator?.Navigate(new NavigationRequest(MainSection.Settings, SettingsHomeRoute.Instance));

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
            gone.Dispose();
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
                    row.SetCounts(contents.Conversations, contents.Summaries, contents.Archived);
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
        foreach (var row in Rows)
        {
            row.Dispose();
        }

        _directory.Workspaces.CollectionChanged -= OnWorkspacesChanged;
        if (_directory is INotifyPropertyChanged observable)
        {
            observable.PropertyChanged -= OnDirectoryChanged;
        }
    }
}

/// <summary>One workspace on 设置 › 工作区: its name (or the box renaming it), what it holds, and its actions.</summary>
public sealed partial class WorkspaceRowViewModel : ObservableObject, IDisposable
{
    private readonly WorkspacesViewModel _owner;

    internal WorkspaceRowViewModel(Workspace workspace, WorkspacesViewModel owner)
    {
        Workspace = workspace;
        _owner = owner;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(AutomationName), nameof(SwitchName), nameof(RenameName), nameof(DeleteName), nameof(SuggestName))]
    public partial Workspace Workspace { get; internal set; }

    /// <summary>
    /// The names offered in the box, best first (2026-11-11): kept from before when the rename starts, or just asked for.
    /// A new list each time, so the box starts a new offer.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNames), nameof(ShowsSuggestHint), nameof(ShowsGhostHint), nameof(ShowsCounts), nameof(ShowsChipList),
        nameof(ShowsSuggestionPanel), nameof(SuggestLabel), nameof(SuggestTip), nameof(SuggestName))]
    public partial IReadOnlyList<string> GhostNames { get; private set; } = [];

    /// <summary>更多建议: every name as a chip under the box (for the mouse; the keyboard has Tab).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsChipList), nameof(ShowsSuggestionPanel), nameof(ChipsToggleLabel), nameof(ChipsToggleName))]
    public partial bool ShowsChips { get; set; }

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
    [NotifyPropertyChangedFor(nameof(ShowsSuggestHint), nameof(ShowsGhostHint), nameof(ShowsCounts))]
    [NotifyCanExecuteChangedFor(nameof(SuggestNamesCommand))]
    public partial bool IsRenaming { get; internal set; }

    [ObservableProperty]
    public partial string RenameText { get; set; } = string.Empty;

    /// <summary>Why the name was not saved (invalid, storage paused, the write failed); the box keeps what was typed.</summary>
    [ObservableProperty]
    public partial string? RenameError { get; internal set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitRenameCommand), nameof(CancelRenameCommand), nameof(SuggestNamesCommand))]
    public partial bool IsSaving { get; internal set; }

    /// <summary>
    /// Whether the workspace holds anything to name it after: false once counted empty (or the service found nothing),
    /// null while not known, which leaves the button available.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SuggestTip), nameof(ShowsSuggestHint), nameof(ShowsGhostHint), nameof(ShowsCounts))]
    [NotifyCanExecuteChangedFor(nameof(SuggestNamesCommand))]
    public partial bool? HasContent { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSuggesting), nameof(HasSuggestions), nameof(HasSuggestionProblem), nameof(ShowsSuggestionPanel))]
    [NotifyCanExecuteChangedFor(nameof(SuggestNamesCommand))]
    public partial NameSuggestionState SuggestionState { get; private set; }

    /// <summary>The same names as chips to click (2–3, fewer when fewer were usable); shown under 更多建议.</summary>
    public ObservableCollection<NameSuggestionViewModel> Suggestions { get; } = [];

    /// <summary>Why there are no names: not configured, storage paused, the service failed, nothing usable came back.</summary>
    [ObservableProperty]
    public partial string? SuggestionProblem { get; private set; }

    /// <summary>The problem is fixed in the model settings: 前往设置 is offered.</summary>
    [ObservableProperty]
    public partial bool SuggestionNeedsSettings { get; private set; }

    /// <summary>Asking again may help: 重试 is offered.</summary>
    [ObservableProperty]
    public partial bool SuggestionCanRetry { get; private set; }

    public bool IsSuggesting => SuggestionState == NameSuggestionState.Generating;

    /// <summary>Names just arrived from a request (the view moves the focus to the box, or to the first chip).</summary>
    public bool HasSuggestions => SuggestionState == NameSuggestionState.Shown;

    public bool HasSuggestionProblem => SuggestionState == NameSuggestionState.Failed;

    /// <summary>There are names to offer (kept from before or just asked for).</summary>
    public bool HasNames => GhostNames.Count > 0;

    /// <summary>The chips are shown: asked for with 更多建议, and there are names.</summary>
    public bool ShowsChipList => ShowsChips && HasNames;

    /// <summary>Under the box: a request running, a problem, or the chips.</summary>
    public bool ShowsSuggestionPanel => IsSuggesting || HasSuggestionProblem || ShowsChipList;

    /// <summary>The 建议名称 button is there at all (the page was given the service).</summary>
    public bool CanSuggestNames => _owner.CanSuggestNames;

    /// <summary>
    /// While renaming a workspace with something in it and no names yet, the line under the box says what 建议名称 sends.
    /// </summary>
    public bool ShowsSuggestHint => IsRenaming && HasContent != false && _owner.CanSuggestNames && !HasNames;

    /// <summary>Once names are offered, the line under the box says what Tab does, with 更多建议.</summary>
    public bool ShowsGhostHint => IsRenaming && HasNames;

    public bool ShowsCounts => !ShowsSuggestHint && !ShowsGhostHint;

    /// <summary>建议名称, or 换一批 once names are there.</summary>
    public string SuggestLabel => HasNames ? UiText.SuggestOtherNames : UiText.SuggestNames;

    public string SuggestTip => HasContent == false ? UiText.SuggestNamesEmptyTip : HasNames ? UiText.SuggestOtherNamesTip : UiText.SuggestNamesTip;

    public string SuggestName => HasNames ? UiText.SuggestOtherNamesName(Name) : UiText.SuggestNamesName(Name);

    public string ChipsToggleLabel => ShowsChips ? UiText.HideNameChips : UiText.ShowNameChips;

    public string ChipsToggleName => ShowsChips ? UiText.HideNameChipsName : UiText.ShowNameChipsName;

    public string AutomationName => UiText.WorkspaceRowName(Name, IsCurrent, CountsText);

    public string SwitchName => UiText.SwitchToWorkspaceName(Name);

    public string RenameName => UiText.RenameWorkspaceName(Name);

    public string DeleteName => UiText.DeleteWorkspaceName(Name);

    internal void SetCounts(int conversations, int summaries, int archived = 0)
    {
        CountsText = UiText.WorkspaceCounts(conversations, summaries, archived);
        HasContent = conversations + summaries > 0;
    }

    private CancellationTokenSource? _suggestion;

    /// <summary>Starts a request: any earlier one is stopped and its message cleared; the names offered stay until new ones arrive.</summary>
    internal CancellationTokenSource StartSuggesting()
    {
        StopSuggesting();
        _suggestion = new CancellationTokenSource();
        SuggestionState = NameSuggestionState.Generating;
        return _suggestion;
    }

    /// <summary>The request is still this row's current one (not stopped, not replaced).</summary>
    internal bool Owns(CancellationTokenSource request) => ReferenceEquals(_suggestion, request);

    /// <summary>Stops a running request and clears its message: the row shows its button again (the names offered stay).</summary>
    internal void StopSuggesting()
    {
        if (_suggestion is { } running)
        {
            _suggestion = null;
            running.Cancel();
            running.Dispose();
        }

        SuggestionProblem = null;
        SuggestionNeedsSettings = false;
        SuggestionCanRetry = false;
        SuggestionState = NameSuggestionState.Idle;
    }

    internal void ShowSuggestions(NameSuggestions result)
    {
        switch (result.Problem)
        {
            case null when result.Names.Count > 0:
                Release();
                SetNames(result.Names.Select(n => n.Value));
                SuggestionProblem = null;
                SuggestionNeedsSettings = false;
                SuggestionCanRetry = false;
                SuggestionState = NameSuggestionState.Shown;
                break;
            case null:
                ShowSuggestionProblem(UiText.SuggestionsNone, retry: true);
                break;
            case NameSuggestionProblem.EmptyWorkspace:
                HasContent = false;
                ShowSuggestionProblem(UiText.SuggestionsEmpty, retry: false);
                break;
            case NameSuggestionProblem.NotConfigured:
                ShowSuggestionProblem(UiText.SuggestionsNotConfigured, retry: false, settings: true);
                break;
            case NameSuggestionProblem.StoragePaused:
                ShowSuggestionProblem(UiText.SuggestionsStoragePaused, retry: true);
                break;
            case NameSuggestionProblem.Unavailable:
                ShowSuggestionProblem(UiText.SuggestionsUnavailable, retry: false);
                break;
            default:
                // A missing key is fixed in the settings; anything else may work on a second try.
                ShowSuggestionProblem(UiText.SuggestionsFailed(result.Error), retry: true, settings: result.Error == GenerationErrorKind.NotConfigured);
                break;
        }
    }

    internal void ShowSuggestionProblem(string message, bool retry, bool settings = false)
    {
        // The names offered before stay offered: a failed 换一批 takes nothing away.
        Release();
        SuggestionProblem = message;
        SuggestionCanRetry = retry;
        SuggestionNeedsSettings = settings;
        SuggestionState = NameSuggestionState.Failed;
    }

    /// <summary>Offers these names in the box (a new offer) and as chips.</summary>
    internal void SetNames(IEnumerable<string> names)
    {
        string[] list = [.. names];
        Suggestions.Clear();
        foreach (var name in list)
        {
            Suggestions.Add(new NameSuggestionViewModel(name, this, _owner));
        }

        GhostNames = list;
    }

    /// <summary>The rename ended (or starts afresh): no names, chips folded.</summary>
    internal void ClearNames()
    {
        Suggestions.Clear();
        GhostNames = [];
        ShowsChips = false;
    }

    [RelayCommand]
    private void ToggleChips() => ShowsChips = !ShowsChips;

    /// <summary>The request has answered: it is no longer running and needs no stopping.</summary>
    private void Release()
    {
        _suggestion?.Dispose();
        _suggestion = null;
    }

    private bool CanSuggest() => IsRenaming && !IsSaving && HasContent != false && SuggestionState != NameSuggestionState.Generating;

    /// <summary>
    /// Starts a request (its task is the page's <see cref="WorkspacesViewModel.Suggesting"/>). Not an async command: one
    /// that was stopped may take a moment to end, and the button must be usable again at once.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSuggest))]
    private void SuggestNames() => _ = _owner.SuggestNamesAsync(this);

    [RelayCommand]
    private void StopSuggestingNames() => StopSuggesting();

    /// <summary>The row left the page (its workspace is gone, or the page closed): a request still running is stopped.</summary>
    public void Dispose() => StopSuggesting();

    [RelayCommand]
    private void OpenModelSettings() => _owner.OpenModelSettings();

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

/// <summary>Where a rename row's suggested names stand.</summary>
public enum NameSuggestionState
{
    /// <summary>Nothing asked: the row shows its 建议名称 button only.</summary>
    Idle,

    /// <summary>The model is being asked; 停止 or Esc stops it.</summary>
    Generating,

    /// <summary>Names to pick from.</summary>
    Shown,

    /// <summary>No names, and why (with 重试 or 前往设置 when that helps).</summary>
    Failed,
}

/// <summary>One suggested name: picking it fills the rename box (nothing is saved).</summary>
public sealed partial class NameSuggestionViewModel
{
    private readonly WorkspaceRowViewModel _row;
    private readonly WorkspacesViewModel _owner;

    internal NameSuggestionViewModel(string name, WorkspaceRowViewModel row, WorkspacesViewModel owner)
    {
        Name = name;
        _row = row;
        _owner = owner;
    }

    public string Name { get; }

    public string AutomationName => UiText.SuggestionChipName(Name);

    [RelayCommand]
    private void Pick() => _owner.PickSuggestion(_row, Name);
}
