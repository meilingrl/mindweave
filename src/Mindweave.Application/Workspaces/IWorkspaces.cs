using Mindweave.Application.Storage;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Workspaces;

/// <summary>Creating workspaces and remembering the last page, as the shell and the workbench use them (course requirement C3).</summary>
public interface IWorkspaces
{
    Task<Workspace> CreateAsync(WorkspaceName name, CancellationToken cancellationToken);

    /// <summary>
    /// Gives the workspace a new name (2026-11-10; requirements recommend renaming). Names follow the creation rules
    /// (<see cref="WorkspaceName"/>), duplicates included: creating does not refuse a name already in use, so neither
    /// does this. Refused while storage is paused (a <see cref="StorageWriteException"/>, nothing changed). Returns the
    /// renamed workspace, or null when it no longer exists.
    /// </summary>
    Task<Workspace?> RenameAsync(WorkspaceId workspace, WorkspaceName name, CancellationToken cancellationToken);

    Task RememberLocationAsync(PageLocation location, CancellationToken cancellationToken);
}
