using System.Collections.Immutable;
using Mindweave.Core.Network;

namespace Mindweave.Desktop.Network;

public sealed partial class NetworkSession
{
    private NetworkViewport viewport = new(0, 0, 1);
    private double viewportWidth, viewportHeight;
    private bool isFitPending;
    public ImmutableDictionary<Guid, NodeExtent> Extents { get; private set; } = ImmutableDictionary<Guid, NodeExtent>.Empty;
    public double ViewportWidth => viewportWidth;
    public double ViewportHeight => viewportHeight;
    public bool IsFitPending { get => isFitPending; private set => SetProperty(ref isFitPending, value); }
    public NetworkViewport Viewport
    {
        get => viewport;
        set
        {
            if (IsQuiesced) return;
            _ = NetworkViewportMath.Pan(value, 0, 0); // shared validation, including legal scale/center
            SetProperty(ref viewport, value);
        }
    }

    public Task ArrangeAsync()
    {
        if (IsQuiesced || IsArranging || Graph.Nodes.IsEmpty) return Task.CompletedTask;
        var stamp = ++arrangeVersion; var epoch = lifetime; var graph = Graph; var extents = Extents;
        return TrackAsync(async () =>
        {
            IsArranging = true;
            try
            {
                var result = await arrangeAsync(graph, extents);
                if (IsQuiesced || epoch != lifetime || stamp != arrangeVersion || graph.DataVersion != Graph.DataVersion) return;
                if (!result.Keys.ToHashSet().SetEquals(Graph.Nodes.Select(n => n.Metadata.Id))) throw new InvalidDataException("布局节点不完整。");
                foreach (var point in result.Values) point.Validate();
                Positions = result; ++Revision; IsDirty = true; layoutError = null;
                NotifyGraphState(); ShowAll();
            }
            catch
            {
                if (!IsQuiesced && epoch == lifetime && stamp == arrangeVersion)
                { layoutError = "布局整理失败，当前布局仍保留。"; OnPropertyChanged(nameof(ErrorMessage)); }
            }
            finally { IsArranging = false; }
        });
    }

    /// <summary>Only the controller commits discard after all cancellable leave prompts succeed.</summary>
    public void Discard()
    {
        ++arrangeVersion;
        Positions = engine.PlaceMissing(Graph, SavedPositions, Extents);
        ++Revision; IsDirty = false; saveError = null; layoutError = null;
        NotifyGraphState();
    }

    public void SetViewportSize(double width, double height)
    {
        if (IsQuiesced) return;
        _ = NetworkViewportMath.ToWorld(new(0, 0), Viewport, width, height);
        SetProperty(ref viewportWidth, width, nameof(ViewportWidth));
        SetProperty(ref viewportHeight, height, nameof(ViewportHeight));
        if (IsFitPending && width > 0 && height > 0) ShowAll();
    }

    /// <summary>Measured world-space node sizes affect subsequent fits/arrangements, never rearrange implicitly.</summary>
    public void SetNodeExtents(ImmutableDictionary<Guid, NodeExtent> extents)
    {
        if (IsQuiesced) return;
        ArgumentNullException.ThrowIfNull(extents);
        foreach (var extent in extents.Values) extent.Validate(nameof(extents));
        Extents = extents; OnPropertyChanged(nameof(Extents));
    }

    public void ShowAll()
    {
        if (IsQuiesced) return;
        IsFitPending = ViewportWidth == 0 || ViewportHeight == 0;
        if (!IsFitPending) Viewport = NetworkViewportMath.Fit(Positions, Extents, ViewportWidth, ViewportHeight, Viewport);
    }

    public void Pan(double deltaX, double deltaY)
    {
        if (!IsQuiesced) Viewport = NetworkViewportMath.Pan(Viewport, deltaX, deltaY);
    }

    public void ZoomAt(NodePosition screenAnchor, double wheelDelta)
    {
        if (!IsQuiesced) Viewport = NetworkViewportMath.ZoomAt(Viewport, screenAnchor, ViewportWidth, ViewportHeight, wheelDelta);
    }

    public void Zoom(double wheelDelta) => ZoomAt(new(ViewportWidth / 2, ViewportHeight / 2), wheelDelta);

    public void FocusNode(Guid nodeId)
    {
        if (IsQuiesced || !Positions.TryGetValue(nodeId, out var point)) return;
        SelectedNodeId = nodeId; Viewport = new(point.X, point.Y, Viewport.Scale);
    }
}
