using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Desktop.Navigation;

namespace Mindweave.Desktop.ViewModels;

/// <summary>The window's root content; the root navigator swaps it between startup, recovery, onboarding and workbench.</summary>
public sealed partial class RootViewModel(ExitPromptViewModel exitPrompt, LayoutPromptViewModel layoutPrompt) : ViewModelBase, IRootNavigator
{
    /// <summary>Overlay shown above any root page when quitting would stop running answers.</summary>
    public ExitPromptViewModel ExitPrompt { get; } = exitPrompt;

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
