using Microsoft.Extensions.Logging;
using Mindweave.Application.Storage;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Workspaces;

public sealed partial class WorkspaceService(
    IWorkspaceStore store,
    ILocationStore locations,
    IClock clock,
    ILogger<WorkspaceService> logger) : IWorkspaces
{
    public Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAsync(cancellationToken);

    public async Task<Workspace> CreateAsync(WorkspaceName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        var workspace = new Workspace(WorkspaceId.New(), name, clock.UtcNow);
        await store.AddAsync(workspace, cancellationToken).ConfigureAwait(false);
        LogCreated(logger, workspace.Id);
        return workspace;
    }

    /// <summary>
    /// Records the last valid page. This is a preference, not core content:
    /// a failure is logged and swallowed so navigation never blocks on it.
    /// </summary>
    public async Task RememberLocationAsync(PageLocation location, CancellationToken cancellationToken)
    {
        try
        {
            await locations.SetLastAsync(location, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Degradable preference write; see summary.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogLocationNotSaved(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created workspace {WorkspaceId}")]
    private static partial void LogCreated(ILogger logger, WorkspaceId workspaceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Last page could not be saved")]
    private static partial void LogLocationNotSaved(ILogger logger, Exception exception);
}
