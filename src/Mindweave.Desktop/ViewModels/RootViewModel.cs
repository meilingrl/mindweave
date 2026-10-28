using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Desktop.Navigation;

namespace Mindweave.Desktop.ViewModels;

/// <summary>The window's root content; the root navigator swaps it between startup, recovery, onboarding and workbench.</summary>
public sealed partial class RootViewModel(ExitCoordinator exit, LayoutPromptViewModel layoutPrompt) : ViewModelBase, IRootNavigator
{
    /// <summary>The exit sequence every way of quitting goes through; its task prompt and save failure overlay any root page.</summary>
    public ExitCoordinator Exit { get; } = exit;

    /// <summary>Overlay shown when leaving a workspace or quitting with an unsaved network layout.</summary>
    public LayoutPromptViewModel LayoutPrompt { get; } = layoutPrompt;

    [ObservableProperty]
    public partial ViewModelBase? Current { get; private set; }

    public void Show(ViewModelBase page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Current = page;
    }
}
