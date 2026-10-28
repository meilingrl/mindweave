using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Controls;

/// <summary>A normal focusable button with bounded text; all full metadata is available in the details pane.</summary>
public sealed class NetworkNodeControl : Button
{
    private readonly NetworkViewModel page;
    private readonly Border circle;
    private readonly TextBlock title;
    public NetworkNodeViewModel Node { get; }
    public NetworkNodeControl(NetworkViewModel page, NetworkNodeViewModel node)
    {
        this.page = page; Node = node; Name = $"NetworkNode_{node.Id:N}";
        UseLayoutRounding = false; Width = 200; Height = 112; Padding = new(0);
        BorderThickness = new(0); Background = Brushes.Transparent;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        RenderTransformOrigin = RelativePoint.Center;
        // The ordinary button is the full accessible hit area. Only this circle marks the
        // world-space point; links and its center therefore share the exact same projection.
        circle = new Border { Name = "NetworkNodeDot", Width = 22, Height = 22, CornerRadius = new(11),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        title = new TextBlock { Text = node.DisplayName, FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, Height = 36, MaxWidth = 196,
            TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var flags = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 196 };
        flags.Children.Add(new TextBlock { Text = node.IsUnread ? "● 未读  " : "○ 已读  ", FontSize = 11 });
        if (node.HasCandidate) flags.Children.Add(new TextBlock { Text = "◇ 候选  ", FontSize = 11 });
        if (node.HasLaterContent) flags.Children.Add(new TextBlock { Text = "↳ 后续内容", FontSize = 11 });
        var layout = new Grid { RowDefinitions = new("40,32,40") };
        layout.Children.Add(title); Grid.SetRow(circle, 1); layout.Children.Add(circle); Grid.SetRow(flags, 2); layout.Children.Add(flags);
        Content = layout;
        AutomationProperties.SetName(this, $"{node.DisplayName} · {node.StatusText}");
        ToolTip.SetTip(this, $"{node.DisplayName}\n{node.BaseTitle}\n来源：{node.ConversationTitle}\n{node.StatusText}");
        Click += (_, _) => page.SelectedNode = Node;
        UpdateState();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); Node.PropertyChanged += Changed; UpdateState(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { Node.PropertyChanged -= Changed; base.OnDetachedFromVisualTree(e); }
    private void Changed(object? sender, PropertyChangedEventArgs e) => UpdateState();
    private void UpdateState()
    {
        circle.BorderThickness = new(Node.IsSelected ? 3 : Node.IsSearchMatch ? 2 : 1.5);
        circle.BorderBrush = Brush.Parse(Node.IsSelected ? "#405634" : Node.IsSearchMatch ? "#916524" : "#7D8C70");
        circle.Background = Brush.Parse(Node.IsSelected ? "#65745A" : "#F4F5EF");
        title.TextDecorations = Node.IsSearchMatch ? TextDecorations.Underline : null;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        page.SelectedNode = Node;
        if (e.ClickCount == 2 && page.OpenSummaryCommand.CanExecute(null)) page.OpenSummaryCommand.Execute(null);
    }
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e); page.SelectedNode = Node;
        if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional) page.Session.FocusNode(Node.Id);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        { if (page.OpenSummaryCommand.CanExecute(null)) page.OpenSummaryCommand.Execute(null); e.Handled = true; return; }
        base.OnKeyDown(e);
    }
}
