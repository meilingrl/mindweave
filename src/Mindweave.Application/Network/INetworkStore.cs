using System.Collections.Immutable;
using Mindweave.Core.Network;

namespace Mindweave.Application.Network;

public interface INetworkStore
{
    Task<NetworkWorkspaceSnapshot> LoadNetworkAsync(Guid workspaceId);
    Task<LayoutWriteResult> TrySaveInitialNodePositionAsync(Guid workspaceId, Guid nodeId, NodePosition position);
    Task<LayoutWriteResult> SaveLayoutAsync(Guid workspaceId,
        ImmutableDictionary<Guid, NodePosition> positions, ImmutableArray<Guid> expectedNodeIds);
}
