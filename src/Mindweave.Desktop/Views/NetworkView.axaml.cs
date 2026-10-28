using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Mindweave.Desktop.Controls;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Views;

public partial class NetworkView : UserControl
{
    private NetworkViewModel? page;
    private bool attached;
    public NetworkView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => ObservePage();
        this.FindControl<CheckBox>("NetworkDirectZoom")!.IsCheckedChanged += (_, _) =>
            this.FindControl<NetworkCanvas>("NetworkSurface")!.DirectWheelZoom = this.FindControl<CheckBox>("NetworkDirectZoom")!.IsChecked == true;
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); attached = true; ObservePage(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { attached = false; Unsubscribe(); base.OnDetachedFromVisualTree(e); }
    private void Unsubscribe()
    { if (page is not null) { page.PropertyChanged -= Changed; page.Session.PropertyChanged -= Changed; } page = null; }
    private void ObservePage()
    {
        Unsubscribe(); if (!attached) return;
        page = DataContext as NetworkViewModel;
        if (page is not null) { page.PropertyChanged += Changed; page.Session.PropertyChanged += Changed; }
        UpdateMetadata();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) => UpdateMetadata();
    private void UpdateMetadata()
    {
        this.FindControl<TextBlock>("NetworkScale")!.Text = $"{page?.Session.Viewport.Scale ?? 1:P0}";
        var node = page?.SelectedNode;
        this.FindControl<TextBox>("NetworkMetadata")!.Text = node is null ? "选择节点查看完整标题、来源及状态。" :
            $"显示名：{node.DisplayName}\n基础标题：{node.BaseTitle}\n来源会话：{node.ConversationTitle}\n创建时间：{node.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n状态：{node.StatusText}\n来源路径：{node.PathDescription}";
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.S || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0 || page is null) return;
        // This handler exists only on the network page; summary editors own their shortcuts.
        if (page.SaveLayoutCommand.CanExecute(null)) page.SaveLayoutCommand.Execute(null);
        e.Handled = true;
    }
}
