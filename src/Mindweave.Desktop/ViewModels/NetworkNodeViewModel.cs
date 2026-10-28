using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Network;
using Mindweave.Core.Network;

namespace Mindweave.Desktop.ViewModels;

public sealed class NetworkNodeViewModel : ObservableObject
{
    private NodePosition position;
    private bool isSelected, isSearchMatch;
    public NetworkNodeViewModel(NetworkNode node, ImmutableArray<NetworkEdge> edges, NodePosition position)
    {
        Node = node; this.position = position;
        IncomingPaths = edges.Where(e => e.ChildNodeId == Id).ToImmutableArray();
    }
    public NetworkNode Node { get; }
    public Guid Id => Node.Metadata.Id;
    public Guid ConversationId => Node.Metadata.ConversationId;
    public Guid RootConversationId => Node.RootConversationId;
    public string DisplayName => Node.Metadata.DisplayName;
    public string BaseTitle => Node.Metadata.BaseTitle;
    public string ConversationTitle => Node.ConversationTitle;
    public DateTimeOffset CreatedAt => Node.Metadata.CreatedAt;
    public bool IsUnread => Node.Metadata.IsUnread;
    public bool HasCandidate => Node.Metadata.HasCandidate;
    public bool HasLaterContent => Node.Metadata.HasLaterContent;
    public string StatusText => string.Join(" · ", new[] { IsUnread ? "未读" : "已读", HasCandidate ? "有候选版本" : null, HasLaterContent ? "有后续内容" : null }.OfType<string>());
    public ImmutableArray<NetworkEdge> IncomingPaths { get; }
    public string PathDescription => IncomingPaths.IsEmpty ? "没有已保存总结的上游节点" : string.Join(Environment.NewLine, IncomingPaths.Select(e =>
        $"{string.Join(" → ", e.Path.Select(p => p.Title))}（{Math.Max(0, e.Path.Length - 2)} 个未总结的中间会话）"));
    public NodePosition Position
    {
        get => position;
        internal set { if (SetProperty(ref position, value)) { OnPropertyChanged(nameof(X)); OnPropertyChanged(nameof(Y)); } }
    }
    public double X => Position.X;
    public double Y => Position.Y;
    public bool IsSelected { get => isSelected; internal set => SetProperty(ref isSelected, value); }
    public bool IsSearchMatch { get => isSearchMatch; internal set => SetProperty(ref isSearchMatch, value); }
}
