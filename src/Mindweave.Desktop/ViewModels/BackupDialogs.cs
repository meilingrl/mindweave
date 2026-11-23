using Mindweave.Desktop.Services;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application;
using Mindweave.Application.Startup;

namespace Mindweave.Desktop.ViewModels;

/// <summary>
/// Ends this session and starts the next one on the restored data (2026-11-12); the application host implements it.
/// Called only after the current data is in <paramref name="keptCopy"/> and every task is stopped and saved.
/// </summary>
public interface IInAppRestore
{
    Task RestoreAsync(BackupInfo backup, string keptCopy);
}

/// <summary>Where nothing can replace the session (services built without a host): a restore is refused.</summary>
internal sealed class UnavailableInAppRestore : IInAppRestore
{
    public Task RestoreAsync(BackupInfo backup, string keptCopy) =>
        throw new InvalidOperationException("Restoring needs the application host's session.");
}

/// <summary>One row of the storage page's backup list: a backup, or the files one earlier restore put aside.</summary>
/// <param name="IsNewestBeforeRestore">The newest backup made before a restore: the way back to the data before the last one.</param>
public sealed record BackupEntry(BackupInfo? Backup, LeftoverDatabase? Leftover, bool IsNewestBeforeRestore = false)
{
    public DateTimeOffset At => Backup?.CreatedAt ?? Leftover!.ReplacedAt;

    public long SizeBytes => Backup?.SizeBytes ?? Leftover!.SizeBytes;

    /// <summary>A stable identity across refreshes: the backup's path, or the leftover's first file.</summary>
    public string Key => Backup?.Path ?? Leftover!.Files[0];
}

/// <summary>The storage page's way to the window's restore and delete dialogs, and their way back to it.</summary>
public interface IBackupPrompts
{
    /// <summary>Something was deleted or a restore did not go ahead: the list is read again.</summary>
    event EventHandler? Changed;

    void Restore(BackupInfo backup);

    void Delete(IReadOnlyList<BackupEntry> entries);
}

/// <summary>Hands the storage page's requests to the window's dialogs (the page factory then has no cycle).</summary>
public sealed class BackupRequests : IBackupPrompts
{
    public event EventHandler? Changed;

    public event EventHandler<BackupInfo>? RestoreRequested;

    public event EventHandler<IReadOnlyList<BackupEntry>>? DeleteRequested;

    public void Restore(BackupInfo backup) => RestoreRequested?.Invoke(this, backup);

    public void Delete(IReadOnlyList<BackupEntry> entries) => DeleteRequested?.Invoke(this, entries);

    internal void Change() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// The restore confirmation (2026-11-12; architecture §12.2–12.3, requirements §9.3). It says when and why the backup was
/// made, that every workspace goes back to that moment, that the current data is first kept as a "恢复前" backup and that
/// running work stops; for a backup made before a delete, that the rest rolls back too. On 恢复 the backup is checked
/// first (a bad one changes nothing and is said here); then the exit sequence's own prompt (running tasks) and
/// its stop-and-save steps run, then the current data is backed up and verified, and only then does the
/// session hand over to the next one, which puts the backup in place. 取消 is the default.
/// </summary>
public sealed partial class BackupRestoreViewModel : ViewModelBase, IDisposable
{
    private readonly IBackupFiles _files;
    private readonly IDatabaseBackup _backups;
    private readonly ExitCoordinator _exit;
    private readonly IInAppRestore _restore;
    private readonly BackupRequests _requests;
    private readonly int _schemaVersion;

    public BackupRestoreViewModel(IBackupFiles files, IDatabaseBackup backups, ExitCoordinator exit, IInAppRestore restore, BackupRequests requests, AppEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(environment);
        _files = files;
        _backups = backups;
        _exit = exit;
        _restore = restore;
        _requests = requests;
        _schemaVersion = environment.SchemaVersion;
        requests.RestoreRequested += OnRestoreRequested;
    }

    /// <summary>Stops listening to the storage page's requests; the view it belongs to is being replaced.</summary>
    public void Dispose() => _requests.RestoreRequested -= OnRestoreRequested;

    private void OnRestoreRequested(object? sender, BackupInfo backup) => Open(backup);

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial BackupInfo? Target { get; private set; }

    public string Title => Target is { } backup ? UiText.RestoreTitle(backup.CreatedAt) : string.Empty;

    /// <summary>What the restore does, one sentence each.</summary>
    public ObservableCollection<string> Points { get; } = [];

    /// <summary>Why nothing was restored: a bad backup, or the current data could not be backed up.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>The last press of 恢复 up to the hand-over (or to its end without one); tests await it.</summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    public void Open(BackupInfo backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (IsBusy)
        {
            return;
        }

        Target = backup;
        Problem = null;
        Points.Clear();
        Points.Add(UiText.RestoreMadeAt(backup));
        Points.Add(UiText.RestoreAllWorkspaces);
        if (backup.Reason is BackupReason.BeforeWorkspaceDelete or BackupReason.BeforeConversationDelete)
        {
            Points.Add(UiText.RestoreBringsBackDeleted);
        }

        Points.Add(UiText.RestoreKeepsCurrent);
        Points.Add(UiText.RestoreStopsWork);
        if (backup.SchemaVersion < _schemaVersion)
        {
            Points.Add(UiText.RestoreUpgrades(backup.SchemaVersion));
        }

        IsOpen = true;
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        IsOpen = false;
        Target = null;
    }

    private bool CanCancel() => !IsBusy;

    private bool CanRestore() => Target is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreAsync()
    {
        Running = RunAsync();
        return Running;
    }

    private async Task RunAsync()
    {
        if (Target is not { } backup)
        {
            return;
        }

        IsBusy = true;
        Problem = null;

        // Checked before any prompt: answering them for a backup that cannot be used would be for nothing.
        var problem = await Task.Run(() => _files.Check(backup));
        if (problem != BackupProblem.None)
        {
            Problem = UiText.BackupProblemText(problem);
            IsBusy = false;
            _requests.Change();
            return;
        }

        var ended = new TaskCompletionSource();
        if (!_exit.BeginRestore(() => HandOverAsync(backup), () => { IsBusy = false; ended.TrySetResult(); }))
        {
            IsBusy = false;
            return;
        }

        // Up to the hand-over or to the end without one; the exit coordinator owns what happens in between.
        await Task.WhenAny(ended.Task, _handedOver.Task);
    }

    private TaskCompletionSource _handedOver = new();

    /// <summary>
    /// Everything is stopped and saved: the current data is backed up and verified first (if that fails nothing is
    /// restored), then the session hands over. False when it did not go ahead.
    /// </summary>
    private async Task<bool> HandOverAsync(BackupInfo backup)
    {
        string kept;
        try
        {
            kept = await _backups.BackupAsync(BackupReason.BeforeRestore, CancellationToken.None);
        }
#pragma warning disable CA1031 // Any failure means: nothing is restored, the reason is shown, work goes on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(ex);
            Problem = UiText.RestoreKeptCopyFailed;
            return false;
        }

        IsOpen = false;
        var handedOver = _handedOver;
        _handedOver = new TaskCompletionSource();
        handedOver.TrySetResult();
        await _restore.RestoreAsync(backup, kept);
        return true;
    }
}

/// <summary>
/// Deleting backups (2026-11-12): one, or the page's selection, with one confirmation that says what is freed and that it
/// cannot be undone; a backup made before a delete may be the only copy of what was deleted, and the newest "恢复前"
/// backup is the way back to the data before the last restore. Files are removed one by one; any that cannot be (in use,
/// no permission) are said and stay selected for another try. 取消 is the default.
/// </summary>
public sealed partial class BackupDeletionViewModel : ViewModelBase, IDisposable
{
    private readonly IBackupFiles _files;
    private readonly BackupRequests _requests;

    public BackupDeletionViewModel(IBackupFiles files, BackupRequests requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        _files = files;
        _requests = requests;
        requests.DeleteRequested += OnDeleteRequested;
    }

    /// <summary>Stops listening to the storage page's requests; the view it belongs to is being replaced.</summary>
    public void Dispose() => _requests.DeleteRequested -= OnDeleteRequested;

    private void OnDeleteRequested(object? sender, IReadOnlyList<BackupEntry> entries) => Open(entries);

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Body))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    public partial IReadOnlyList<BackupEntry> Targets { get; private set; } = [];

    public string Title => UiText.DeleteBackupsTitle(Targets.Count);

    public string Body => UiText.DeleteBackupsBody(Targets.Count, Targets.Sum(t => t.SizeBytes));

    /// <summary>What may be lost beyond the space: the only copy of something deleted, or the way back from a restore.</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty]
    public partial string? Problem { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>The last delete; tests await it.</summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    public void Open(IReadOnlyList<BackupEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (IsBusy || entries.Count == 0)
        {
            return;
        }

        Problem = null;
        Show(entries);
        IsOpen = true;
    }

    private void Show(IReadOnlyList<BackupEntry> entries)
    {
        Targets = [.. entries];
        Warnings.Clear();
        if (entries.Any(e => e.Backup?.Reason == BackupReason.BeforeWorkspaceDelete))
        {
            Warnings.Add(UiText.DeleteOnlyCopyOfWorkspace);
        }

        if (entries.Any(e => e.Backup?.Reason == BackupReason.BeforeConversationDelete))
        {
            Warnings.Add(UiText.DeleteOnlyCopyOfConversations);
        }

        if (entries.Any(e => e.IsNewestBeforeRestore))
        {
            Warnings.Add(UiText.DeleteWayBackFromRestore);
        }

        if (entries.Any(e => e.Leftover is not null))
        {
            Warnings.Add(UiText.DeleteLeftoverFiles);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        IsOpen = false;
        Targets = [];
    }

    private bool CanCancel() => !IsBusy;

    private bool CanDelete() => Targets.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteAsync()
    {
        Running = RunAsync();
        return Running;
    }

    private async Task RunAsync()
    {
        IsBusy = true;
        Problem = null;
        var entries = Targets;
        var failed = await Task.Run(() => entries.Where(e => !TryDelete(e)).ToList());
        IsBusy = false;
        _requests.Change();
        if (failed.Count == 0)
        {
            IsOpen = false;
            Targets = [];
            return;
        }

        Show(failed);
        Problem = UiText.BackupsDeleteFailed(failed.Count);
    }

    private bool TryDelete(BackupEntry entry)
    {
        try
        {
            if (entry.Backup is { } backup)
            {
                _files.Delete(backup);
            }
            else if (entry.Leftover is { } leftover)
            {
                _files.Delete(leftover);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
