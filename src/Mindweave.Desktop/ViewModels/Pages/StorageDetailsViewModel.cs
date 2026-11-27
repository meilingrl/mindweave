using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application;
using Mindweave.Application.Startup;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// Where data lives, and its backups (2026-11-12): each can be restored (except one from a newer build, which says why)
/// or deleted, several at once through their check boxes; the total size is shown, and the files earlier restores put
/// aside are listed with them so they can be removed too. Nothing is ever removed without a confirmation. The list is
/// read again when the page is shown again and after a restore or delete dialog changed something.
/// </summary>
public sealed partial class StorageDetailsViewModel : PageViewModel, IDisposable
{
    private readonly ISystemShell _shell;
    private readonly IBackupFiles _files;
    private readonly IBackupPrompts _prompts;

    public StorageDetailsViewModel(AppEnvironment environment, IBackupFiles files, ISystemShell shell, IBackupPrompts prompts)
        : base(StorageDetailsRoute.Instance)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        Environment = environment;
        _files = files;
        _shell = shell;
        _prompts = prompts;
        prompts.Changed += OnChanged;
        Refresh();
    }

    public AppEnvironment Environment { get; }

    public ObservableCollection<BackupRowViewModel> Rows { get; } = [];

    public bool HasBackups => Rows.Count > 0;

    public string TotalText => UiText.BackupsTotal(Rows.Count(r => r.Entry.Backup is not null), Rows.Count(r => r.Entry.Leftover is not null), Rows.Sum(r => r.Entry.SizeBytes));

    public int SelectedCount => Rows.Count(r => r.IsChecked);

    public bool HasSelection => SelectedCount > 0;

    public string DeleteSelectedText => UiText.DeleteSelectedBackups(SelectedCount, Rows.Where(r => r.IsChecked).Sum(r => r.Entry.SizeBytes));

    public override string Title => UiText.StorageTitle;

    /// <summary>Reads the list again, keeping what was checked where it is still there.</summary>
    public void Refresh()
    {
        var checkedKeys = Rows.Where(r => r.IsChecked).Select(r => r.Entry.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Rows.Clear();
        var backups = _files.ListBackups();
        var newestBeforeRestore = backups.Where(b => b.Reason == BackupReason.BeforeRestore).MaxBy(b => b.CreatedAt);
        foreach (var backup in backups)
        {
            Add(new BackupEntry(backup, null, ReferenceEquals(backup, newestBeforeRestore)));
        }

        foreach (var leftover in _files.ListLeftovers())
        {
            Add(new BackupEntry(null, leftover));
        }

        OnPropertyChanged(nameof(HasBackups));
        OnPropertyChanged(nameof(TotalText));
        SelectionChanged();

        void Add(BackupEntry entry)
        {
            var row = new BackupRowViewModel(entry, Environment.SchemaVersion) { IsChecked = checkedKeys.Contains(entry.Key) };
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenFolder(Environment.DataDirectory);

    /// <summary>D2: the log is what a report of a problem needs first; the folder is made if nothing was logged yet.</summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(Environment.LogDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DesktopLog.Swallowed(ex); // The system's own message about a missing folder says enough.
        }

        _shell.OpenFolder(Environment.LogDirectory);
    }

    [RelayCommand]
    private void Restore(BackupRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.CanRestore && row.Entry.Backup is { } backup)
        {
            _prompts.Restore(backup);
        }
    }

    [RelayCommand]
    private void Delete(BackupRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _prompts.Delete([row.Entry]);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelected() => _prompts.Delete([.. Rows.Where(r => r.IsChecked).Select(r => r.Entry)]);

    public void Dispose() => _prompts.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BackupRowViewModel.IsChecked))
        {
            SelectionChanged();
        }
    }

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(DeleteSelectedText));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One backup or leftover in the storage page's list.</summary>
public sealed partial class BackupRowViewModel(BackupEntry entry, int schemaVersion) : ObservableObject
{
    public BackupEntry Entry { get; } = entry;

    public string TimeText => UiText.FormatTime(Entry.At);

    public bool IsBackup => Entry.Backup is not null;

    /// <summary>"结构版本 8"; null for a leftover, whose version is not in its name.</summary>
    public string? VersionText => Entry.Backup is { } backup ? UiText.SchemaVersionBadge(backup.SchemaVersion) : null;

    /// <summary>"删除工作区前", "删除会话前", "恢复前", or "恢复时替换下的旧数据"; null for an upgrade backup.</summary>
    public string? Badge => Entry.Backup is { } backup ? UiText.BackupReasonLabel(backup.Reason) : UiText.LeftoverDatabase;

    public string SizeText => UiText.FormatSize(Entry.SizeBytes);

    /// <summary>A backup this build can open; one written by a newer build is listed but not restorable.</summary>
    public bool CanRestore => Entry.Backup is { } backup && backup.SchemaVersion <= schemaVersion;

    /// <summary>Written by a newer build: its version badge is marked and 恢复… says why it is off.</summary>
    public bool IsTooNew => Entry.Backup is { } backup && backup.SchemaVersion > schemaVersion;

    /// <summary>Made before a restore: its badge stands out, as the way back to that data.</summary>
    public bool IsBeforeRestore => Entry.Backup?.Reason == BackupReason.BeforeRestore;

    /// <summary>Why 恢复… is disabled (its tooltip), or what a leftover is.</summary>
    public string? Hint => Entry.Backup is { } backup
        ? backup.SchemaVersion > schemaVersion ? UiText.BackupTooNew(backup.SchemaVersion, schemaVersion) : null
        : UiText.LeftoverHint;

    /// <summary>The row as a screen reader names it, inside each of its controls' names.</summary>
    public string RowName => Entry.Backup is { } b
        ? UiText.BackupRowName(b.CreatedAt, Badge, b.SizeBytes)
        : UiText.LeftoverRowName(Entry.At, Entry.SizeBytes);

    public string SelectName => UiText.SelectBackupName(RowName);

    public string RestoreName => UiText.RestoreBackupName(RowName);

    public string DeleteName => UiText.DeleteBackupName(RowName);

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}
