using Mindweave.Application.Navigation;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Storage;

public sealed record WorkspaceSelection(Guid WorkspaceId, MainArea Area, ContentKind Kind = ContentKind.Overview, Guid? TargetId = null);

public interface IWorkspaceStore : IAsyncDisposable
{
    Task<IReadOnlyList<Workspace>> ListAsync();
    Task<Workspace> CreateAsync(string name);
    Task<WorkspaceSelection?> LoadSelectionAsync();
    Task SaveSelectionAsync(WorkspaceSelection selection);
}
