using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Core.Workspaces;

namespace Mindweave.Desktop.ViewModels;

/// <summary>Validated workspace-name entry, shared by first-run onboarding and the sidebar's "new workspace".</summary>
public sealed partial class WorkspaceNameForm : ViewModelBase
{
    private readonly Func<WorkspaceName, Task> _submit;

    public WorkspaceNameForm(Func<WorkspaceName, Task> submit) => _submit = submit;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        if (!WorkspaceName.TryCreate(Name, out var name, out var error))
        {
            Error = UiText.WorkspaceNameError(error!.Value);
            return;
        }

        Error = null;
        try
        {
            await _submit(name!);
            Name = string.Empty;
        }
#pragma warning disable CA1031 // Saving failed: keep the typed name and show why; nothing is lost.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = UiText.SaveFailed(ex.Message);
        }
    }

    private bool CanSubmit() => !string.IsNullOrWhiteSpace(Name);

    /// <summary>Back to empty, as when the form is cancelled (the sidebar's and the workspaces page's forms).</summary>
    public void Reset()
    {
        Name = string.Empty;
        Error = null;
    }
}

/// <summary>First run: no workspace exists yet, so ask for one before opening the workbench.</summary>
public sealed class OnboardingViewModel(Func<WorkspaceName, Task> create) : ViewModelBase
{
    public WorkspaceNameForm Form { get; } = new(create);
}
