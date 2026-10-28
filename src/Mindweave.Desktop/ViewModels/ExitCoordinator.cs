using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
/// </summary>
public sealed partial class ExitCoordinator(
    GenerationCoordinator generation,
    LayoutPromptViewModel layout,
    Workbench workbench,
    ReadingStateService reading,
    IDatabaseLifecycle database,
    ISystemShell shell,
    ExitOptions options) : ViewModelBase
{
    private IReadOnlyList<NetworkSession> _discardOnCommit = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTaskPromptOpen), nameof(IsBusy), nameof(IsFailed))]
    [NotifyCanExecuteChangedFor(nameof(StopAndExitCommand), nameof(KeepRunningCommand), nameof(RetryCommand), nameof(ReturnCommand))]
    public partial ExitStage Stage { get; private set; }

    [ObservableProperty]
    public partial string TaskMessage { get; private set; } = string.Empty;

    public bool IsTaskPromptOpen => Stage == ExitStage.TaskPrompt;

    /// <summary>Stopping or saving: the overlay shows progress and offers nothing to press.</summary>
    public bool IsBusy => Stage is ExitStage.Stopping or ExitStage.Flushing;

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

    private void Begin()
    {
        if (layout.HoldExit(OnLayoutAnswered, () => Stage = ExitStage.Idle))
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
            TaskMessage = UiText.ExitWithUnfinished(work.Answers, work.Summaries);
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
        Stage = ExitStage.Idle;
    }

    [RelayCommand(CanExecute = nameof(IsFailed))]
    private void Retry() => Running = SaveAndReleaseAsync();

    /// <summary>Back to work after a failed save: new work is accepted, what the exit stopped stays stopped.</summary>
    [RelayCommand(CanExecute = nameof(IsFailed))]
    private void Return()
    {
        generation.Reopen();
        Stage = ExitStage.Idle;
    }

    private async Task CommitAsync()
    {
        Stage = ExitStage.Stopping;

        // The first irreversible steps: every prompt is answered.
        foreach (var session in _discardOnCommit)
        {
            session.DiscardChanges();
        }

        _discardOnCommit = [];
        await generation.StopForExitAsync(options.StopWait);
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
        catch (Exception)
#pragma warning restore CA1031
        {
            saved = false;
        }

        if (!saved)
        {
            Stage = ExitStage.Failed;
            return;
        }

        Stage = ExitStage.Releasing;
        shell.ExitApplication();
    }

    /// <summary>
    /// Step 4: open pages' drafts (including any still inside a throttle), message drafts and reading positions, the
    /// stopped answers' terminal writes that failed, the last location, then the write queue and a best-effort
    /// checkpoint. True when nothing the user wrote or received is left unsaved; page state alone may degrade.
    /// </summary>
    private async Task<bool> SaveEverythingAsync()
    {
        var pages = await workbench.FlushAsync();
        var drafts = await reading.FlushAsync();
        var answers = await generation.SaveUnsavedAsync();
        await database.DrainAsync();
        return pages && drafts && answers;
    }
}
