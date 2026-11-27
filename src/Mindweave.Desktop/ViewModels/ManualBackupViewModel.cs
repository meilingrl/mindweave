using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Export;
using Mindweave.Application.ManualBackup;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels;

/// <summary>A workspace in the dialog: its name, how much it holds, and a tick.</summary>
public sealed partial class ManualBackupWorkspaceViewModel(ManualBackupViewModel owner, ManualBackupWorkspace workspace) : ViewModelBase
{
    public WorkspaceId Id { get; } = workspace.Id;

    public string Name { get; } = workspace.Name;

    public string Details { get; } = UiText.ManualBackupConversations(workspace.Conversations) + " · " + UiText.FormatSize(workspace.Bytes);

    public long Bytes { get; } = workspace.Bytes;

    /// <summary>All ticked when the dialog opens (docs/design/sharing-backup.md V1).</summary>
    [ObservableProperty]
    public partial bool IsTicked { get; set; } = true;

    partial void OnIsTickedChanged(bool value) => owner.TicksChanged();
}

/// <summary>
/// The manual backup's dialog (docs/design/sharing-backup.md §5): the workspaces with ticks, all ticked, the size, what the
/// file is for, and Save through the app-modal file panel (D-040). While it saves it shows how far it has got, and Cancel
/// stops it; a cancelled or failed save leaves no file.
/// </summary>
public sealed partial class ManualBackupViewModel(IManualBackups backups, ISystemShell shell, TimeProvider time) : ViewModelBase
{
    private CancellationTokenSource? _saving;
    private int _opened;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ManualBackupWorkspaceViewModel> Workspaces { get; private set; } = [];

    /// <summary>The ticked workspaces' size, an estimate; empty while they are read.</summary>
    [ObservableProperty]
    public partial string Size { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsReading { get; private set; }

    /// <summary>Saving: the list is fixed, Cancel stops it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(CloseCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>How far the save has got.</summary>
    [ObservableProperty]
    public partial string? Progress { get; private set; }

    /// <summary>Saved, cancelled, or why it was not saved.</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    [ObservableProperty]
    public partial bool NoticeIsError { get; private set; }

    /// <summary>The last read of the workspaces; tests await it.</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>The last save; tests await it.</summary>
    internal Task Saving { get; private set; } = Task.CompletedTask;

    public void Open()
    {
        if (IsBusy)
        {
            return;
        }

        _opened++;
        Workspaces = [];
        Notice = null;
        Progress = null;
        IsOpen = true;
        Loading = LoadAsync(_opened);
    }

    internal void TicksChanged()
    {
        var ticked = Workspaces.Where(w => w.IsTicked).ToList();
        Size = Workspaces.Count == 0 ? string.Empty : UiText.ManualBackupSize(ticked.Sum(w => w.Bytes));
        if (ticked.Count == 0 && Workspaces.Count > 0)
        {
            Say(UiText.ManualBackupNothingTicked, error: false);
        }
        else if (Notice == UiText.ManualBackupNothingTicked)
        {
            Notice = null;
        }

        SaveCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadAsync(int opened)
    {
        IsReading = true;
        try
        {
            var listed = await backups.ListAsync(CancellationToken.None);
            if (opened == _opened && IsOpen)
            {
                Workspaces = [.. listed.Select(w => new ManualBackupWorkspaceViewModel(this, w))];
                TicksChanged();
            }
        }
#pragma warning disable CA1031 // Unreadable workspaces are said; closing still works.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            Say(UiText.ManualBackupReadFailed, error: true);
        }
        finally
        {
            IsReading = false;
        }
    }

    private bool CanSave() => !IsBusy && !IsReading && Workspaces.Any(w => w.IsTicked);

    /// <summary>Asks where, with "Mindweave" and the date as the name, then writes the file.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsBusy = true;
        Notice = null;
        using var saving = new CancellationTokenSource();
        _saving = saving;
        try
        {
            var day = DateOnly.FromDateTime(time.GetLocalNow().DateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (await shell.PickBackupFileAsync("Mindweave " + day, CancellationToken.None) is not { } destination)
            {
                return;
            }

            var chosen = Workspaces.Where(w => w.IsTicked).Select(w => w.Id).ToList();
            Progress = UiText.ManualBackupProgress(0, chosen.Count);
            var progress = new Progress<ManualBackupProgress>(p =>
            {
                if (IsBusy)
                {
                    Progress = UiText.ManualBackupProgress(p.Done, p.Total);
                }
            });
            var task = backups.SaveAsync(chosen, destination, progress, saving.Token);
            Saving = task;
            var saved = await task;
            Say(UiText.ManualBackupSaved(destination.DisplayName) + (saved.MissingOriginals > 0 ? UiText.ManualBackupMissingOriginals(saved.MissingOriginals) : string.Empty), error: false);
        }
        catch (OperationCanceledException)
        {
            Say(UiText.ManualBackupCancelled, error: false);
        }
        catch (ExportWriteException ex)
        {
            Say(UiText.ExportFailed(ex.Failure, ex.Detail), error: true);
        }
#pragma warning disable CA1031 // Anything else (a failing save panel, a store already closed by exit) is reported, never thrown at the UI thread.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(ex);
            Say(UiText.ExportFailedUnexpectedly, error: true);
        }
        finally
        {
            _saving = null;
            Progress = null;
            IsBusy = false;
        }
    }

    private bool CanCancel() => IsBusy;

    /// <summary>Stops the save; the file is not written.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _saving?.Cancel();

    private bool CanClose() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close()
    {
        _opened++;
        IsOpen = false;
        Workspaces = [];
    }

    private void Say(string text, bool error)
    {
        NoticeIsError = error;
        Notice = text;
    }
}
