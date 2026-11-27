using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application;
using Mindweave.Application.Exchange;
using Mindweave.Application.Import;
using Mindweave.Application.ManualBackup;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels;

/// <summary>A workspace the file can be imported into; null for a new one.</summary>
public sealed partial class ImportTargetViewModel(ImportViewModel owner, string text, WorkspaceId? workspace) : ViewModelBase
{
    public string Text { get; } = text;

    public WorkspaceId? Workspace { get; } = workspace;

    [ObservableProperty]
    public partial bool IsChosen { get; set; }

    partial void OnIsChosenChanged(bool value)
    {
        if (value)
        {
            owner.Choose(this);
        }
    }
}

/// <summary>One workspace of a manual backup in the dialog: its name, and what happens to it or happened.</summary>
public sealed record ImportBackupGroup(string Heading, IReadOnlyList<string> Lines);

/// <summary>
/// The import dialog (docs/design/sharing-import.md §5): the share's title and kind, the workspace, what will happen, and
/// Import; or why the file is refused. Nothing is written before Import, and then all of it or nothing. A manual backup
/// (docs/design/sharing-backup.md §5) comes through the same dialog: it has no workspace to choose, shows per workspace
/// what will happen, and afterwards names what was done.
/// </summary>
public sealed partial class ImportViewModel(
    ISharedFileReader reader,
    IImports imports,
    IManualBackupImports backups,
    ISystemShell shell,
    IClock clock,
    Workbench workbench,
    IWorkspaceSwitcher switcher) : ViewModelBase
{
    private ExchangeDocument? _document;
    private ManualBackupFile? _backup;
    private bool _blocked;
    private bool _reading;
    private IReadOnlyDictionary<ConversationId, WorkspaceId> _holdings = new Dictionary<ConversationId, WorkspaceId>();
    private StoredContents _stored = StoredContents.Nothing;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand), nameof(CloseCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>The share's own title; the file's name while it is refused.</summary>
    [ObservableProperty]
    public partial string ShareTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? KindText { get; private set; }

    /// <summary>The workspaces it can go into, the one offered first chosen; empty when there is nothing to import.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ImportTargetViewModel> Targets { get; private set; } = [];

    /// <summary>What an import into the chosen workspace would do, one line each.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCounts))]
    public partial IReadOnlyList<string> Counts { get; private set; } = [];

    /// <summary>Why it is refused, or why the import did not happen.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <summary>Everything in the file exists already, here or in another workspace.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsChoice), nameof(CloseText))]
    public partial bool IsAllHere { get; private set; }

    /// <summary>The workspaces and the Import button; not for a refused file, nor when nothing is new anywhere.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsChoice), nameof(CloseText))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial bool IsReady { get; private set; }

    public bool ShowsChoice => IsReady && !IsAllHere && !IsDone;

    /// <summary>A share goes into a workspace the user chooses; a manual backup goes where each workspace already is.</summary>
    public bool ShowsTargets => ShowsChoice && !IsBackup;

    public string CloseText => ShowsChoice ? UiText.Cancel : UiText.ShareClose;

    /// <summary>The file is a manual backup's package.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(ShowsTargets), nameof(AllHereText))]
    public partial bool IsBackup { get; private set; }

    /// <summary>A manual backup was imported; the dialog names what was done.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsChoice), nameof(ShowsTargets), nameof(CloseText), nameof(CountsHeading))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial bool IsDone { get; private set; }

    public string Title => IsBackup ? UiText.ImportBackupTitle : UiText.ImportTitle;

    public string CountsHeading => IsDone ? UiText.ImportBackupDoneHeading : UiText.ImportCountsHeading;

    public string AllHereText => IsBackup ? UiText.ImportBackupAllHere : UiText.ImportAllHere;

    /// <summary>For a manual backup: each workspace with what will happen to it, or what happened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCounts))]
    public partial IReadOnlyList<ImportBackupGroup> BackupWorkspaces { get; private set; } = [];

    public bool HasCounts => Counts.Count > 0 || BackupWorkspaces.Count > 0;

    /// <summary>The chosen workspace would gain something.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial bool BringsAnything { get; private set; }

    /// <summary>The most recent pick or drop; tests await it.</summary>
    internal Task Opening { get; private set; } = Task.CompletedTask;

    /// <summary>"Import…": asks for the file, then shows what is in it.</summary>
    public void Pick() => Opening = PickAsync();

    /// <summary>A file dropped on the window.</summary>
    public void Drop(string path) => Opening = OpenAsync(path);

    /// <summary>Whether one dropped file is taken: a page, as a share is, while the workbench is there and the dialog is not.</summary>
    public bool Takes(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return !IsOpen && workbench.CurrentWorkspace is not null && paths.Count == 1
            && Path.GetExtension(paths[0]) is var extension
            && (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase) || IsPackage(paths[0]));
    }

    /// <summary>A manual backup is told by its own extension.</summary>
    private bool IsPackage(string path) => Path.GetExtension(path).Equals("." + backups.Extension, StringComparison.OrdinalIgnoreCase);

    private async Task PickAsync()
    {
        if (await shell.PickSharedFileAsync(CancellationToken.None) is { } path)
        {
            await OpenAsync(path);
        }
    }

    /// <summary>Reads the file and opens the dialog on what it holds, or on why it is refused.</summary>
    public async Task OpenAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (IsOpen || _reading || workbench.CurrentWorkspace is null)
        {
            return;
        }

        // Read before it is shown: it opens with its buttons as they stay, so it can take the keyboard at once.
        _reading = true;
        (_document, KindText, Targets, Counts, Problem, BringsAnything, IsReady, IsAllHere) = (null, null, [], [], null, false, false, false);
        (_backup, _blocked, BackupWorkspaces, IsDone, IsBackup) = (null, false, [], false, IsPackage(path));
        ShareTitle = Path.GetFileName(path);
        try
        {
            if (IsBackup)
            {
                await OpenBackupAsync(path);
                return;
            }

            var read = await reader.ReadAsync(path, CancellationToken.None);
            if (read.Document is not { } document)
            {
                Problem = UiText.ImportRefused(read.Refusal ?? ImportRefusal.Damaged);
                return;
            }

            await CompareAsync(document);
            if (ImportRules.Plan(document, _holdings, null) is null)
            {
                // A parent neither in the file nor here (§7).
                Problem = UiText.ImportRefused(ImportRefusal.Damaged);
                return;
            }

            _document = document;
            ShareTitle = ImportRules.OneLine(document.Title) is { Length: > 0 } title ? title : UiText.ImportedWorkspace;
            KindText = document.Kind == ExchangeDocument.ContentKind ? UiText.ImportKindContent : UiText.ImportKindStructure;
            Offer();
        }
#pragma warning disable CA1031 // An unreadable store is said; closing still works.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            (_document, IsReady, Targets, Counts) = (null, false, [], []);
            Problem = UiText.ImportFailed;
        }
        finally
        {
            _reading = false;
            IsOpen = true;
        }
    }

    private async Task OpenBackupAsync(string path)
    {
        var read = await backups.ReadAsync(path, CancellationToken.None);
        if (read.File is not { } file)
        {
            Problem = UiText.ImportBackupRefused(read.Refusal ?? ImportRefusal.Damaged);
            return;
        }

        _backup = file;
        KindText = UiText.ImportBackupKind(file.MadeAt);
        Plan(await backups.PlanAsync(file, CancellationToken.None));
    }

    /// <summary>What importing the backup would do now, per workspace; nothing to choose.</summary>
    private void Plan(ManualBackupPlan plan)
    {
        _blocked = plan.IsBlocked;
        IsAllHere = plan.IsEmpty;
        BringsAnything = !plan.IsEmpty;
        Problem = _blocked ? UiText.ImportBackupRunning : null;
        Counts = _backup is { Mismatched: > 0 and var damaged } ? [UiText.ImportBackupMismatched(damaged)] : [];
        BackupWorkspaces = [.. plan.Workspaces.Select(w => new ImportBackupGroup(UiText.ImportBackupWorkspace(NameOf(w), w.Create), [.. Lines(w)]))];
        IsReady = true;
        ImportCommand.NotifyCanExecuteChanged();
    }

    private static IEnumerable<string> Lines(ManualBackupWorkspacePlan plan)
    {
        if (plan.New > 0)
        {
            yield return UiText.ImportNew(plan.New);
        }

        if (plan.GainsMessages > 0)
        {
            yield return UiText.ImportGainsMessages(plan.GainsMessages);
        }

        if (plan.Joined > 0)
        {
            yield return UiText.ImportBackupJoined(plan.Joined);
        }

        if (plan.GainsOther > 0)
        {
            yield return UiText.ImportGainsMore(plan.GainsOther);
        }

        if (plan.Links.Count > 0)
        {
            yield return UiText.ImportLinks(plan.Links.Count);
        }

        if (plan.AlreadyHere > 0)
        {
            yield return UiText.ImportHere(plan.AlreadyHere);
        }

        if (plan.Elsewhere > 0)
        {
            yield return UiText.ImportBackupElsewhere(plan.Elsewhere);
        }

        if (plan.Unfit > 0)
        {
            yield return UiText.ImportUnfit(plan.Unfit);
        }
    }

    private static IEnumerable<string> Done(ManualBackupWorkspacePlan plan)
    {
        if (plan.New > 0)
        {
            yield return UiText.ImportBackupDoneNew(plan.New);
        }

        if (plan.GainsMessages > 0)
        {
            yield return UiText.ImportBackupDoneGained(plan.GainsMessages);
        }

        if (plan.Joined > 0)
        {
            yield return UiText.ImportBackupDoneJoined(plan.Joined);
        }

        if (plan.GainsOther > 0)
        {
            yield return UiText.ImportBackupDoneMore(plan.GainsOther);
        }

        if (plan.Links.Count > 0)
        {
            yield return UiText.ImportBackupDoneLinks(plan.Links.Count);
        }
    }

    /// <summary>A workspace that is here keeps its own name; one that is new has the backup's.</summary>
    private string NameOf(ManualBackupWorkspacePlan plan) =>
        workbench.Workspaces.FirstOrDefault(w => w.Id == plan.Workspace.Id)?.Name.Value ?? plan.Workspace.Name;

    private async Task ImportBackupAsync(ManualBackupFile file)
    {
        IsBusy = true;
        Problem = null;
        try
        {
            var result = await backups.ImportAsync(file, CancellationToken.None);
            switch (result.Status)
            {
                case ManualBackupImportStatus.Imported:
                    foreach (var created in result.Created.Where(c => workbench.Workspaces.All(w => w.Id != c.Id)))
                    {
                        workbench.Workspaces.Add(created);
                    }

                    BackupWorkspaces = [.. result.Workspaces.Select(w => new ImportBackupGroup(UiText.ImportBackupWorkspace(NameOf(w), w.Create), [.. Done(w)]))];
                    Counts = result.MissingOriginals > 0 ? [UiText.ImportBackupDoneMissing(result.MissingOriginals)] : [];
                    IsDone = true;
                    break;
                default:
                    // Something here changed while the dialog was open: what it would do now.
                    Plan(await backups.PlanAsync(file, CancellationToken.None));
                    break;
            }
        }
        catch (StorageWriteException)
        {
            Problem = UiText.ImportStoragePaused;
        }
#pragma warning disable CA1031 // Anything else is reported, never thrown at the UI thread; the transaction left nothing.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(ex);
            Problem = UiText.ImportFailed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CompareAsync(ExchangeDocument document)
    {
        _holdings = await imports.HoldingsAsync(document, CancellationToken.None);
        _stored = await imports.ContentsAsync(document, CancellationToken.None);
    }

    internal void Choose(ImportTargetViewModel target)
    {
        foreach (var other in Targets.Where(t => !ReferenceEquals(t, target)))
        {
            other.IsChosen = false;
        }

        Problem = null;
        if (_document is { } document && ImportRules.Plan(document, _holdings, target.Workspace, _stored) is { } plan)
        {
            BringsAnything = !plan.IsEmpty;
            Counts = [.. Lines(plan)];
        }
    }

    private bool CanImport() => IsReady && !IsBusy && !IsAllHere && BringsAnything && !IsDone && !(IsBackup && _blocked);

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (_backup is { } backup)
        {
            await ImportBackupAsync(backup);
            return;
        }

        if (_document is not { } document || Targets.FirstOrDefault(t => t.IsChosen) is not { } chosen)
        {
            return;
        }

        IsBusy = true;
        Problem = null;
        (Workspace? Created, WorkspaceId Target, ConversationId Opens)? done = null;
        try
        {
            var created = chosen.Workspace is null ? new Workspace(WorkspaceId.New(), NewName(), clock.UtcNow) : null;
            var target = created is null ? ImportTarget.Existing(chosen.Workspace!.Value) : ImportTarget.New(created);
            var result = await imports.ImportAsync(document, target, CancellationToken.None);
            switch (result.Status)
            {
                case ImportStatus.Imported:
                    done = (created, target.Id, result.Opens!.Value);
                    break;
                case ImportStatus.WorkspaceMissing:
                    Problem = UiText.ImportWorkspaceMissing;
                    break;
                case ImportStatus.DoesNotFit:
                    Problem = UiText.ImportDoesNotFit;
                    break;
                default:
                    // What was new arrived another way while the dialog was open.
                    await CompareAsync(document);
                    Offer();
                    break;
            }
        }
        catch (StorageWriteException)
        {
            Problem = UiText.ImportStoragePaused;
        }
#pragma warning disable CA1031 // Anything else is reported, never thrown at the UI thread; the transaction left nothing.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(ex);
            Problem = UiText.ImportFailed;
        }
        finally
        {
            IsBusy = false;
        }

        if (done is { } imported)
        {
            // Outside the try: it is imported, whatever happens while it is shown.
            Close();
            Show(imported.Created, imported.Target, imported.Opens);
        }
    }

    private bool CanClose() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close()
    {
        IsOpen = false;
        _document = null;
        _backup = null;
    }

    /// <summary>The first imported root opens in its workspace (§5).</summary>
    private void Show(Workspace? created, WorkspaceId target, ConversationId opens)
    {
        if (created is not null)
        {
            workbench.Workspaces.Add(created);
        }

        if (workbench.CurrentWorkspace?.Id != target && workbench.Workspaces.FirstOrDefault(w => w.Id == target) is { } workspace)
        {
            switcher.SwitchTo(workspace);
        }

        if (workbench.CurrentWorkspace?.Id == target)
        {
            workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(opens)));
        }
    }

    /// <summary>
    /// The workspaces to choose from. First the one that holds most of the file; when none holds any, a new one, which is
    /// also the easiest to remove again.
    /// </summary>
    private void Offer()
    {
        if (_document is not { } document || workbench.CurrentWorkspace is not { } current)
        {
            return;
        }

        var home = ImportRules.Home(document, _holdings);
        var targets = new List<ImportTargetViewModel>();
        var fresh = new ImportTargetViewModel(this, UiText.ImportIntoNew(NewName().Value), null);
        if (home is null)
        {
            targets.Add(fresh);
        }
        else if (home != current.Id && workbench.Workspaces.FirstOrDefault(w => w.Id == home) is { } holder)
        {
            targets.Add(new ImportTargetViewModel(this, UiText.ImportIntoExisting(holder.Name.Value), home));
        }

        targets.Add(new ImportTargetViewModel(this, UiText.ImportIntoCurrent(current.Name.Value), current.Id));
        if (home is not null)
        {
            targets.Add(fresh);
        }

        // Whether a conversation is new depends on the workspace only through its parent: where the parent is, it can be added.
        // One that is here can still gain something where it is, or differ from the file.
        IsAllHere = document.Conversations.All(c => _holdings.ContainsKey(c.Id))
            && _holdings.Values.Distinct().All(w => ImportRules.Plan(document, _holdings, w, _stored) is { IsEmpty: true, Unfit: 0, Own: 0 });
        Targets = targets;
        IsReady = true;
        targets[0].IsChosen = true;
        if (IsAllHere)
        {
            Counts = [.. document.Conversations.GroupBy(c => _holdings[c.Id]).Select(g => UiText.ImportAllHereIn(NameOf(g.Key), g.Count()))];
        }
    }

    private IEnumerable<string> Lines(ImportPlan plan)
    {
        if (plan.New.Count > 0)
        {
            yield return UiText.ImportNew(plan.New.Count);
        }

        if (plan.Gains.Count(g => g.Messages.Count > 0) is > 0 and var messages)
        {
            yield return UiText.ImportGainsMessages(messages);
        }

        if (plan.Gains.Count(g => g.Messages.Count == 0) is > 0 and var more)
        {
            yield return UiText.ImportGainsMore(more);
        }

        if (plan.Links.Count > 0)
        {
            yield return UiText.ImportLinks(plan.Links.Count);
        }

        if (plan.AlreadyHere > 0)
        {
            yield return UiText.ImportHere(plan.AlreadyHere);
        }

        if (plan.Own > 0)
        {
            yield return UiText.ImportOwn(plan.Own);
        }

        if (plan.Unfit > 0)
        {
            yield return UiText.ImportUnfit(plan.Unfit);
        }

        foreach (var skipped in plan.Skipped)
        {
            yield return UiText.ImportSkipped(skipped.Conversations, NameOf(skipped.Workspace));
        }
    }

    private string NameOf(WorkspaceId id) => workbench.Workspaces.FirstOrDefault(w => w.Id == id)?.Name.Value ?? UiText.ImportOtherWorkspace;

    /// <summary>The share's title as a workspace's name, cut where it is longer than one may be.</summary>
    private WorkspaceName NewName() =>
        WorkspaceName.TryCreate(ConversationRules.FirstTextElements(ShareTitle, WorkspaceName.MaxLength), out var name, out _) ? name! : WorkspaceName.FromStored(UiText.ImportedWorkspace);
}
