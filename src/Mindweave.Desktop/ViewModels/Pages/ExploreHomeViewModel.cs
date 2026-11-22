using CommunityToolkit.Mvvm.Input;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>Workspace entry page: start a conversation or pick one from the sidebar. Its title is the workspace's name.</summary>
public sealed partial class ExploreHomeViewModel(Workspace workspace, IContentNavigator content) : PageViewModel(ExploreHomeRoute.Instance), IShowsWorkspaceName
{
    public Workspace Workspace { get; private set; } = workspace;

    public override string Title => Workspace.Name.Value;

    public void WorkspaceRenamed(Workspace renamed)
    {
        ArgumentNullException.ThrowIfNull(renamed);
        Workspace = renamed;
        OnPropertyChanged(nameof(Workspace));
        OnPropertyChanged(nameof(Title));
    }

    [RelayCommand]
    private void NewConversation() => content.Push(NewConversationRoute.Instance);
}
