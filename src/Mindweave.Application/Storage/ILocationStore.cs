using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Storage;

/// <summary>
/// Last valid page, restored on restart. Section and content keys are opaque to this layer;
/// the desktop navigation layer owns their meaning. Back history is never persisted.
/// </summary>
public sealed record PageLocation(WorkspaceId WorkspaceId, string SectionKey, string? ContentKey);

public interface ILocationStore
{
    Task<PageLocation?> GetLastAsync(CancellationToken cancellationToken);

    Task SetLastAsync(PageLocation location, CancellationToken cancellationToken);
}
