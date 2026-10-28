using System.Collections.Immutable;
using Mindweave.Core.Network;

namespace Mindweave.Application.Network;

public sealed record NetworkConversation(Guid Id, Guid? ParentId, string Title, DateTimeOffset CreatedAt);
public sealed record NetworkNodeMetadata(Guid Id, Guid ConversationId, string DisplayName,
    string BaseTitle, DateTimeOffset CreatedAt, bool IsUnread, bool HasCandidate, bool HasLaterContent);
public sealed record NetworkNode(NetworkNodeMetadata Metadata, Guid RootConversationId, string ConversationTitle = "");
public sealed record NetworkPathItem(Guid ConversationId, string Title);
// Endpoints are included; Path.Length - 2 is the number of unsummarized intermediate conversations.
public sealed record NetworkEdge(Guid ParentNodeId, Guid ChildNodeId, ImmutableArray<NetworkPathItem> Path);
public sealed record NetworkGraphSnapshot(Guid WorkspaceId, string DataVersion,
    ImmutableArray<NetworkNode> Nodes, ImmutableArray<NetworkEdge> Edges);
public sealed record NetworkWorkspaceSnapshot(NetworkGraphSnapshot Graph,
    ImmutableDictionary<Guid, NodePosition> SavedPositions);
public enum LayoutWriteStatus { Applied, AlreadyExists, MembershipChanged, NodeMissing }
public sealed record LayoutWriteResult(LayoutWriteStatus Status, NodePosition? ExistingPosition = null);
