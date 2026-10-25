using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Storage;

public interface IWorkspaceStore
{
    /// <summary>All workspaces, oldest first.</summary>
    Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken cancellationToken);

    Task AddAsync(Workspace workspace, CancellationToken cancellationToken);
}
