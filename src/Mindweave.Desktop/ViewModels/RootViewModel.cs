using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Desktop.Navigation;

namespace Mindweave.Desktop.ViewModels;

/// <summary>The window's root content; the root navigator swaps it between startup, recovery, onboarding and workbench.</summary>
public sealed partial class RootViewModel(ExitCoordinator exit, LayoutPromptViewModel layoutPrompt, WorkspaceDeletionViewModel workspaceDeletion, ConversationDeletionViewModel conversationDeletion) : ViewModelBase, IRootNavigator
{
    /// <summary>The exit sequence every way of quitting goes through; its task prompt and save failure overlay any root page.</summary>
    public ExitCoordinator Exit { get; } = exit;

    /// <summary>Overlay shown when leaving a workspace or quitting with an unsaved network layout.</summary>
    public LayoutPromptViewModel LayoutPrompt { get; } = layoutPrompt;

    /// <summary>The delete-a-workspace dialog over the workbench (S7 round 4).</summary>
    public WorkspaceDeletionViewModel WorkspaceDeletion { get; } = workspaceDeletion;

    /// <summary>Deleting conversations with their branches and summaries, or a sidebar selection (requirements v0.12 §8.2).</summary>
    public ConversationDeletionViewModel ConversationDeletion { get; } = conversationDeletion;

    [ObservableProperty]
    public partial ViewModelBase? Current { get; private set; }

    public void Show(ViewModelBase page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Current = page;
    }
}
