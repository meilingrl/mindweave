using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Mindweave.Application.Recommendations;
using Mindweave.Application.SummaryNetwork;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Views.Pages;

/// <summary>
/// Draws the network and turns pointer input into selection, opening and viewport changes (architecture §11.1). All
/// drawing uses one canvas → screen transform, so nodes and edges always agree. Positions are computed, never changed here;
/// the viewport lives in the workspace's <see cref="SummaryNetworkSession"/>. The look follows the confirmed network sample
/// (S7): dot grid, labels to the right, hover halo, the selected node's source trail, search rings. Every colour is a
/// theme resource, read again when the theme changes.
/// </summary>
public sealed class SummaryNetworkCanvas : Control
{
    public const double RootRadius = SummaryNetworkLayout.NodeRadius;
    public const double ChildRadius = 7;
    public const double HoverGrowth = 2;

    /// <summary>Labels other than the selected, hovered or matching ones are hidden at this zoom and below.</summary>
    public const double LabelHideScale = 0.45;

    /// <summary>The largest label size; below it labels scale with the zoom so they stay inside their reserved box.</summary>
    public const double LabelMaxSize = 13;

    /// <summary>A selected, hovered or matching label shown below the hiding threshold is drawn at least this large.</summary>
    public const double FocusedLabelMinSize = 10;

    /// <summary>Screen px between a date and the centre of its root's dot.</summary>
    public const double CaptionGap = SummaryNetworkLayout.CaptionGap;

    private const double CaptionSize = SummaryNetworkLayout.CaptionSize;
    private const double EdgeLabelSize = 10;
    private const double EdgeLabelEmphasizedSize = 11;

    private const double GridSpacing = 28;

    /// <summary>How far outside a dot its summary ring is drawn.</summary>
    internal const double SummaryRingGap = SummaryNetworkLayout.SummaryRingGap;

    /// <summary>The time thread's arrowhead.</summary>
    private const double ArrowLength = 6;
    private const double ArrowAngle = Math.PI / 6;

    private static readonly Typeface Face = new(FontFamily.Default);
    internal static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private readonly Dictionary<SummaryNodeId, Vector> _offsets = [];
    private readonly DispatcherTimer _motion;
    private SummaryNetworkHomeViewModel? _page;
    private SummaryNetworkPalette? _palette;
    private Point? _panFrom;
    private Viewport _panStart;
    private bool _dragged;
    private Point? _pointer;

    /// <summary>
    /// The summary the last press of "关联到…" landed on: the second press of a double click there belongs to that pick,
    /// so it neither opens the summary (leaving the new link's note editor) nor links it again.
    /// </summary>
    private SummaryNodeId? _picked;

    public SummaryNetworkCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        _motion = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => StepMotion());
        ActualThemeVariantChanged += (_, _) =>
        {
            _palette = null;
            InvalidateVisual();
        };
    }

    /// <summary>The viewport in use, fitted on first use.</summary>
    internal Viewport Current => _page?.Session.Viewport ?? Fit();

    /// <summary>The node under the pointer, if any (drawn larger, with a halo and its edges brightened).</summary>
    internal SummaryNodeId? Hovered { get; private set; }

    /// <summary>The recommended node under the pointer, if any (its ring and line drawn stronger).</summary>
    internal RecommendationId? HoveredRecommendation { get; private set; }

    /// <summary>The link suggestion whose words are under the pointer, if any (its line drawn stronger).</summary>
    internal Mindweave.Application.SummaryLinks.LinkSuggestionId? HoveredSuggestion { get; private set; }

    /// <summary>The link line under the pointer, if any (its note shows and its ends come out).</summary>
    internal Mindweave.Application.SummaryLinks.SummaryLinkId? HoveredLink { get; private set; }

    /// <summary>How faded the rest of the picture is while a dot with links is selected (design §8).</summary>
    internal const double DimOpacity = 0.3;

    /// <summary>Where the pointer is for hover and the pull; null when it is off the canvas.</summary>
    internal Point? Pointer => _pointer;

    /// <summary>The decorative pull is running (it stops by itself once every node has settled).</summary>
    internal bool IsMoving => _motion.IsEnabled;

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_page is not null)
        {
            _page.ViewChanged -= OnViewChanged;
            _page.ZoomRequested -= OnZoomRequested;
        }

        _page = DataContext as SummaryNetworkHomeViewModel;
        if (_page is not null)
        {
            _page.ViewChanged += OnViewChanged;
            _page.ZoomRequested += OnZoomRequested;
        }

        Hovered = null;
        HoveredRecommendation = null;
        HoveredSuggestion = null;
        HoveredLink = null;
        _offsets.Clear();
        InvalidateVisual();
        base.OnDataContextChanged(e);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _motion.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Canvas coordinates to control coordinates.</summary>
    internal Point ToScreen(double x, double y)
    {
        var v = Current;
        return new Point(((x - v.CenterX) * v.Zoom) + (Bounds.Width / 2), ((y - v.CenterY) * v.Zoom) + (Bounds.Height / 2));
    }

    internal (double X, double Y) ToCanvas(Point screen)
    {
        var v = Current;
        return (((screen.X - (Bounds.Width / 2)) / v.Zoom) + v.CenterX, ((screen.Y - (Bounds.Height / 2)) / v.Zoom) + v.CenterY);
    }

    private static double RadiusOf(NodeView node) => SummaryNetworkGeometry.DotRadius(node.Weight);

    private Point ScreenOf(NodeView node) => ToScreen(node.X, node.Y);

    /// <summary>
    /// The node under a control point: its hit box at its layout position. Hidden labels are not clickable. A drawn dot
    /// (its summary ring included) comes first, the nearest when several are under the point, so a neighbour's box never
    /// takes a click on a dot.
    /// </summary>
    internal NodeView? HitTest(Point screen)
    {
        if (_page is null)
        {
            return null;
        }

        var dot = _page.Nodes
            .Select(n => (Node: n, Distance: Point.Distance(ScreenOf(n), screen)))
            .Where(x => x.Distance <= SummaryNetworkLayout.DotFootprintRadius(x.Node.Weight, x.Node.HasSummary))
            .OrderBy(x => x.Distance)
            .Select(x => x.Node)
            .FirstOrDefault();
        if (dot is not null)
        {
            return dot;
        }

        var zoom = Current.Zoom;
        foreach (var node in _page.Nodes.Reverse())
        {
            if (HitBoxOf(node, ScreenOf(node), zoom).Contains(screen))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>
    /// The recommended node under a control point, when no summary node is (summaries win). Hidden question text is not
    /// clickable; the ring and the "推荐" tag always are.
    /// </summary>
    internal RecommendedNodeView? HitTestRecommendation(Point screen) =>
        PaintRecommendations().LastOrDefault(r => r.HitBounds.Contains(screen))?.Node;

    /// <summary>What each recommended node looks like now (requirements §10.3.2). The render pass and the tests read the same list.</summary>
    internal IReadOnlyList<RecommendationPaint> PaintRecommendations()
    {
        if (_page is null || _page.RecommendedNodes.Count == 0)
        {
            return [];
        }

        var palette = _palette ??= SummaryNetworkPalette.From(this);
        var zoom = Current.Zoom;
        var size = SummaryNetworkGeometry.LabelSize(zoom);
        var nodes = _page.Nodes.ToDictionary(n => n.Id);
        var selected = _page.SelectedRecommendation?.Id;
        return [.. _page.RecommendedNodes.Select(node =>
        {
            var center = ToScreen(node.X, node.Y);
            var basis = nodes.TryGetValue(node.Basis, out var b) ? ScreenOf(b) + _offsets.GetValueOrDefault(b.Id) : ToScreen(node.BasisX, node.BasisY);
            var isSelected = node.Id == selected;
            var hovered = node.Id == HoveredRecommendation;
            var radius = ChildRadius + (hovered ? HoverGrowth : 0);
            var tagSize = Math.Max(9, Math.Min(11, size * 0.85));
            var tag = Text(UiText.RecommendedTag, tagSize, palette.Recommendation, FontWeight.SemiBold);
            var tagOrigin = new Point(center.X + radius + SummaryNetworkLayout.LabelGap + 3, center.Y - (tag.Height / 2));
            var tagBounds = new Rect(tagOrigin, new Size(tag.Width, tag.Height)).Inflate(new Thickness(3, 0));
            var visible = zoom > LabelHideScale || isSelected || hovered;
            var labelSize = zoom > LabelHideScale ? size : Math.Max(size, FocusedLabelMinSize);
            var text = visible ? Text(node.Label, labelSize, isSelected || hovered ? palette.LabelActive : palette.LabelRead) : null;
            var labelOrigin = text is null ? default : new Point(tagBounds.Right + 5, center.Y - (text.Height / 2));
            return new RecommendationPaint(node, center, radius, basis, isSelected, hovered)
            {
                Tag = tag,
                TagBounds = tagBounds,
                Text = text,
                LabelBounds = text is null ? default : new Rect(labelOrigin, new Size(text.Width, text.Height)),
            };
        })];
    }

    /// <summary>Labels show above the hiding threshold; below it only the selected, hovered and matching ones.</summary>
    private bool IsLabelVisible(NodeView node, double zoom) =>
        zoom > LabelHideScale || node.Id == Hovered || (_page is { } page && (node.Id == page.Session.Selected || page.Matches.Contains(node.Id)));

    /// <summary>A node's hit box: its dot and, while shown, its label.</summary>
    private Rect HitBoxOf(NodeView node, Point center, double zoom) =>
        SummaryNetworkGeometry.HitBox(center, node, zoom, IsLabelVisible(node, zoom));

    /// <summary>What each node looks like now, in drawing order. The render pass and the tests read the same list.</summary>
    internal IReadOnlyList<NodePaint> PaintNodes()
    {
        if (_page is null)
        {
            return [];
        }

        var palette = _palette ??= SummaryNetworkPalette.From(this);
        var zoom = Current.Zoom;
        var size = SummaryNetworkGeometry.LabelSize(zoom);
        var drawn = _page.Nodes.ToDictionary(n => n.Id, n => ScreenOf(n) + _offsets.GetValueOrDefault(n.Id));
        var incident = _page.Edges.SelectMany(e => new[] { (Node: e.From, Other: e.To), (Node: e.To, Other: e.From) })
            .Where(x => drawn.ContainsKey(x.Other)).ToLookup(x => x.Node, x => drawn[x.Other]);
        var selected = _page.Session.Selected;
        var trail = _page.Trail;
        var matches = _page.Matches;
        var focus = LinkFocus();
        var linkEnds = HoveredLinkEnds();
        return [.. _page.Nodes.Select(node =>
        {
            var hovered = node.Id == Hovered;
            var isSelected = node.Id == selected;
            var matched = matches.Contains(node.Id);
            var radius = RadiusOf(node) + (hovered ? HoverGrowth : 0);
            var center = drawn[node.Id];
            var ring = isSelected ? NodeRing.Selected : matched ? NodeRing.Match : NodeRing.None;
            var label = SummaryNetworkLayout.Label(node.ShortTitle, node.HasCandidate);
            var visible = IsLabelVisible(node, zoom);
            var labelSize = zoom > LabelHideScale ? size : Math.Max(size, FocusedLabelMinSize);
            var brush = hovered || isSelected ? palette.LabelActive : !node.Unread ? palette.LabelRead : palette.Label;
            var weight = node.Unread ? FontWeight.SemiBold : FontWeight.Normal;
            FormattedText? text;
            Point origin;
            if (node.Placed is { } placed)
            {
                // The spatial layout placed this label round its dot and wrapped it; its date line, if any, goes under it.
                text = visible ? PlacedText(placed, labelSize, brush, weight) : null;
                var day = text is not null && node.Dated ? DayText(node, palette) : null;
                origin = text is null ? default : PlaceBlock(placed.Side, radius + (node.HasSummary ? SummaryNetworkLayout.SummaryRingGap + 1 : 0), center, Size(text), day is null ? null : Size(day)).Title;
            }
            else
            {
                text = visible ? Text(label, labelSize, brush, weight) : null;
                origin = text is null ? default : SummaryNetworkGeometry.LabelOrigin(center, radius, new Size(text.Width, text.Height), incident[node.Id]);
            }

            FormattedText? date = null;
            Rect dateBounds = default;
            if (text is not null && hovered && node.Placed is { } hoveredPlaced)
            {
                // A placed label's hover date follows the same rule (final review M6): after the title's last line when
                // that covers no other dot, else on its own line under the block or over it, else not at all.
                var reach = radius + (node.HasSummary ? SummaryNetworkLayout.SummaryRingGap + 1 : 0);
                var day = node.Dated ? DayText(node, palette) : null;
                var lines = hoveredPlaced.Lines;
                var longer = new SpatialLabel(hoveredPlaced.Side, [.. lines.Take(lines.Count - 1), UiText.DotHoverLabel(lines[^1], node.CreatedAt)]);
                var oneLine = PlacedText(longer, labelSize, brush, weight);
                var (at, _, _) = PlaceBlock(hoveredPlaced.Side, reach, center, Size(oneLine), day is null ? null : Size(day));
                if (ClearOfOtherDots(node.Id, new Rect(at, Size(oneLine)), drawn))
                {
                    (label, text, origin) = (UiText.DotHoverLabel(label, node.CreatedAt), oneLine, at);
                }
                else
                {
                    date = Text(UiText.DotHoverDate(node.CreatedAt), Math.Min(labelSize, CaptionSize), brush);
                    var (_, _, block) = PlaceBlock(hoveredPlaced.Side, reach, center, Size(text), day is null ? null : Size(day));
                    var x = Aligned(hoveredPlaced.Side, block, date.Width);
                    var line = new Size(date.Width, date.Height);
                    var place = new[] { new Rect(new Point(x, block.Bottom), line), new Rect(new Point(x, block.Top - date.Height), line) }
                        .FirstOrDefault(r => ClearOfOtherDots(node.Id, r, drawn));
                    (date, dateBounds) = place == default ? (null, default) : (date, place);
                }
            }
            else if (text is not null && hovered)
            {
                // A hovered dot also says when its conversation started (final review M6): after the title when that
                // covers no other dot, else on a line of its own under the title or over it, else not at all (the details
                // still give it).
                var oneLine = Text(UiText.DotHoverLabel(label, node.CreatedAt), labelSize, brush, weight);
                var oneLineOrigin = SummaryNetworkGeometry.LabelOrigin(center, radius, new Size(oneLine.Width, oneLine.Height), incident[node.Id]);
                if (ClearOfOtherDots(node.Id, new Rect(oneLineOrigin, new Size(oneLine.Width, oneLine.Height)), drawn))
                {
                    (label, text, origin) = (UiText.DotHoverLabel(label, node.CreatedAt), oneLine, oneLineOrigin);
                }
                else
                {
                    // Its own line is as quiet as a day caption, and keeps clear of the captions too.
                    date = Text(UiText.DotHoverDate(node.CreatedAt), Math.Min(labelSize, CaptionSize), brush);
                    var title = new Rect(origin, new Size(text.Width, text.Height));
                    var line = new Size(date.Width, date.Height);
                    var captions = PaintCaptions().Select(c => c.Bounds).ToList();
                    var place = new[] { new Rect(new Point(title.Left, title.Bottom), line), new Rect(new Point(title.Left, title.Top - date.Height), line) }
                        .FirstOrDefault(r => ClearOfOtherDots(node.Id, r, drawn) && !captions.Any(r.Intersects));
                    (date, dateBounds) = place == default ? (null, default) : (date, place);
                }
            }

            return new NodePaint(
                node,
                center,
                radius,
                UnreadDot: node.Unread,
                hovered,
                trail.Contains(node.Id),
                ring,
                Halo: hovered || isSelected || linkEnds.Contains(node.Id) || (focus.Contains(node.Id) && !trail.Contains(node.Id)),
                LabelVisible: visible,
                LabelBold: node.Unread,
                labelSize,
                label)
            {
                Text = text,
                LabelBounds = text is null ? default : new Rect(origin, new Size(text.Width, text.Height)),
                DateText = date,
                DateBounds = dateBounds,
                SummaryRing = node.HasSummary,
                Dimmed = focus.Count > 0 && !focus.Contains(node.Id) && !linkEnds.Contains(node.Id),
            };
        })];
    }

    /// <summary>A label rectangle touches no other node's dot or summary ring as drawn.</summary>
    private bool ClearOfOtherDots(SummaryNodeId self, Rect label, Dictionary<SummaryNodeId, Point> drawn) =>
        _page!.Nodes.Where(n => n.Id != self).All(n =>
        {
            var center = drawn[n.Id];
            var reach = SummaryNetworkLayout.DotFootprintRadius(n.Weight, n.HasSummary);
            var x = Math.Clamp(center.X, label.Left, label.Right) - center.X;
            var y = Math.Clamp(center.Y, label.Top, label.Bottom) - center.Y;
            return (x * x) + (y * y) > reach * reach;
        });

    /// <summary>Each edge from the parent's drawn position to the child's, with its state.</summary>
    internal IReadOnlyList<EdgePaint> PaintEdges()
    {
        if (_page is null)
        {
            return [];
        }

        var trail = _page.Trail;
        // The time thread: dotted with an arrowhead in the spatial view, the main line on the timeline.
        var timeline = _page.Session.IsTimeline;
        var nodes = _page.Nodes.ToDictionary(n => n.Id);
        var focus = LinkFocus();
        return [.. _page.Edges.Select(edge =>
        {
            var from = ToScreen(edge.FromX, edge.FromY) + _offsets.GetValueOrDefault(edge.From);
            var to = ToScreen(edge.ToX, edge.ToY) + _offsets.GetValueOrDefault(edge.To);
            var thread = edge.Kind == SummaryNetworkEdgeKind.TimeThread;
            var state = !thread && trail.Contains(edge.To) ? EdgeState.Trail
                : Hovered is { } h && (h == edge.To || h == edge.From) ? EdgeState.Near
                : EdgeState.Normal;
            var arrow = thread && !timeline;
            return new EdgePaint(edge, from, to, state)
            {
                Arrow = arrow,
                ArrowInset = arrow && nodes.TryGetValue(edge.To, out var target)
                    ? SummaryNetworkGeometry.DotFootprintRadius(target.Weight, target.HasSummary) + (target.Id == Hovered ? HoverGrowth : 0) + 2
                    : 0,
                Curved = edge.Curved,
                Timeline = timeline,
                Dimmed = focus.Count > 0 && !(focus.Contains(edge.From) && focus.Contains(edge.To)),
            };
        })];
    }

    /// <summary>
    /// Each content link between the two summaries as drawn (following the pull): a quiet dash without words at rest, its
    /// note on the line while it is active (requirements §10.3.3). The note goes at the middle unless a summary (its ring
    /// and its name) or an earlier link's note is there, then further along the line; where every place is taken it is left
    /// out (the details still give it), never drawn over a summary's name (review 2026-11-17, g55b).
    /// </summary>
    internal IReadOnlyList<LinkPaint> PaintLinks()
    {
        if (_page is null || _page.LinkEdges.Count == 0)
        {
            return [];
        }

        var palette = _palette ??= SummaryNetworkPalette.From(this);
        var zoom = Current.Zoom;
        var selected = _page.Session.Selected;
        var focused = LinkFocus().Count > 0;
        var taken = _page.Nodes.Select(n => HitBoxOf(n, ScreenOf(n) + _offsets.GetValueOrDefault(n.Id), zoom)).ToList();
        return [.. _page.LinkEdges.Select(link =>
        {
            var from = ToScreen(link.AX, link.AY) + _offsets.GetValueOrDefault(link.A);
            var to = ToScreen(link.BX, link.BY) + _offsets.GetValueOrDefault(link.B);
            var touchesSelected = link.A == selected || link.B == selected;
            var active = touchesSelected || link.A == Hovered || link.B == Hovered || link.Id == HoveredLink;
            FormattedText? note = null;
            string? label = null;
            var bounds = default(Rect);

            // No word at rest: the note shows only while the link is active (design §8).
            if (active && link.Note is { } words)
            {
                var text = Text(words, EdgeLabelEmphasizedSize, palette.Link, FontWeight.SemiBold);
                var size = new Size(text.Width + 8, text.Height + 2);
                Rect At(double t) => new(from.X + ((to.X - from.X) * t) - (size.Width / 2), from.Y + ((to.Y - from.Y) * t) - (size.Height / 2), size.Width, size.Height);
                if (LabelStops.Select(At).FirstOrDefault(r => !taken.Any(o => o.Intersects(r))) is { Width: > 0 } free)
                {
                    (label, note, bounds) = (words, text, free);
                    taken.Add(free);
                }
            }

            return new LinkPaint(link, from, to, active) { Note = note, Label = label, NoteBounds = bounds, Dimmed = focused && !touchesSelected && link.Id != HoveredLink };
        })];
    }

    /// <summary>Where along a link's or suggestion's line its words may go, the middle first: away from a summary, a link's note or another suggestion's words.</summary>
    private static readonly double[] LabelStops = [0.5, 0.4, 0.6, 0.32, 0.68, 0.25, 0.75];

    /// <summary>
    /// Each pending link suggestion between the two summaries as drawn (following the pull), with "建议" and its
    /// note on the line, at the middle unless a summary, a link's note or another suggestion's words are there (requirements §10.3.4:
    /// never colour or line style alone). The words hide with the other labels when zoomed far out, unless an end is selected
    /// or hovered or the suggestion is highlighted.
    /// </summary>
    internal IReadOnlyList<SuggestionPaint> PaintSuggestions()
    {
        if (_page is null || _page.SuggestionEdges.Count == 0)
        {
            return [];
        }

        var palette = _palette ??= SummaryNetworkPalette.From(this);
        var zoom = Current.Zoom;
        var selected = _page.Session.Selected;
        var highlighted = _page.HighlightedSuggestion;
        // A summary lying on the line (its ring and its name) is never covered: the words would hide it and take its clicks.
        var taken = PaintLinks().Where(l => l.Note is not null).Select(l => l.NoteBounds)
            .Concat(_page.Nodes.Select(n => HitBoxOf(n, ScreenOf(n) + _offsets.GetValueOrDefault(n.Id), zoom)))
            .ToList();
        return [.. _page.SuggestionEdges.Select(edge =>
        {
            var from = ToScreen(edge.AX, edge.AY) + _offsets.GetValueOrDefault(edge.A);
            var to = ToScreen(edge.BX, edge.BY) + _offsets.GetValueOrDefault(edge.B);
            var active = edge.Id == highlighted || edge.Id == HoveredSuggestion || edge.A == selected || edge.B == selected || edge.A == Hovered || edge.B == Hovered;
            FormattedText? text = null;
            string? label = null;
            var bounds = default(Rect);
            if (zoom > LabelHideScale || active)
            {
                label = edge.Label;
                text = Text(label, active ? EdgeLabelEmphasizedSize : EdgeLabelSize, palette.Link, active ? FontWeight.SemiBold : FontWeight.Normal);
                var size = new Size(text.Width + 8, text.Height + 2);
                Rect At(double t) => new(from.X + ((to.X - from.X) * t) - (size.Width / 2), from.Y + ((to.Y - from.Y) * t) - (size.Height / 2), size.Width, size.Height);
                bounds = LabelStops.Select(At).FirstOrDefault(r => !taken.Any(o => o.Intersects(r)), At(0.5));
                taken.Add(bounds);
            }

            return new SuggestionPaint(edge, from, to, active) { Text = text, Label = label, LabelBounds = bounds };
        })];
    }

    /// <summary>The link whose line passes within a few px of a control point, the nearest first; ends are left to the dots.</summary>
    internal LinkEdgeView? HitTestLink(Point screen)
    {
        const double reach = 5;
        LinkEdgeView? best = null;
        var bestDistance = reach;
        foreach (var link in PaintLinks())
        {
            var d = link.To - link.From;
            var length = Math.Sqrt((d.X * d.X) + (d.Y * d.Y));
            if (length < 1)
            {
                continue;
            }

            var t = Math.Clamp((((screen.X - link.From.X) * d.X) + ((screen.Y - link.From.Y) * d.Y)) / (length * length), 0, 1);
            var near = link.From + (d * t) - screen;
            var distance = Math.Sqrt((near.X * near.X) + (near.Y * near.Y));
            if (distance <= bestDistance && t * length > 12 && (1 - t) * length > 12)
            {
                (best, bestDistance) = (link.Link, distance);
            }
        }

        return best;
    }

    /// <summary>
    /// The nodes brought out while a dot with links is selected: it, its link partners and its
    /// path to the root. Empty otherwise, so nothing fades (design §8).
    /// </summary>
    private HashSet<SummaryNodeId> LinkFocus()
    {
        if (_page?.Session.Selected is not { } selected)
        {
            return [];
        }

        var partners = _page.LinkEdges.Where(l => l.A == selected || l.B == selected).Select(l => l.A == selected ? l.B : l.A).ToList();
        if (partners.Count == 0)
        {
            return [];
        }

        var focus = new HashSet<SummaryNodeId>(_page.Trail) { selected };
        focus.UnionWith(partners);
        return focus;
    }

    /// <summary>The two ends of the hovered link, if any: they come out like a hovered dot.</summary>
    private HashSet<SummaryNodeId> HoveredLinkEnds() =>
        HoveredLink is { } hovered && _page?.LinkEdges.FirstOrDefault(l => l.Id == hovered) is { } link ? [link.A, link.B] : [];

    /// <summary>The suggestion whose words are under a control point (drawn last, so above summaries and their labels).</summary>
    internal SuggestionEdgeView? HitTestSuggestion(Point screen) =>
        PaintSuggestions().LastOrDefault(s => s.Text is not null && s.LabelBounds.Inflate(2).Contains(screen))?.Suggestion;

    /// <summary>
    /// The day labels of the roots that start a new day: above the root on the timeline, under its title in the spatial
    /// view; hidden with the other labels when zoomed far out.
    /// </summary>
    internal IReadOnlyList<CaptionPaint> PaintCaptions()
    {
        if (_page is null || Current.Zoom <= LabelHideScale)
        {
            return [];
        }

        var palette = _palette ??= SummaryNetworkPalette.From(this);
        var nodes = _page.Nodes.ToDictionary(n => n.Id);
        var result = new List<CaptionPaint>();
        var size = SummaryNetworkGeometry.LabelSize(Current.Zoom);

        // Spatial dates sit by their root's title, which the root's edges may have moved; only they need the edges.
        var spatial = !_page.Session.IsTimeline;
        var incident = spatial
            ? _page.Edges.SelectMany(e => new[] { (Node: e.From, Other: e.To), (Node: e.To, Other: e.From) })
                .Where(x => nodes.ContainsKey(x.Other)).ToLookup(x => x.Node, x => ScreenOf(nodes[x.Other]))
            : null;
        foreach (var caption in _page.Captions)
        {
            var members = caption.Members.Where(nodes.ContainsKey).Select(id => (Node: nodes[id], At: ScreenOf(nodes[id]))).ToList();
            if (members.Count == 0)
            {
                continue;
            }

            if (incident is not null)
            {
                // Like the titles, a date shrinks below zoom 1 so it keeps clear of the dots round it.
                var date = Text(caption.Text, CaptionSize * Math.Clamp(Current.Zoom, 0.6, 1), palette.Caption);
                var root = members[0].Node;
                if (root.Placed is { } placed)
                {
                    // Under the root's placed title, aligned like it, inside the block the layout reserved for both.
                    // As drawn: the pointer's pull moves the dot, and hovering grows it, so the title moves too.
                    var title = PlacedText(placed, size, Brushes.Black, root.Unread ? FontWeight.SemiBold : FontWeight.Normal);
                    var grow = root.Id == Hovered ? HoverGrowth : 0;
                    var reach = RadiusOf(root) + grow + (root.HasSummary ? SummaryNetworkLayout.SummaryRingGap + 1 : 0);
                    var at = members[0].At + _offsets.GetValueOrDefault(root.Id);
                    result.Add(new CaptionPaint(caption, date, PlaceBlock(placed.Side, reach, at, Size(title), Size(date)).Date));
                    continue;
                }

                result.Add(new CaptionPaint(caption, date, SpatialDateOrigin(root, members[0].At, date, incident, size)));
                continue;
            }

            var text = Text(caption.Text, CaptionSize, palette.Caption);
            var left = members.Min(m => m.At.X - RadiusOf(m.Node));
            var top = members.Min(m => m.At.Y);
            result.Add(new CaptionPaint(caption, text, new Point(left, top - CaptionGap - text.Height)));
        }

        return result;
    }

    /// <summary>
    /// Where a spatial date sits (spec §4.4): beside the root dot, on the title's line of text, above it, or below it when
    /// an edge lifted the title up, so it stays inside the root's packing footprint (dot, title, and the room for a lifted title).
    /// </summary>
    private static Point SpatialDateOrigin(NodeView node, Point center, FormattedText date, ILookup<SummaryNodeId, Point> incident, double labelSize)
    {
        var title = Text(SummaryNetworkLayout.Label(node.ShortTitle, node.HasCandidate), labelSize, Brushes.Black);
        var radius = RadiusOf(node);
        var at = SummaryNetworkGeometry.LabelOrigin(center, radius, new Size(title.Width, title.Height), incident[node.Id]);
        var x = center.X + radius + SummaryNetworkLayout.LabelGap;
        return at.Y < center.Y - 9 - 0.5 ? new Point(x, center.Y + 3) : new Point(x, center.Y - 9 - 2 - date.Height);
    }

    /// <summary>A placed label's wrapped lines as one block of text, each line aligned toward the dot.</summary>
    private static FormattedText PlacedText(SpatialLabel placed, double size, IBrush brush, FontWeight weight)
    {
        var text = Text(string.Join('\n', placed.Lines), size, brush, weight);
        text.MaxTextWidth = text.Width + 0.5;
        text.TextAlignment = SpatialLayout.Sides[placed.Side].Anchor switch
        {
            LabelAnchor.Start => TextAlignment.Left,
            LabelAnchor.End => TextAlignment.Right,
            _ => TextAlignment.Center,
        };
        return text;
    }

    /// <summary>The day caption of a root that starts a new day, as the spatial view draws it under the root's label.</summary>
    private FormattedText? DayText(NodeView node, SummaryNetworkPalette palette)
    {
        if (Current.Zoom <= LabelHideScale || _page?.Captions.FirstOrDefault(c => c.Members.Count > 0 && c.Members[0] == node.Id) is not { } caption)
        {
            return null;
        }

        return Text(caption.Text, CaptionSize * Math.Clamp(Current.Zoom, 0.6, 1), palette.Caption);
    }

    private static Size Size(FormattedText text) => new(text.Width, text.Height);

    /// <summary>
    /// Where a placed label goes as drawn: the block of its title and date line (if any) beside the dot on the layout's
    /// side (<see cref="SpatialLayout.LabelOffset"/>), the title on top and the date under it, both aligned toward the dot.
    /// </summary>
    internal static (Point Title, Point Date, Rect Block) PlaceBlock(int side, double reach, Point center, Size title, Size? date)
    {
        var width = Math.Max(title.Width, date?.Width ?? 0);
        var height = title.Height + (date?.Height ?? 0);
        var (left, top) = SpatialLayout.LabelOffset(side, reach, width, height);
        var block = new Rect(center.X + left, center.Y + top, width, height);
        return (new Point(Aligned(side, block, title.Width), block.Top), new Point(Aligned(side, block, date?.Width ?? 0), block.Top + title.Height), block);
    }

    /// <summary>The left edge of a line of the given width inside a placed block, aligned toward the dot.</summary>
    private static double Aligned(int side, Rect block, double width) => SpatialLayout.Sides[side].Anchor switch
    {
        LabelAnchor.Start => block.Left,
        LabelAnchor.End => block.Right - width,
        _ => block.Center.X - (width / 2),
    };

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var palette = _palette ??= SummaryNetworkPalette.From(this);
        context.FillRectangle(palette.Background, new Rect(Bounds.Size));
        DrawGrid(context, palette);
        if (_page is null || _page.Nodes.Count == 0)
        {
            return;
        }

        // Layers: resting edges, label backdrops (resting edges pass behind labels), highlighted edges (above the
        // backdrops, so a trail is never hidden by a label), nodes, label text, captions.
        var edges = PaintEdges();
        var nodes = PaintNodes();
        var recommended = PaintRecommendations();
        var links = PaintLinks();
        var suggestions = PaintSuggestions();
        // While a dot with links is selected, everything else fades (design §8).
        void Faded(bool dimmed, Action draw)
        {
            if (!dimmed)
            {
                draw();
                return;
            }

            using (context.PushOpacity(DimOpacity))
            {
                draw();
            }
        }

        foreach (var edge in edges.Where(e => !e.Highlighted))
        {
            Faded(edge.Dimmed, () => DrawEdge(context, palette, edge));
        }

        // Content links, quiet neutral dashes, over the resting source edges, under the nodes; their notes come last.
        foreach (var link in links)
        {
            Faded(link.Dimmed, () => context.DrawLine(palette.LinkPen(link.Active), link.From, link.To));
        }

        // While a dot with links is selected, suggestions, recommended nodes and dates fade too, unless they involve it.
        var focusSet = LinkFocus();
        var fading = focusSet.Count > 0;
        var selectedNow = _page.Session.Selected;

        // Pending link suggestions: light and dash-dotted beside the links; their words come last with the notes.
        foreach (var suggestion in suggestions)
        {
            Faded(fading && suggestion.Suggestion.A != selectedNow && suggestion.Suggestion.B != selectedNow,
                () => context.DrawLine(palette.LinkSuggestionPen(suggestion.Active), suggestion.From, suggestion.To));
        }

        // Recommended nodes: their dotted lines rest with the edges, their rings below the summaries (never covering one).
        foreach (var item in recommended)
        {
            Faded(fading, () => context.DrawLine(palette.RecommendationLink(item.Selected || item.Hovered), item.Basis, item.Center));
        }

        foreach (var item in recommended)
        {
            Faded(fading, () => DrawRecommendation(context, palette, item));
        }

        using (context.PushOpacity(0.85))
        {
            // A faded label keeps a faded backdrop, so lines still pass behind its text.
            foreach (var node in nodes.Where(n => n.Text is not null && !n.Dimmed))
            {
                context.FillRectangle(palette.Background, node.Backdrop, 3);
                if (node.DateText is not null)
                {
                    context.FillRectangle(palette.Background, node.DateBounds.Inflate(new Thickness(2, 0)), 3);
                }
            }
        }

        using (context.PushOpacity(0.85 * DimOpacity))
        {
            foreach (var node in nodes.Where(n => n.Text is not null && n.Dimmed))
            {
                context.FillRectangle(palette.Background, node.Backdrop, 3);
            }
        }

        foreach (var edge in edges.Where(e => e.Highlighted))
        {
            Faded(edge.Dimmed, () => DrawEdge(context, palette, edge));
        }

        foreach (var node in nodes)
        {
            Faded(node.Dimmed, () => DrawNode(context, palette, node));
        }

        foreach (var node in nodes)
        {
            Faded(node.Dimmed, () =>
            {
                if (node.Text is { } text)
                {
                    context.DrawText(text, node.LabelBounds.TopLeft);
                }

                if (node.DateText is { } date)
                {
                    context.DrawText(date, node.DateBounds.TopLeft);
                }
            });
        }

        foreach (var caption in PaintCaptions())
        {
            Faded(fading && caption.Caption.Members.Count > 0 && !focusSet.Contains(caption.Caption.Members[0]), () => context.DrawText(caption.Text, caption.Origin));
        }

        foreach (var link in links.Where(l => l.Note is not null))
        {
            // The note in a small framed pill over the line's middle, readable over edges and labels alike.
            Faded(link.Dimmed, () =>
            {
                context.DrawRectangle(palette.Background, null, link.NoteBounds, 4, 4);
                context.DrawRectangle(palette.LinkFill, palette.LinkNoteFrame, link.NoteBounds, 4, 4);
                context.DrawText(link.Note!, link.NoteBounds.TopLeft + new Vector(4, 1));
            });
        }

        foreach (var suggestion in suggestions.Where(s => s.Text is not null && !(fading && s.Suggestion.A != selectedNow && s.Suggestion.B != selectedNow)))
        {
            // "建议 · note" in a dashed frame (a link's note has a solid one), so the two never read alike.
            context.DrawRectangle(palette.Background, null, suggestion.LabelBounds, 4, 4);
            context.DrawRectangle(palette.LinkFill, palette.LinkSuggestionFrame, suggestion.LabelBounds, 4, 4);
            context.DrawText(suggestion.Text!, suggestion.LabelBounds.TopLeft + new Vector(4, 1));
        }
    }

    private static void DrawRecommendation(DrawingContext context, SummaryNetworkPalette palette, RecommendationPaint item)
    {
        var p = item.Center;
        var r = item.Radius;
        if (item.Selected || item.Hovered)
        {
            context.DrawEllipse(item.Selected ? palette.HaloSelected : palette.HaloHover, null, p, r + 11, r + 11);
        }

        if (item.Selected)
        {
            context.DrawEllipse(null, palette.RingSelected, p, r + 5, r + 5);
        }

        // A dashed ring over a pale fill (the line to the basis ends under it), then the word in a thin frame.
        context.DrawEllipse(palette.RecommendationFill, palette.RecommendationRing(item.Selected || item.Hovered), p, r, r);
        using (context.PushOpacity(0.85))
        {
            context.FillRectangle(palette.Background, item.TagBounds.Union(item.Text is null ? item.TagBounds : item.LabelBounds).Inflate(new Thickness(2, 0)), 3);
        }

        context.DrawRectangle(palette.RecommendationFill, palette.RecommendationTag, item.TagBounds, 3, 3);
        context.DrawText(item.Tag, item.TagBounds.TopLeft + new Vector(3, 0));
        if (item.Text is { } text)
        {
            context.DrawText(text, item.LabelBounds.TopLeft);
        }
    }

    private static void DrawEdge(DrawingContext context, SummaryNetworkPalette palette, EdgePaint edge)
    {
        if (edge.Arrow)
        {
            DrawTimeThread(context, palette, edge);
            return;
        }

        var pen = edge.Timeline ? palette.TimelinePen(edge.State, mainLine: edge.Edge.Kind == SummaryNetworkEdgeKind.TimeThread)
            : palette.EdgePen(edge.State);
        if (edge.Curved)
        {
            // A timeline branch to another lane: leaves the parent and reaches the child horizontally.
            var middle = (edge.From.X + edge.To.X) / 2;
            var curve = new StreamGeometry();
            using (var g = curve.Open())
            {
                g.BeginFigure(edge.From, false);
                g.CubicBezierTo(new Point(middle, edge.From.Y), new Point(middle, edge.To.Y), edge.To);
                g.EndFigure(false);
            }

            context.DrawGeometry(null, pen, curve);
        }
        else
        {
            context.DrawLine(pen, edge.From, edge.To);
        }
    }

    /// <summary>The time thread: a dotted line up to the later dot's edge, ending in a small open arrowhead.</summary>
    private static void DrawTimeThread(DrawingContext context, SummaryNetworkPalette palette, EdgePaint edge)
    {
        var delta = edge.To - edge.From;
        var length = Math.Sqrt((delta.X * delta.X) + (delta.Y * delta.Y));
        if (length <= edge.ArrowInset + ArrowLength)
        {
            return;
        }

        var near = edge.State == EdgeState.Near;
        var direction = delta / length;
        var tip = edge.To - (direction * edge.ArrowInset);
        context.DrawLine(palette.TimeThreadPen(near), edge.From, tip);
        Point Barb(double angle) => tip - (new Vector(
            (direction.X * Math.Cos(angle)) - (direction.Y * Math.Sin(angle)),
            (direction.X * Math.Sin(angle)) + (direction.Y * Math.Cos(angle))) * ArrowLength);
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(Barb(ArrowAngle), false);
            g.LineTo(tip);
            g.LineTo(Barb(-ArrowAngle));
            g.EndFigure(false);
        }

        context.DrawGeometry(null, palette.TimeArrowPen(near), head);
    }

    private void DrawGrid(DrawingContext context, SummaryNetworkPalette palette)
    {
        // Dots every 28 screen px; the grid pans with the view but its spacing does not scale.
        var origin = _page is { Nodes.Count: > 0 } ? ToScreen(0, 0) : default;
        var startX = ((origin.X % GridSpacing) + GridSpacing) % GridSpacing;
        var startY = ((origin.Y % GridSpacing) + GridSpacing) % GridSpacing;
        for (var x = startX; x < Bounds.Width; x += GridSpacing)
        {
            for (var y = startY; y < Bounds.Height; y += GridSpacing)
            {
                context.DrawEllipse(palette.GridDot, null, new Point(x, y), 0.8, 0.8);
            }
        }
    }

    private static void DrawNode(DrawingContext context, SummaryNetworkPalette palette, NodePaint node)
    {
        var p = node.Center;
        var r = node.Radius;
        if (node.Halo)
        {
            context.DrawEllipse(node.Ring == NodeRing.Selected ? palette.HaloSelected : palette.HaloHover, null, p, r + 11, r + 11);
        }

        if (node.Ring != NodeRing.None)
        {
            // Outside a summary ring, so the two never run together.
            var gap = node.SummaryRing ? SummaryRingGap + 3.5 : 5;
            context.DrawEllipse(null, node.Ring == NodeRing.Selected ? palette.RingSelected : palette.RingMatch, p, r + gap, r + gap);
        }

        var tone = node.Hovered ? palette.NodeHover : node.OnTrail ? palette.NodeTrail : palette.Node;
        context.DrawEllipse(tone, null, p, r, r);
        if (node.SummaryRing)
        {
            context.DrawEllipse(null, palette.SummaryRingPen(tone), p, r + SummaryRingGap, r + SummaryRingGap);
        }

        if (node.UnreadDot)
        {
            context.DrawEllipse(palette.UnreadDot, null, node.UnreadDotCenter, NodePaint.UnreadDotRadius, NodePaint.UnreadDotRadius);
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        // A size change keeps the centre and zoom; only an unset viewport is fitted (§11.3).
        base.OnSizeChanged(e);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetPosition(this);
        if (e.ClickCount >= 2 && _picked is { } last && HitTest(point)?.Id == last)
        {
            e.Handled = true;
            return;
        }

        _picked = null;
        if (_page is { IsPicking: true } picking)
        {
            // "关联到…": a click on another summary links it; a recommended node is refused with the reason; nothing is
            // selected. Empty canvas still pans.
            if (HitTest(point) is { } picked)
            {
                _picked = picked.Id;
                _ = picking.PickNodeAsync(picked.Id);
                e.Handled = true;
                return;
            }

            if (HitTestRecommendation(point) is not null)
            {
                picking.RefusePickOfRecommendation();
                e.Handled = true;
                return;
            }
        }

        if (_page is { IsPicking: false } && HitTest(point) is null && HitTestSuggestion(point) is { } suggestion)
        {
            // The suggestion's words (a summary under them wins): its row in the details stands out with 接受 and 忽略.
            _page.SelectSuggestion(suggestion.Id);
            e.Handled = true;
            return;
        }

        if (HitTest(point) is { } node)
        {
            _page?.Select(node.Id);
            if (e.ClickCount >= 2)
            {
                _page?.OpenCommand.Execute(node.Id);
            }

            e.Handled = true;
            return;
        }

        if (HitTestRecommendation(point) is { } recommended)
        {
            // Selected only: never dragged (it has no place of its own), never opened by a double click.
            _page?.SelectRecommendation(recommended);
            e.Handled = true;
            return;
        }

        _panFrom = point;
        _panStart = Current;
        _dragged = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_panFrom is not { } from || _page is null)
        {
            SetPointer(point);
            return;
        }

        if (!_dragged && Math.Abs(point.X - from.X) + Math.Abs(point.Y - from.Y) < 3)
        {
            return;
        }

        if (!_dragged)
        {
            // The view as it is now: a reload since the press may have opened the view again.
            _panStart = Current;
        }

        _dragged = true;
        _page.Session.Viewport = _panStart with
        {
            CenterX = _panStart.CenterX - ((point.X - from.X) / _panStart.Zoom),
            CenterY = _panStart.CenterY - ((point.Y - from.Y) / _panStart.Zoom),
        };
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_panFrom is null)
        {
            SetPointer(null);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerReleased(e);
        if (_panFrom is not null && !_dragged && _page is not { IsPicking: true })
        {
            // A click on empty canvas clears the selection (not while "关联到…" waits for a summary).
            _page?.Select(null);
        }

        _panFrom = null;
        e.Pointer.Capture(null);

        // During a pan the pointer stays put while the network moves under it: hover follows what is under it now.
        SetPointer(e.GetPosition(this));
    }

    /// <summary>A pan ends with the capture (low finding ui-vm-6): it used to follow the pointer after the button was let go elsewhere.</summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _panFrom = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerWheelChanged(e);
        if (_page is null)
        {
            return;
        }

        var zoomKey = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (zoomKey || _page.WheelZooms)
        {
            ZoomAround(e.GetPosition(this), Math.Pow(1.15, e.Delta.Y));
        }
        else
        {
            // Two-finger scrolling pans, as in the network sample.
            var v = Current;
            _page.Session.Viewport = v with { CenterX = v.CenterX - (e.Delta.X * 40 / v.Zoom), CenterY = v.CenterY - (e.Delta.Y * 40 / v.Zoom) };
            InvalidateVisual();
        }

        SetPointer(e.GetPosition(this));
        e.Handled = true;
    }

    /// <summary>Zooms keeping the canvas point under <paramref name="screen"/> where it is.</summary>
    internal void ZoomAround(Point screen, double factor)
    {
        if (_page is null)
        {
            return;
        }

        var v = Current;
        var zoom = Viewport.Clamp(v.Zoom * factor);
        var (x, y) = ToCanvas(screen);
        _page.Session.Viewport = new Viewport(
            x - ((screen.X - (Bounds.Width / 2)) / zoom),
            y - ((screen.Y - (Bounds.Height / 2)) / zoom),
            zoom);
        InvalidateVisual();
    }

    /// <summary>The pointer moved over the canvas (or left it): hover, the hand cursor and the decorative pull follow.</summary>
    internal void SetPointer(Point? point)
    {
        _pointer = point;
        var hovered = point is { } p ? HitTest(p)?.Id : null;
        var suggestion = hovered is null && _page is { IsPicking: false } && point is { } s ? HitTestSuggestion(s)?.Id : null;
        var recommended = hovered is null && suggestion is null && point is { } q ? HitTestRecommendation(q)?.Id : null;
        var link = hovered is null && suggestion is null && recommended is null && point is { } l ? HitTestLink(l)?.Id : null;
        if (hovered != Hovered || recommended != HoveredRecommendation || suggestion != HoveredSuggestion || link != HoveredLink)
        {
            Hovered = hovered;
            HoveredRecommendation = recommended;
            HoveredSuggestion = suggestion;
            HoveredLink = link;
            InvalidateVisual();
        }

        Cursor = hovered is null && recommended is null && suggestion is null ? Cursor.Default : HandCursor;

        if (!_motion.IsEnabled)
        {
            _motion.Start();
        }
    }

    /// <summary>One 16 ms easing step; stops the timer once every node has settled.</summary>
    internal void StepMotion()
    {
        if (_page is null)
        {
            _motion.Stop();
            return;
        }

        var moving = false;
        foreach (var node in _page.Nodes)
        {
            var target = SummaryNetworkGeometry.Attraction(ScreenOf(node), _pointer);
            var now = _offsets.GetValueOrDefault(node.Id);
            var next = now + ((target - now) * 0.18);
            if (Math.Abs(next.X - target.X) + Math.Abs(next.Y - target.Y) < 0.02)
            {
                next = target;
            }
            else
            {
                moving = true;
            }

            if (next == default)
            {
                _offsets.Remove(node.Id);
            }
            else
            {
                _offsets[node.Id] = next;
            }
        }

        if (!moving)
        {
            _motion.Stop();
        }

        InvalidateVisual();
    }

    private void OnZoomRequested(object? sender, double factor) => ZoomAround(new Point(Bounds.Width / 2, Bounds.Height / 2), factor);

    private void OnViewChanged(object? sender, bool reset) => InvalidateVisual();

    /// <summary>
    /// The opening view, kept on the session: the timeline at zoom 1 on its newest end (spec §5.3); otherwise, and after
    /// "显示全部", every node, label and caption fitted into view with a margin, zoom limited to the allowed range.
    /// </summary>
    private Viewport Fit()
    {
        if (_page is null || _page.Nodes.Count == 0)
        {
            // Nothing to fit: a request for "显示全部" must not outlive the empty picture and fit the next one.
            if (_page is not null)
            {
                _page.Session.FitAllRequested = false;
            }

            return new Viewport(0, 0, 1);
        }

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return new Viewport(0, 0, 1);
        }

        var opening = !_page.Session.FitAllRequested;
        var fitted = opening && _page.Session.IsTimeline
            ? SummaryNetworkGeometry.TimelineOpening(_page.Nodes, Bounds.Size, DrawnLabelWidth)
            : opening
                ? SummaryNetworkGeometry.SpatialOpening(_page.Nodes, Bounds.Size, _page.RecommendedNodes)
                : SummaryNetworkGeometry.FitTo(_page.Nodes, Bounds.Size, _page.RecommendedNodes);
        if (opening)
        {
            _page.Session.SetOpeningViewport(fitted);
        }
        else
        {
            _page.Session.Viewport = fitted;
        }

        _page.Session.FitAllRequested = false;
        return fitted;
    }

    /// <summary>A node's label as drawn at <see cref="LabelMaxSize"/> (zoom 1 and a little below): its width in px.</summary>
    private static double DrawnLabelWidth(NodeView node) =>
        Text(SummaryNetworkLayout.Label(node.ShortTitle, node.HasCandidate), LabelMaxSize, Brushes.Black, node.Unread ? FontWeight.SemiBold : FontWeight.Normal).Width;

    internal static FormattedText Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(Face.FontFamily, FontStyle.Normal, weight), size, brush);
}
