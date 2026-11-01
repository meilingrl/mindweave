using Mindweave.Application.Storage;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Workspaces;

/// <summary>Creating workspaces and remembering the last page, as the shell and the workbench use them (course requirement C3).</summary>
public interface IWorkspaces
{
    Task<Workspace> CreateAsync(WorkspaceName name, CancellationToken cancellationToken);

    Task RememberLocationAsync(PageLocation location, CancellationToken cancellationToken);
}
