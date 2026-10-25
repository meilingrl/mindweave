using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mindweave.Core;

namespace Mindweave.Desktop;

public partial class MainWindow : Window
{
    private readonly WorkspaceViewModel _vm;
    private readonly Dictionary<string, SelectableTextBlock> _textBlocks = [];
    private readonly Dictionary<string, Border> _blockBorders = [];
    private readonly Dictionary<string, StackPanel> _blockGroups = [];
    private readonly Dictionary<string, MarkdownBlock> _blockModels = [];
    private sealed record AssistantView(StackPanel Blocks, TextBlock Status);
    private readonly Dictionary<string, AssistantView> _assistantViews = [];
    private readonly HashSet<string> _renderedMessages = [];
    private Control? _emptyPrompt;
    private readonly Popup _selectionPopup = new() { Placement = PlacementMode.Pointer, IsLightDismissEnabled = true, HorizontalOffset = 8, VerticalOffset = 12 };
    private (string MessageId, MarkdownBlock Block, int Start, int End)? _selection;
    private bool _restoring;
    private bool _resizePending;
    private bool _forceFollow;
    private int _renderEpoch;
    private readonly DispatcherTimer? _autoSaveTimer;
    private bool _savingTick;
    private bool _closingRequested;
    public bool CloseApproved { get; private set; }
    public async Task<bool> RequestCloseAsync()
    {
        if (CloseApproved) return true;
        if (_closingRequested) return false;
        _closingRequested = true;
        try
        {
            if (_vm.Generation.HasRunning && !await ConfirmStopAndExitAsync()) return false;
            ShellGrid.IsEnabled = false;
            _autoSaveTimer?.Stop();
            await _vm.Generation.CancelAllAsync();
            CaptureForSave();
            if (!await _vm.SaveAsync()) { UpdateGenerationChrome(); return false; }
            CloseApproved = true;
            Close();
            return true;
        }
        finally
        {
            if (!CloseApproved) ShellGrid.IsEnabled = true;
            _closingRequested = false;
        }
    }

    public MainWindow() : this(new WorkspaceViewModel()) { }
    public MainWindow(WorkspaceViewModel workspace)
    {
        _vm = workspace;
        InitializeComponent();
        DataContext = _vm;
        MainPane.Children.Add(_selectionPopup);
        SendButton.Content = Icons.Make("arrow", "#FFFFFF", 15);
        AboutButton.Content = Icons.Make("info", size: 16);
        MoreButton.Content = Icons.Make("more");
        SidebarToggle.Content = Icons.Make("sidebar", size: 15);
        SidebarReopen.Content = Icons.Make("sidebar", size: 15);
        BackToSourceButton.Content = Row(Icons.Make("back", "#6C775E", 15), Label("返回来源", 12), 7);
        NewChatButton.Content = Row(Icons.Make("new", size: 17), Label("新的探索", 13), 11);
        ComposerHint.Content = Row(Icons.Make("branch", "#89917E", 13), Label("选中文字，延伸一个问题", 11, "#89917E"), 7);
        NewChatButton.Click += (_, _) => _vm.NewConversation(CapturePosition(), Composer.Text ?? "");
        BackToSourceButton.Click += (_, _) => _vm.ReturnToSource(CapturePosition(), Composer.Text ?? "");
        MoreButton.Click += (_, _) => ShowHelp();
        AboutButton.Click += (_, _) => ShowHelp();
        ComposerHint.Click += (_, _) => ShowHelp();
        SidebarToggle.Click += (_, _) => ToggleSidebar(false);
        SidebarReopen.Click += (_, _) => ToggleSidebar(true);
        SendButton.Click += (_, _) => { if (_vm.Generation.IsRunning(_vm.Current.Id)) _vm.Generation.Cancel(_vm.Current.Id); else Send(); };
        Composer.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { Send(); e.Handled = true; }
        };
        ChatScroll.ScrollChanged += (_, _) => { if (!_restoring) { _vm.Current.Position = CapturePosition(); _vm.MarkDirty(); } _selectionPopup.IsOpen = false; };
        Composer.TextChanged += (_, _) => { if (!_restoring) { _vm.Current.Draft = Composer.Text ?? ""; _vm.MarkDirty(); } };
        SizeChanged += (_, _) => RestoreAfterResize();
        _vm.Navigated += RenderConversation;
        _vm.MessageUpdated += OnMessageUpdated;
        _vm.PropertyChanged += (_, _) => UpdateGenerationChrome();
        if (_vm.CanSave)
        {
            _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            _autoSaveTimer.Tick += async (_, _) =>
            {
                if (_savingTick || _restoring || _closingRequested) return;
                _savingTick = true;
                try { CaptureForSave(); if (!await _vm.SaveAsync()) _autoSaveTimer.Stop(); }
                finally { _savingTick = false; }
            };
            _autoSaveTimer.Start();
        }
        Closing += async (_, e) =>
        {
            if (CloseApproved) return;
            e.Cancel = true;
            await Task.Yield();
            await RequestCloseAsync();
        };
        Closed += (_, _) => { _autoSaveTimer?.Stop(); _selectionPopup.IsOpen = false; _vm.MessageUpdated -= OnMessageUpdated; _vm.Navigated -= RenderConversation; };
        RenderConversation(new(_vm.Current, _vm.Current.Position));
    }

    private void CaptureForSave()
    {
        var position = _restoring ? _vm.Current.Position : CapturePosition();
        var draft = Composer.Text ?? "";
        if (_vm.Current.Position != position || _vm.Current.Draft != draft) _vm.MarkDirty();
        _vm.Current.Position = position;
        _vm.Current.Draft = draft;
    }

    private Task<bool> ConfirmStopAndExitAsync()
    {
        var panel = new StackPanel { Margin = new Thickness(28), Spacing = 17 };
        panel.Children.Add(Label("仍有会话正在模拟输出", 20, "#424C36"));
        panel.Children.Add(new TextBlock { Text = "退出会停止所有会话的输出，并保存已经收到的内容。下次打开可以继续阅读，不会自动重新生成。", TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 25, Foreground = Brush.Parse("#7A846C") });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0, 9, 0, 0) };
        var keep = new Button { Name = "KeepExploringButton", Content = "继续探索", Classes = { "quiet" } };
        var exit = new Button { Name = "ConfirmExitButton", Content = "停止输出并退出", Background = Brush.Parse("#3E4933"), Foreground = Brushes.White, CornerRadius = new CornerRadius(8), Padding = new Thickness(13, 8) };
        var dialog = new Window { Title = "退出 Mindweave", Width = 470, Height = 235, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#FAFAF8"), Content = panel };
        keep.Click += (_, _) => dialog.Close(false);
        exit.Click += (_, _) => dialog.Close(true);
        actions.Children.Add(keep); actions.Children.Add(exit); panel.Children.Add(actions);
        return dialog.ShowDialog<bool>(this);
    }

    private void ToggleSidebar(bool show)
    {
        var position = CapturePosition();
        _restoring = true;
        SidebarContainer.IsVisible = show;
        ShellGrid.ColumnDefinitions[0].Width = new GridLength(show ? 244 : 0);
        SidebarReopen.IsVisible = !show;
        RestorePosition(new(_vm.Current, position));
    }

    private void RenderConversation(NavigationRequest request)
    {
        _selectionPopup.IsOpen = false;
        _selection = null;
        _restoring = true;
        var epoch = ++_renderEpoch;
        _textBlocks.Clear(); _blockBorders.Clear(); _blockGroups.Clear(); _blockModels.Clear(); _assistantViews.Clear(); _renderedMessages.Clear();
        _emptyPrompt = null;
        MessagesPanel.Children.Clear();
        HeaderTitle.Text = request.Conversation.Title;
        BackToSourceButton.IsVisible = request.Conversation.Source != null;
        Composer.Text = request.Conversation.Draft;
        ComposerSource.IsVisible = request.Conversation.Source != null;
        ComposerSourceText.Text = request.Conversation.Source is { } source ? $"来自「{source.Quote}」的分支" : "";
        Composer.Watermark = request.Conversation.Source == null ? "继续你的好奇心…" : "围绕这段文字，继续问点什么…";
        RenderSidebar();
        UpdateGenerationChrome();

        if (request.Conversation.Source is { } origin)
        {
            var parent = _vm.State.Conversations.Single(x => x.Id == origin.ConversationId);
            var sourcePanel = new StackPanel { Spacing = 8 };
            sourcePanel.Children.Add(Label("从这里延伸", 11, "#939789"));
            sourcePanel.Children.Add(new SelectableTextBlock { Text = "“" + origin.Quote + "”", FontSize = 16, Foreground = Brush.Parse("#606A54"), TextWrapping = TextWrapping.Wrap, LineHeight = 27 });
            var returnButton = new Button { Content = Row(Icons.Make("back", "#878D7E", 12), Label(parent.Title, 11, "#878D7E"), 5), Padding = new Thickness(0, 4), Classes = { "quiet" } };
            returnButton.Click += (_, _) => _vm.ReturnToSource(CapturePosition(), Composer.Text ?? "");
            sourcePanel.Children.Add(returnButton);
            MessagesPanel.Children.Add(new Border { BorderBrush = Brush.Parse("#D2D8C8"), BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(16, 2), Margin = new Thickness(0, 0, 0, 32), Child = sourcePanel });
        }
        else if (request.Conversation.Messages.Count > 0)
            MessagesPanel.Children.Add(new TextBlock { Text = "今天", FontSize = 11, Foreground = Brush.Parse("#B0B3A8"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 27) });

        if (request.Conversation.Messages.Count == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(0, 58, 0, 60), Spacing = 14 };
            empty.Children.Add(new TextBlock { Text = request.Conversation.Source == null ? "好奇心，从一个问题开始。" : "让这个问题，再往前一步。", FontSize = 25, FontWeight = FontWeight.Medium, Foreground = Brush.Parse("#4A5043") });
            empty.Children.Add(new TextBlock { Text = request.Conversation.Source == null ? "写下你想了解的事，或回到左侧继续探索。" : "来源已经带上。写下你想深入了解的部分。", FontSize = 14, Foreground = Brush.Parse("#939989") });
            _emptyPrompt = empty;
            MessagesPanel.Children.Add(empty);
        }
        foreach (var message in request.Conversation.Messages) RenderMessage(message);

        Dispatcher.UIThread.Post(() => { if (epoch == _renderEpoch) RestorePosition(request); }, DispatcherPriority.Loaded);
    }

    private void RenderSidebar()
    {
        SessionList.Children.Clear();
        foreach (var root in _vm.State.Conversations.Where(x => x.Source == null)) AddSession(root, 0);
    }

    private void AddSession(Conversation conversation, int depth)
    {
        var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*,8"), ColumnSpacing = 9 };
        panel.Children.Add(Icons.Make(depth == 0 ? "chat" : "branch", conversation.Id == _vm.Current.Id ? "#5F6755" : "#9DA291", 14));
        var title = new TextBlock { Text = conversation.Title, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush.Parse(conversation.Id == _vm.Current.Id ? "#353D2D" : "#6B7162") };
        Grid.SetColumn(title, 1); panel.Children.Add(title);
        if (_vm.Generation.IsRunning(conversation.Id)) { var dot = Label("•", 12, "#91A17E"); Grid.SetColumn(dot, 2); panel.Children.Add(dot); }
        var button = new Button { Content = panel, Classes = { "session" }, Margin = new Thickness(depth * 12, 0, 0, 0) };
        if (conversation.Id == _vm.Current.Id) button.Classes.Add("active");
        ToolTip.SetTip(button, conversation.Title);
        button.Click += (_, _) => _vm.SwitchTo(conversation.Id, CapturePosition(), Composer.Text ?? "");
        SessionList.Children.Add(button);
        foreach (var child in _vm.State.Conversations.Where(x => x.Source?.ConversationId == conversation.Id)) AddSession(child, depth + 1);
    }

    private void RenderMessage(Message message)
    {
        _renderedMessages.Add(message.Id);
        if (message.IsUser)
        {
            MessagesPanel.Children.Add(new Border
            {
                Background = Brush.Parse("#EEEEEA"), CornerRadius = new CornerRadius(17), Padding = new Thickness(17, 12),
                HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 565, Margin = new Thickness(80, 0, 0, 32),
                Child = new SelectableTextBlock { Text = message.Markdown, FontSize = 14.5, LineHeight = 25, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#4A4E43") }
            });
            return;
        }
        var byline = Row(new Border { Width = 21, Height = 21, Background = Brush.Parse("#EAEEE3"), CornerRadius = new CornerRadius(7), Child = new TextBlock { Text = "m", FontFamily = new FontFamily("Georgia"), FontStyle = FontStyle.Italic, FontSize = 19, Foreground = Brush.Parse("#68775A"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) } }, Label("Mindweave", 12, "#78816C"), 8);
        byline.Margin = new Thickness(0, 0, 0, 18);
        var blocks = new StackPanel();
        foreach (var block in MarkdownDocument.Parse(message.Markdown)) RenderBlock(message, block, blocks);
        var status = Label(ReplyStatusText(message.Status), 10, "#A8AE9F");
        _assistantViews[message.Id] = new(blocks, status);
        var tools = Row(IconButton("copy", "复制回答", async () => { if (Clipboard != null) await Clipboard.SetTextAsync(message.Markdown); }), status, 10);
        tools.Margin = new Thickness(-5, 2, 0, 32);
        MessagesPanel.Children.Add(new StackPanel { Children = { byline, blocks, tools } });
    }

    private void RenderBlock(Message message, MarkdownBlock block, StackPanel host, int? replaceIndex = null)
    {
        var key = message.Id + "/" + block.Id;
        var group = new StackPanel();
        var text = new SelectableTextBlock
        {
            FontSize = 15.5, LineHeight = 28.5, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#444A3D"),
            SelectionBrush = Brush.Parse("#D6DECA"), SelectionForegroundBrush = Brush.Parse("#293421"), Focusable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var run in block.Runs)
        {
            var inline = new Run(run.Text);
            if (run.Bold) { inline.FontWeight = FontWeight.SemiBold; inline.Foreground = Brush.Parse("#30382A"); }
            if (run.Code) { inline.FontFamily = new FontFamily("Menlo, Consolas, monospace"); inline.FontSize = 13; inline.Foreground = Brush.Parse("#697956"); }
            if (run.Link != null) { inline.Foreground = Brush.Parse("#6C8158"); inline.TextDecorations = TextDecorations.Underline; }
            text.Inlines!.Add(inline);
        }
        var wrapper = new Border { Margin = new Thickness(0, 0, 0, 17), CornerRadius = new CornerRadius(4) };
        switch (block.Kind)
        {
            case BlockKind.Heading:
                text.FontSize = block.Level == 1 ? 25 : 18;
                text.LineHeight = block.Level == 1 ? 38 : 31;
                text.FontWeight = block.Level == 1 ? FontWeight.SemiBold : FontWeight.Medium;
                text.Foreground = Brush.Parse("#30372A");
                wrapper.Margin = block.Level == 1 ? new Thickness(0, 0, 0, 19) : new Thickness(0, 14, 0, 12);
                break;
            case BlockKind.Bullet:
            case BlockKind.Numbered:
                var list = new Grid { ColumnDefinitions = new ColumnDefinitions("24,*") };
                list.Children.Add(new TextBlock { Text = block.Kind == BlockKind.Bullet ? "•" : block.Label, Foreground = Brush.Parse("#929B84"), FontSize = 14, Margin = new Thickness(3, 4, 0, 0) });
                Grid.SetColumn(text, 1); list.Children.Add(text); wrapper.Child = list; wrapper.Margin = new Thickness(0, 0, 0, 10);
                break;
            case BlockKind.Code:
                var codePanel = new StackPanel { Spacing = 9 };
                codePanel.Children.Add(Label(string.IsNullOrEmpty(block.Label) ? "TEXT" : block.Label.ToUpperInvariant(), 9, "#9AA18F"));
                text.FontFamily = new FontFamily("Menlo, Consolas, monospace"); text.FontSize = 13; text.LineHeight = 25;
                codePanel.Children.Add(text); wrapper.Child = codePanel; wrapper.Background = Brush.Parse("#F0F2EB"); wrapper.Padding = new Thickness(17, 13); wrapper.CornerRadius = new CornerRadius(10); break;
            case BlockKind.Quote:
                wrapper.BorderBrush = Brush.Parse("#D8DDCE"); wrapper.BorderThickness = new Thickness(2, 0, 0, 0); wrapper.Padding = new Thickness(14, 2); text.Foreground = Brush.Parse("#8B937F"); text.FontSize = 14; break;
        }
        wrapper.Child ??= text;
        _textBlocks[key] = text; _blockBorders[key] = wrapper;
        _blockGroups[key] = group; _blockModels[key] = block;
        text.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (message.Status == ReplyStatus.Generating || e.InitialPressMouseButton != MouseButton.Left || string.IsNullOrWhiteSpace(text.SelectedText)) return;
            _selection = (message.Id, block, text.SelectionStart, text.SelectionEnd);
            ShowSelectionPopup(text);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        var branchItem = new MenuItem { Header = "继续追问 ↗" };
        branchItem.Click += (_, _) =>
        {
            if (message.Status == ReplyStatus.Generating || string.IsNullOrWhiteSpace(text.SelectedText)) return;
            _selection = (message.Id, block, text.SelectionStart, text.SelectionEnd); CreateBranch();
        };
        var copyItem = new MenuItem { Header = "复制选中文字" };
        copyItem.Click += async (_, _) => { if (Clipboard != null) await Clipboard.SetTextAsync(text.SelectedText); };
        var context = new ContextMenu();
        context.Items.Add(branchItem); context.Items.Add(copyItem);
        context.Opening += (_, _) => branchItem.IsEnabled = message.Status != ReplyStatus.Generating;
        text.ContextMenu = context;
        foreach (var link in block.Runs.Where(x => x.Link != null))
        {
            var linkItem = new MenuItem { Header = "打开链接：" + link.Text + " ↗" };
            linkItem.Click += async (_, _) => { if (Launcher != null) await Launcher.LaunchUriAsync(new Uri(link.Link!)); };
            context.Items.Add(linkItem);
        }
        group.Children.Add(wrapper);
        foreach (var child in _vm.State.Conversations.Where(x => x.Source is { } s && s.ConversationId == _vm.Current.Id && s.MessageId == message.Id && s.BlockId == block.Id))
        {
            var chip = new Button { Content = Row(Icons.Make("branch", "#7E8A6F", 12), Label(child.Title, 11.5, "#78846A"), 7), Classes = { "branch" }, Margin = new Thickness(0, -4, 0, 17), HorizontalAlignment = HorizontalAlignment.Left };
            chip.Click += (_, _) => _vm.SwitchTo(child.Id, CapturePosition(), Composer.Text ?? "");
            group.Children.Add(chip);
        }
        if (replaceIndex is { } index && index < host.Children.Count) host.Children[index] = group;
        else host.Children.Add(group);
    }

    private void ShowSelectionPopup(Control target)
    {
        _selectionPopup.IsOpen = false;
        var button = new Button { Content = Row(Icons.Make("branch", "#FFFFFF", 13), Label("继续追问", 12, "#FFFFFF"), 8), Background = Brush.Parse("#3E4933"), Foreground = Brushes.White, Padding = new Thickness(12, 9), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(0) };
        button.Click += (_, _) => CreateBranch();
        _selectionPopup.Child = new Border { Child = button, CornerRadius = new CornerRadius(9), BoxShadow = new BoxShadows(new BoxShadow { Blur = 14, OffsetY = 3, Color = Color.Parse("#26000000") }) };
        _selectionPopup.PlacementTarget = target;
        _selectionPopup.IsOpen = true;
    }

    private void CreateBranch()
    {
        if (_selection is not { } selection) return;
        _selectionPopup.IsOpen = false;
        _vm.Branch(selection.MessageId, selection.Block.Id, selection.Start, selection.End, CapturePosition(), Composer.Text ?? "");
        Dispatcher.UIThread.Post(() => Composer.Focus());
    }

    private ReadingPosition CapturePosition()
    {
        if (ChatScroll.Offset.Y <= 0) return new(null, 0);
        (string Key, SelectableTextBlock Text, Point Origin)? candidate = null;
        foreach (var (key, text) in _textBlocks)
        {
            if (text.TranslatePoint(new Point(), ChatScroll) is not { } origin) continue;
            if (origin.Y <= 20 && origin.Y + text.Bounds.Height > 20) { candidate = (key, text, origin); break; }
            if (origin.Y > 20 && candidate == null) candidate = (key, text, origin);
        }
        if (candidate is not { } chosen) return new(null, ChatScroll.Offset.Y);
        var hit = chosen.Text.TextLayout.HitTestPoint(new Point(0, Math.Clamp(20 - chosen.Origin.Y, 0, Math.Max(0, chosen.Text.Bounds.Height - 1))));
        var character = hit.TextPosition;
        var line = chosen.Text.TextLayout.HitTestTextPosition(character);
        return new(chosen.Key, -(chosen.Origin.Y + line.Y), character);
    }

    private void RestorePosition(NavigationRequest request)
    {
        // Rebuilt blocks must be measured at their actual width before character hit-testing.
        UpdateLayout();
        var y = request.Position.Offset;
        if (request.Position.BlockId is { } key && _blockBorders.TryGetValue(key, out var target))
        {
            var point = target.TranslatePoint(new Point(), MessagesPanel);
            if (point is { } p) y = Math.Max(0, p.Y + (request.Highlight != null ? -110 : request.Position.Offset));
            if (request.Position.CharacterIndex is { } character && _textBlocks.TryGetValue(key, out var readingText) && readingText.TranslatePoint(new Point(), MessagesPanel) is { } readingOrigin)
                y = readingOrigin.Y + MessagesPanel.Margin.Top + readingText.TextLayout.HitTestTextPosition(character).Y + request.Position.Offset;
        }
        if (request.Highlight is { } source && _textBlocks.TryGetValue(source.MessageId + "/" + source.BlockId, out var text))
        {
            text.Focus(); text.SelectionStart = source.DisplayStart; text.SelectionEnd = source.DisplayEnd;
            text.SelectionBrush = Brush.Parse("#E2E7C9");
            _blockBorders[source.MessageId + "/" + source.BlockId].Background = Brush.Parse("#F4F5EB");
            UpdateLayout();
            var textOrigin = text.TranslatePoint(new Point(), MessagesPanel);
            var selectedLine = text.TextLayout.HitTestTextPosition(source.DisplayStart);
            if (textOrigin is { } origin) y = origin.Y + MessagesPanel.Margin.Top + selectedLine.Y - 110;
        }
        ChatScroll.Offset = new Vector(0, Math.Max(0, y));
        _restoring = false;
        _vm.Current.Position = CapturePosition();
        _vm.MarkDirty();
    }

    private void RestoreAfterResize()
    {
        if (_restoring || _resizePending || !IsVisible) return;
        var position = _vm.Current.Position;
        var conversationId = _vm.Current.Id;
        _resizePending = true; _restoring = true;
        Dispatcher.UIThread.Post(() =>
        {
            _resizePending = false;
            if (_vm.Current.Id == conversationId) RestorePosition(new(_vm.Current, position));
            else _restoring = false;
        }, DispatcherPriority.Loaded);
    }

    private void OnMessageUpdated(GenerationUpdate update)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnMessageUpdated(update)); return; }
        UpdateGenerationChrome();
        if (update.Change != GenerationChange.Appended) RenderSidebar();
        if (update.Conversation.Id != _vm.Current.Id) return;
        var position = CapturePosition();
        var following = _forceFollow || (ChatScroll.Extent.Height - ChatScroll.Viewport.Height - ChatScroll.Offset.Y < 70 && !_textBlocks.Values.Any(x => !string.IsNullOrEmpty(x.SelectedText)));
        _forceFollow = false;
        _restoring = true;
        if (_emptyPrompt != null) { MessagesPanel.Children.Remove(_emptyPrompt); _emptyPrompt = null; }
        foreach (var message in _vm.Current.Messages.Where(x => !_renderedMessages.Contains(x.Id))) RenderMessage(message);
        if (_assistantViews.TryGetValue(update.Message.Id, out var view))
        {
            var blocks = MarkdownDocument.Parse(update.Message.Markdown);
            var keys = blocks.Select(x => update.Message.Id + "/" + x.Id).ToHashSet();
            for (var index = 0; index < blocks.Count; index++)
            {
                var block = blocks[index];
                var key = update.Message.Id + "/" + block.Id;
                if (!_blockModels.TryGetValue(key, out var prior) || prior.Kind != block.Kind || prior.Level != block.Level || prior.Label != block.Label || prior.Text != block.Text || !prior.Runs.SequenceEqual(block.Runs))
                    RenderBlock(update.Message, block, view.Blocks, index);
            }
            foreach (var key in _blockModels.Keys.Where(x => x.StartsWith(update.Message.Id + "/", StringComparison.Ordinal) && !keys.Contains(x)).ToArray())
            {
                view.Blocks.Children.Remove(_blockGroups[key]);
                _blockGroups.Remove(key); _blockModels.Remove(key); _textBlocks.Remove(key); _blockBorders.Remove(key);
            }
            view.Status.Text = ReplyStatusText(update.Message.Status);
        }
        UpdateLayout();
        if (following)
        {
            ChatScroll.ScrollToEnd();
            _restoring = false;
            _vm.Current.Position = CapturePosition();
        }
        else RestorePosition(new(_vm.Current, position));
    }

    private static string ReplyStatusText(ReplyStatus status) => status switch
    {
        ReplyStatus.Generating => "模拟输出中 · 完成或停止后可分支",
        ReplyStatus.Cancelled => "已停止 · 已保留部分内容",
        ReplyStatus.Interrupted => "上次输出中断 · 已保留部分内容",
        ReplyStatus.Failed => "示例输出失败 · 已保留部分内容",
        _ => "固定示例"
    };

    private void UpdateGenerationChrome()
    {
        HeaderTitle.Text = _vm.Current.Title;
        var generating = _vm.Generation.IsRunning(_vm.Current.Id);
        SendButton.Content = Icons.Make(generating ? "stop" : "arrow", "#FFFFFF", 15);
        ToolTip.SetTip(SendButton, generating ? "停止模拟输出，保留已收到的内容" : "发送示例消息");
        FooterStatus.Text = _vm.PersistenceError ?? (_vm.DataFile == null ? "交互原型 · 固定示例内容，无真实 AI 服务" : "交互原型 · 本地自动保存 · 无真实 AI 服务");
        ToolTip.SetTip(FooterStatus, _vm.DataFile);
    }

    private void Send()
    {
        if (string.IsNullOrWhiteSpace(Composer.Text) || _vm.Generation.IsRunning(_vm.Current.Id)) return;
        _forceFollow = true;
        _ = _vm.SendAsync(Composer.Text);
        Composer.Text = "";
        Composer.Focus();
    }

    private async void ShowHelp()
    {
        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(28) };
        panel.Children.Add(Label("给好奇心留一条回来的路", 21, "#424C36"));
        panel.Children.Add(new TextBlock { Text = "拖选回答里的文字，点击「继续追问」。新分支保留来源；「返回来源」定位并高亮原句。\n\n发送后以约 16 秒分块输出固定示例。切换会话不会停止生成；输入区的停止按钮保留已收到内容。正在输出的回答完成或停止后才可创建分支，旧回答仍可正常选择与分支。\n\n本地自动保存会话、草稿、来源和字符阅读位置。重启后，未完成输出标为中断，不自动重跑。这个原型不连接 AI，仍不支持跨段选区和完整 Markdown。", TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 25, Foreground = Brush.Parse("#7A846C") });
        panel.Children.Add(new TextBlock { Text = _vm.PersistenceError ?? (_vm.DataFile == null ? "当前是内存预览。" : "保存位置：" + _vm.DataFile), TextWrapping = TextWrapping.Wrap, FontSize = 11, LineHeight = 18, Foreground = Brush.Parse("#969D8B") });
        var close = new Button { Content = "开始探索", HorizontalAlignment = HorizontalAlignment.Right, Classes = { "quiet" } };
        var dialog = new Window { Title = "Mindweave · 原型说明", Width = 490, Height = 535, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#FAFAF8"), Content = panel };
        close.Click += (_, _) => dialog.Close(); panel.Children.Add(close);
        await dialog.ShowDialog(this);
    }

    private static TextBlock Label(string text, double size = 14, string color = "#4A5140") => new() { Text = text, FontSize = size, Foreground = Brush.Parse(color), VerticalAlignment = VerticalAlignment.Center };
    private static StackPanel Row(Control first, Control second, double spacing) => new() { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center, Children = { first, second } };
    private static Button IconButton(string name, string tip, Action action)
    {
        var button = new Button { Content = Icons.Make(name, "#9BA38E", 13), Classes = { "quiet" }, Padding = new Thickness(6) };
        ToolTip.SetTip(button, tip); button.Click += (_, _) => action(); return button;
    }
}
