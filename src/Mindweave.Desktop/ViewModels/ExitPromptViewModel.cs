using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Conversations;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels;

/// <summary>
/// Exit sequence (architecture §12.3): the cancelable prompt comes first; only after confirmation are tasks
/// stopped, received text saved, and the application closed. Cancel leaves everything running.
/// </summary>
public sealed partial class ExitPromptViewModel(GenerationCoordinator generation, ISystemShell shell) : ViewModelBase
{
    private bool _confirmed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message))]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand), nameof(CancelCommand))]
    public partial bool IsStopping { get; private set; }

    public string Message => UiText.ExitWithRunningAnswers(generation.UnfinishedAnswerCount);

    /// <summary>Called when the window or app is asked to close. Returns true when closing must wait for the user.</summary>
    public bool ShouldHoldClose()
    {
        if (_confirmed)
        {
            return false;
        }

        if (generation.UnfinishedAnswerCount == 0)
        {
            return false;
        }

        IsOpen = true;
        OnPropertyChanged(nameof(Message));
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ConfirmAsync()
    {
        IsStopping = true;
        await generation.ShutdownAsync();
        _confirmed = true;
        IsOpen = false;
        shell.ExitApplication();
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Cancel() => IsOpen = false;

    private bool CanAct() => !IsStopping;
}
