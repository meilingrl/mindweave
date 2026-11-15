using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Conversations;
using Mindweave.Application.Reading;
using Mindweave.Application.Startup;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Network;
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
    LayoutPrompt,
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
/// The cancelable prompts — unsaved layout, then unfinished user answers and summaries — all come before the first
/// irreversible step; "don't save" for the layout is only recorded until then. Stopping waits a bounded time; then
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
    LayoutPromptViewModel layout,
    Workbench workbench,
    IReadingState reading,
    Application.Summaries.ISummaries summaries,
    IDatabaseLifecycle database,
    Application.Export.ISummaryExport exports,
    ISystemShell shell,
    ExitOptions options,
    ILogger<ExitCoordinator> logger) : ViewModelBase
{
    private IReadOnlyList<NetworkSession> _discardOnCommit = [];
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
    /// when something must be asked: an answer or summary still running, or a layout not saved; then the usual sequence
    /// starts and null is returned (the caller cancels the request). Otherwise everything is saved now and the returned
    /// task completes when it is; the caller waits for it, within a limit, and lets the system go ahead.
    /// </summary>
    public Task? EndForSystem()
    {
        if (Stage == ExitStage.Releasing)
        {
            return Task.CompletedTask;
        }

        if (Stage != ExitStage.Idle || layout.HasUnsavedLayout || generation.UnfinishedUserWork.Any)
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
    /// Starts the sequence for a restore: the unsaved-layout and running-task prompts as on exit (cancelling either ends
    /// it with nothing changed), then stopping and saving; then <paramref name="restore"/> runs. It returns false when it
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
        if (layout.HoldExit(OnLayoutAnswered, BackToIdle))
        {
            Stage = ExitStage.LayoutPrompt;
        }
        else
        {
            AskAboutTasks();
        }
    }

    private void OnLayoutAnswered(IReadOnlyList<NetworkSession> discardOnCommit)
    {
        _discardOnCommit = discardOnCommit;
        AskAboutTasks();
    }

    private void AskAboutTasks()
    {
        var work = generation.UnfinishedUserWork;
        if (work.Any)
        {
            TaskMessage = IsForRestore ? UiText.RestoreWithUnfinished(work.Answers, work.Summaries) : UiText.ExitWithUnfinished(work.Answers, work.Summaries);
            Stage = ExitStage.TaskPrompt;
        }
        else
        {
            Running = CommitAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(IsTaskPromptOpen))]
    private void StopAndExit() => Running = CommitAsync();

    /// <summary>Cancel at the task prompt: nothing has happened yet, so an unsaved layout stays unsaved.</summary>
    [RelayCommand(CanExecute = nameof(IsTaskPromptOpen))]
    private void KeepRunning()
    {
        _discardOnCommit = [];
        BackToIdle();
    }

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

    /// <summary>
    /// Runs unobserved (the UI never awaits it), so nothing may escape: a step that throws ends in the failure state with
    /// retry and return, never in "stopping" with no way out.
    /// </summary>
    private async Task CommitAsync()
    {
        Stage = ExitStage.Stopping;
        _stopped = false;
        var discard = _discardOnCommit;
        _discardOnCommit = [];
        try
        {
            // The first irreversible steps: every prompt is answered.
            foreach (var session in discard)
            {
                session.DiscardChanges();
            }
        }
#pragma warning disable CA1031 // A layout the user chose not to save is lost either way; carry on with the exit.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStepFailed(logger, ex, ExitStage.Stopping);
        }

        await StopAndReleaseAsync();
    }

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
    /// Step 4: an export still writing its file (so it is not cut off and leaves no temporary file), summary edit drafts
    /// (including any still inside their autosave delay), message drafts and reading positions, the stopped answers' terminal
    /// writes that failed, the last location, then the write queue and a best-effort checkpoint. True when nothing the
    /// user wrote or received is left unsaved; page state alone may degrade.
    /// </summary>
    private async Task<bool> SaveEverythingAsync()
    {
        await exports.WhenIdleAsync();
        await workbench.FlushAsync();
        var summaryDrafts = await summaries.FlushDraftsAsync();
        var drafts = await reading.FlushAsync();
        var answers = await generation.SaveUnsavedAsync();
        await database.DrainAsync();
        return summaryDrafts && drafts && answers;
    }
}
