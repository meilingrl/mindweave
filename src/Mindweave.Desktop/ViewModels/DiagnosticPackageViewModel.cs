using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Diagnostics;
using Mindweave.Application.Export;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels;

/// <summary>
/// The diagnostic package's dialog (D2 §2.2): what will be in it, with ticks for model names and hosts and for the logs,
/// both on; the one line on what the log can hold; Save through the app-modal file panel. From the recovery page it lists
/// the summary and the logs only. Nothing is written until Save, and a failed save leaves no file.
/// </summary>
public sealed partial class DiagnosticPackageViewModel(IDiagnosticPackage package, ISystemShell shell, TimeProvider time) : ViewModelBase
{
    private int _opened;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>False from the recovery page: the data could not be opened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WithoutData))]
    public partial bool DataAvailable { get; private set; } = true;

    public bool WithoutData => !DataAvailable;

    [ObservableProperty]
    public partial string? RequestsLine { get; private set; }

    [ObservableProperty]
    public partial string? LogsLine { get; private set; }

    [ObservableProperty]
    public partial bool ModelsAndHosts { get; set; } = true;

    [ObservableProperty]
    public partial bool Logs { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsReading { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(CloseCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? Notice { get; private set; }

    [ObservableProperty]
    public partial bool NoticeIsError { get; private set; }

    /// <summary>The last read of what will leave; tests await it.</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>The last save; tests await it.</summary>
    internal Task Saving { get; private set; } = Task.CompletedTask;

    /// <param name="dataAvailable">False from the recovery page, where the package holds the summary and the logs.</param>
    public void Open(bool dataAvailable)
    {
        if (IsBusy)
        {
            return;
        }

        _opened++;
        DataAvailable = dataAvailable;
        ModelsAndHosts = true;
        Logs = true;
        RequestsLine = null;
        LogsLine = null;
        Notice = null;
        IsOpen = true;
        Loading = LoadAsync(_opened);
    }

    private async Task LoadAsync(int opened)
    {
        IsReading = true;
        try
        {
            var preview = await package.PreviewAsync(DataAvailable, CancellationToken.None);
            if (opened == _opened && IsOpen)
            {
                RequestsLine = DataAvailable ? UiText.DiagnosticRequestsFile(preview.Requests) : null;
                LogsLine = UiText.DiagnosticLogs(preview.LogFiles, preview.LogBytes);
            }
        }
#pragma warning disable CA1031 // The preview is a convenience: saving still builds the package from what can be read.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            RequestsLine = null;
            LogsLine = UiText.DiagnosticLogs(0, 0);
        }
        finally
        {
            IsReading = false;
        }
    }

    private bool CanSave() => !IsBusy && !IsReading;

    /// <summary>Asks where, with the product and the date as the name, then writes the zip.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsBusy = true;
        Notice = null;
        try
        {
            var day = DateOnly.FromDateTime(time.GetLocalNow().DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (await shell.PickDiagnosticFileAsync("mindweave-diagnostics-" + day, CancellationToken.None) is not { } destination)
            {
                return;
            }

            var task = package.SaveAsync(destination, new DiagnosticChoice(ModelsAndHosts, Logs), DataAvailable, CancellationToken.None);
            Saving = task;
            await task;
            Say(UiText.DiagnosticSaved(destination.DisplayName), error: false);
        }
        catch (ExportWriteException ex)
        {
            Say(UiText.ExportFailed(ex.Failure, ex.Detail), error: true);
        }
#pragma warning disable CA1031 // Anything else (a failing save panel) is reported, never thrown at the UI thread.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(ex);
            Say(UiText.ExportFailedUnexpectedly, error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanClose() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close()
    {
        _opened++;
        IsOpen = false;
    }

    private void Say(string text, bool error)
    {
        NoticeIsError = error;
        Notice = text;
    }
}
