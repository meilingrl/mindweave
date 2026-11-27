using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Conversations;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels;

public sealed class ExitOptions
{
    /// <summary>How long stopped tasks get to acknowledge before the exit moves on (their checkpoints stand).</summary>
    public TimeSpan StopWait { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Where an exit is: the prompts come first, everything irreversible after them.</summary>
public enum ExitStage
{
    Idle,
    TaskPrompt,
    Stopping,
    Flushing,
    Failed,

    /// <summary>A restore (2026-11-12): everything is saved; the current data is being backed up and the backup put in place.</summary>
    Restoring,
    Releasing,
}

/// <summary>
/// The one exit sequence (architecture §12.3, requirement §9.2–9.3) behind every way of quitting: closing the window
/// and the application lifetime's shutdown request (⌘Q, the dock) both cancel and call <see cref="InterceptClose"/>.
/// The cancelable prompt — unfinished user answers and summaries — comes before the first irreversible step. Stopping
/// waits a bounded time; then
/// drafts, reading state, failed terminal writes and the write queue are saved while the window is still open. A
/// failed save keeps the window with "retry" and "return"; returning accepts new work only. When everything is saved
/// the coordinator asks for a programmatic shutdown, which the hooks let through.
/// <para>
/// Restoring a backup in the running application (2026-11-12) goes through the same prompts and steps
/// (<see cref="BeginRestore"/>): instead of the shutdown, the restore's own step runs once everything is saved; when it
/// does not go ahead, new work is accepted again and nothing else changes.
/// </para>
/// </summary>
public sealed partial class ExitCoordinator(
    IGenerationExit generation,
    Workbench workbench,
    Application.Summaries.ISummaries summaries,
    ISaveBeforeClose save,
    ISystemShell shell,
    ExitOptions options,
    ILogger<ExitCoordinator> logger) : ViewModelBase
{
    private bool _stopped;
    private Func<Task<bool>>? _restore;
    private Action? _restoreEnded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTaskPromptOpen), nameof(IsBusy), nameof(IsFailed), nameof(BusyText))]
    [NotifyCanExecuteChangedFor(nameof(StopAndExitCommand), nameof(KeepRunningCommand), nameof(RetryCommand), nameof(ReturnCommand))]
    public partial ExitStage Stage { get; private set; }

    [ObservableProperty]
    public partial string TaskMessage { get; private set; } = string.Empty;

    public bool IsTaskPromptOpen => Stage == ExitStage.TaskPrompt;

    /// <summary>The sequence leads to a restore rather than to quitting; the prompts say so.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskTitle), nameof(TaskConfirm), nameof(TaskCancel), nameof(BusyText), nameof(FailedBody), nameof(FailedRetry))]
    public partial bool IsForRestore { get; private set; }

    public string TaskTitle => IsForRestore ? UiText.RestoreTaskTitle : UiText.ExitTitle;

    public string TaskConfirm => IsForRestore ? UiText.RestoreTaskConfirm : UiText.ExitConfirm;

    public string TaskCancel => IsForRestore ? UiText.Cancel : UiText.ExitCancel;

    public string BusyText => Stage == ExitStage.Restoring ? UiText.RestoreRunning : IsForRestore ? UiText.RestoreSaving : UiText.ExitSaving;

    public string FailedBody => (IsForRestore ? UiText.RestoreSaveFailedBody : UiText.ExitSaveFailedBody) + UiText.UnsaveableSummaryEdits(UnsaveableSummaries);

    /// <summary>Summaries holding an edit that can never be saved as it is; named so the user knows where to discard it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FailedBody))]
    public partial IReadOnlyList<string> UnsaveableSummaries { get; private set; } = [];

    // The names belong to the failure they explain (review 2026-11-15): leaving it (retry, return) clears them, so a
    // later failure at another step never shows an old list.
    partial void OnStageChanged(ExitStage value)
    {
        if (value != ExitStage.Failed)
        {
            UnsaveableSummaries = [];
        }
    }

    public string FailedRetry => IsForRestore ? UiText.RestoreRetry : UiText.ExitRetry;

    /// <summary>Stopping, saving or restoring: the overlay shows progress and offers nothing to press.</summary>
    public bool IsBusy => Stage is ExitStage.Stopping or ExitStage.Flushing or ExitStage.Restoring;

    public bool IsFailed => Stage == ExitStage.Failed;

    /// <summary>The step that is running after a confirmation or retry; tests await it, the UI never does.</summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Called by both close hooks. True means "cancel this close": the exit sequence starts (or is already under way)
    /// and will ask for a programmatic shutdown itself. Only that final shutdown is let through.
    /// </summary>
    public bool InterceptClose()
    {
        if (Stage == ExitStage.Releasing)
        {
            return false;
        }

        if (Stage == ExitStage.Idle)
        {
            Begin();
        }

        return true;
    }

    /// <summary>
    /// The system logs out, restarts or shuts down (health check 2026-11-12, gap-lifecycle-2). Vetoing it is right only
    /// when something must be asked: an answer or summary still running; then the usual sequence
    /// starts and null is returned (the caller cancels the request). Otherwise everything is saved now and the returned
    /// task completes when it is; the caller waits for it, within a limit, and lets the system go ahead.
    /// </summary>
    public Task? EndForSystem()
    {
        if (Stage == ExitStage.Releasing)
        {
            return Task.CompletedTask;
        }

        if (Stage != ExitStage.Idle || generation.UnfinishedUserWork.Any)
        {
            InterceptClose();
            return null;
        }

        Stage = ExitStage.Flushing;
        return SaveForSystemAsync();
    }

    /// <summary>
    /// The system's wait for <see cref="EndForSystem"/> ran out (review 2026-11-13): the request was vetoed after all, so
    /// the application quits on its own once the save ends instead of staying open with new work refused nowhere.
    /// </summary>
    public void SystemSaveTimedOut()
    {
        _quitWhenSaved = true;
        if (Stage == ExitStage.Releasing)
        {
            shell.ExitApplication();
        }
    }

    private bool _quitWhenSaved;

    private async Task SaveForSystemAsync()
    {
        try
        {
            // Background work (titles, names) stops first, so nothing writes during or after the save; no answer or
            // summary is running, or the sequence would have asked.
            await generation.StopForExitAsync(TimeSpan.FromSeconds(1));
            await SaveEverythingAsync();
        }
#pragma warning disable CA1031 // The system is ending the session either way; what could not be saved is logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStepFailed(logger, ex, ExitStage.Flushing);
        }
        finally
        {
            Stage = ExitStage.Releasing;
            if (_quitWhenSaved)
            {
                shell.ExitApplication();
            }
        }
    }

    /// <summary>
    /// Starts the sequence for a restore: the running-task prompt as on exit (cancelling it ends it with nothing
    /// changed), then stopping and saving; then <paramref name="restore"/> runs. It returns false when it
    /// did not go ahead (the backup of the current data failed): new work is accepted again. <paramref name="ended"/> runs
    /// whenever the sequence ends without the restore having gone ahead. False when an exit or restore is already under way.
    /// </summary>
    public bool BeginRestore(Func<Task<bool>> restore, Action ended)
    {
        ArgumentNullException.ThrowIfNull(restore);
        ArgumentNullException.ThrowIfNull(ended);
        if (Stage != ExitStage.Idle)
        {
            return false;
        }

        _restore = restore;
        _restoreEnded = ended;
        IsForRestore = true;
        Begin();
        return true;
    }

    /// <summary>Back to idle with nothing irreversible done (a prompt cancelled, or "return" after a failed save).</summary>
    private void BackToIdle()
    {
        Stage = ExitStage.Idle;
        var ended = _restoreEnded;
        _restore = null;
        _restoreEnded = null;
        IsForRestore = false;
        ended?.Invoke();
    }

    private void Begin()
    {
        var work = generation.UnfinishedUserWork;
        if (work.Any)
        {
            TaskMessage = IsForRestore ? UiText.RestoreWithUnfinished(work.Answers, work.Summaries) : UiText.ExitWithUnfinished(work.Answers, work.Summaries);
            Stage = ExitStage.TaskPrompt;
        }
        else
        {
            Running = StartStopping();
        }
    }

    [RelayCommand(CanExecute = nameof(IsTaskPromptOpen))]
    private void StopAndExit() => Running = StartStopping();

    /// <summary>Cancel at the task prompt: nothing has happened yet.</summary>
    [RelayCommand(CanExecute = nameof(IsTaskPromptOpen))]
    private void KeepRunning() => BackToIdle();

    /// <summary>Retry after a failure: the stop step again if it did not finish, then saving.</summary>
    [RelayCommand(CanExecute = nameof(IsFailed))]
    private void Retry() => Running = _stopped ? SaveAndReleaseAsync() : StopAndReleaseAsync();

    /// <summary>Back to work after a failed save: new work is accepted, what the exit stopped stays stopped.</summary>
    [RelayCommand(CanExecute = nameof(IsFailed))]
    private void Return()
    {
        generation.Reopen();
        BackToIdle();
    }

    /// <summary>Every prompt is answered: the first irreversible step.</summary>
    private Task StartStopping()
    {
        _stopped = false;
        return StopAndReleaseAsync();
    }

    /// <summary>
    /// Runs unobserved (the UI never awaits it), so nothing may escape: a step that throws ends in the failure state with
    /// retry and return, never in "stopping" with no way out.
    /// </summary>
    private async Task StopAndReleaseAsync()
    {
        Stage = ExitStage.Stopping;
        try
        {
            await generation.StopForExitAsync(options.StopWait);
            _stopped = true;
        }
#pragma warning disable CA1031 // Any failure while stopping means: keep the window, offer retry or return.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStepFailed(logger, ex, ExitStage.Stopping);
            Stage = ExitStage.Failed;
            return;
        }

        await SaveAndReleaseAsync();
    }

    private async Task SaveAndReleaseAsync()
    {
        Stage = ExitStage.Flushing;
        bool saved;
        try
        {
            saved = await SaveEverythingAsync();
        }
#pragma warning disable CA1031 // Any failure while saving means: keep the window, offer retry or return.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStepFailed(logger, ex, ExitStage.Flushing);
            saved = false;
        }

        if (!saved)
        {
            UnsaveableSummaries = await UnsaveableSummariesAsync();
            Stage = ExitStage.Failed;
            return;
        }

        if (_restore is { } restore)
        {
            await RestoreAsync(restore);
            return;
        }

        Stage = ExitStage.Releasing;
        shell.ExitApplication();
    }

    /// <summary>
    /// Everything is saved and nothing new can start: the restore's own step. When it does not go ahead, new work is
    /// accepted again, as after "return"; when it does, this session is over (the next one has its own coordinator).
    /// </summary>
    private async Task RestoreAsync(Func<Task<bool>> restore)
    {
        Stage = ExitStage.Restoring;
        bool restored;
        try
        {
            restored = await restore();
        }
#pragma warning disable CA1031 // Anything failing before the swap leaves the data as it was: back to work.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStepFailed(logger, ex, ExitStage.Restoring);
            restored = false;
        }

        if (!restored)
        {
            generation.Reopen();
            BackToIdle();
            return;
        }

        _restore = null;
        _restoreEnded = null;
        Stage = ExitStage.Releasing;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Exit step {Stage} failed; the window stays open with retry and return")]
    private static partial void LogStepFailed(ILogger logger, Exception exception, ExitStage stage);

    /// <summary>Best effort: a failed read only leaves the names out of the message.</summary>
    private async Task<IReadOnlyList<string>> UnsaveableSummariesAsync()
    {
        try
        {
            return await summaries.UnsaveableDraftsAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // The message stays generic; retry and return still work.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStepFailed(logger, ex, ExitStage.Failed);
            return [];
        }
    }

    /// <summary>
    /// Step 4: this view's last location, then everything on the one list of what is saved before the data is closed
    /// (<see cref="ISaveBeforeClose"/>), which the end of the session runs again. True when nothing the user wrote or
    /// received is left unsaved; page state alone may degrade.
    /// </summary>
    private async Task<bool> SaveEverythingAsync()
    {
        await workbench.FlushAsync();
        return await save.SaveAsync();
    }
}
