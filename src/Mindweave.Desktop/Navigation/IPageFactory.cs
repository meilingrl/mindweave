using System.Collections.ObjectModel;
using Mindweave.Core.Reading;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Navigation;

/// <summary>
/// Where a page lives: its workspace, the viewer slot showing it, and the navigator it may use. <paramref name="Workspaces"/>
/// is the workbench's list of workspaces, for the pages that show or change it (设置 › 工作区).
/// </summary>
public sealed record PageContext(Workspace Workspace, ViewerSlotKey Slot, IWorkbenchNavigator Navigator, IWorkspaceDirectory? Workspaces = null);

public interface IPageFactory
{
    PageViewModel Create(ContentRoute route, PageContext context);
}

/// <summary>The workspaces the workbench lists, as pages see them (2026-11-10).</summary>
public interface IWorkspaceDirectory
{
    /// <summary>Every workspace, oldest first, as the switcher lists them.</summary>
    ObservableCollection<Workspace> Workspaces { get; }

    Workspace? CurrentWorkspace { get; }

    /// <summary>A workspace was renamed (stored already): the switcher, page titles and hints show the new name at once.</summary>
    void RenameWorkspace(Workspace renamed);
}

/// <summary>A page that shows its workspace's name and follows a rename (<see cref="IWorkspaceDirectory.RenameWorkspace"/>).</summary>
public interface IShowsWorkspaceName
{
    void WorkspaceRenamed(Workspace renamed);
}
