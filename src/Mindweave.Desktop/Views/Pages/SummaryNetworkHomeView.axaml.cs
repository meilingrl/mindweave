using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mindweave.Application.Preferences;
using Mindweave.Desktop.Text;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Views.Pages;

public sealed partial class SummaryNetworkHomeView : UserControl
{
    private const int QuestionLines = 4;
    private const double ExpandedShare = 0.6;
    private object? _node;
    private string? _question;
    private double _width = -1;
    private bool _needsToggle;
    private bool _expanded;
    private bool _applied;
    private double _detailsWidth;
    private readonly DispatcherTimer _legendDelay;
    private bool _legendPinned;
    private bool _legendHovered;

    public SummaryNetworkHomeView()
    {
        InitializeComponent();
        DetailQuestion.LayoutUpdated += (_, _) => RefreshQuestion();
        ToggleQuestionButton.Click += OnToggleQuestion;

        DetailsGrip.MoveStarted += (_, _) => _detailsWidth = Page?.Details.Width ?? 0;
        DetailsGrip.Moved += (_, delta) =>
        {
            // Dragging left widens the details; the canvas keeps at least half of the page.
            if (Page is { } page)
            {
                page.Details.Width = Math.Min(_detailsWidth - delta, Math.Max(SummaryNetworkDetailsLayout.MinWidth, Bounds.Width / 2));
            }
        };
        DetailsGrip.MoveEnded += (_, _) => Page?.Details.Save();
        DetailsGrip.ResetRequested += (_, _) =>
        {
            if (Page is { } page)
            {
                page.Details.Width = SummaryNetworkDetailsLayout.DefaultWidth;
                page.Details.Save();
            }
        };

        _legendDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Input, (_, _) => ShowLegend());
        _legendDelay.Stop();
        LegendButton.PointerEntered += (_, _) => HoverLegend();
        LegendButton.PointerExited += (_, _) => LeaveLegend();
        LegendCard.PointerEntered += (_, _) => HoverLegend();
        LegendCard.PointerExited += (_, _) => LeaveLegend();
        LegendButton.Click += (_, _) =>
        {
            _legendPinned = !_legendPinned;
            ShowLegend();
        };
        LegendButton.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && LegendCard.IsVisible)
            {
                CloseLegend();
                e.Handled = true;
            }
        };
        LegendButton.LostFocus += (_, _) => CloseLegend();
    }

    private SummaryNetworkHomeViewModel? Page => DataContext as SummaryNetworkHomeViewModel;

    private void HoverLegend()
    {
        _legendHovered = true;
        ShowLegend();
    }

    /// <summary>A short delay, so the pointer can go from the icon to the card.</summary>
    private void LeaveLegend()
    {
        _legendHovered = false;
        _legendDelay.Stop();
        _legendDelay.Start();
    }

    private void CloseLegend()
    {
        _legendPinned = false;
        _legendHovered = false;
        ShowLegend();
    }

    private void ShowLegend()
    {
        _legendDelay.Stop();
        LegendCard.IsVisible = _legendPinned || _legendHovered;
        LegendButton.Classes.Set("on", _legendPinned);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CloseLegend();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnToggleQuestion(object? sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        _applied = false;
        RefreshQuestion();
    }

    /// <summary>
    /// Keeps the question to four lines until 展开; expanded, it scrolls in a box of at most 60% of the details' height that leaves room for the buttons.
    /// The toggle shows only when the whole question needs more lines than four. The measuring runs only when the
    /// question or its width changes; another selected node starts collapsed again.
    /// </summary>
    private void RefreshQuestion()
    {
        var node = (DataContext as SummaryNetworkHomeViewModel)?.Selected?.Id;
        var text = DetailQuestion.Text;
        var width = DetailQuestion.Bounds.Width;
        // The details are as tall as their content: what they may take is the height beside the canvas.
        var height = DetailsArea.Bounds.Height - DetailsCard.Padding.Top - DetailsCard.Padding.Bottom;
        if (!Equals(node, _node))
        {
            _node = node;
            _expanded = false;
            _applied = false;
        }

        if (text != _question || width != _width)
        {
            _question = text;
            _width = width;
            _applied = false;
            _needsToggle = false;
            if (!string.IsNullOrEmpty(text) && width > 0)
            {
                var whole = new TextBlock { Text = text, TextWrapping = DetailQuestion.TextWrapping, FontSize = DetailQuestion.FontSize, FontFamily = DetailQuestion.FontFamily };
                whole.Measure(new Size(width, double.PositiveInfinity));
                _needsToggle = whole.TextLayout.TextLines.Count > QuestionLines;
            }
        }

        // At most 60% of the details, and never so tall that what follows the question (the buttons) leaves the panel.
        var others = DetailPanel.DesiredSize.Height - QuestionScroller.DesiredSize.Height;
        var maxHeight = _expanded && height > 0
            ? Math.Max(QuestionLines * DetailQuestion.FontSize * 1.5, Math.Min(height * ExpandedShare, height - others - 8))
            : double.PositiveInfinity;
        if (_applied && QuestionScroller.MaxHeight.Equals(maxHeight))
        {
            return;
        }

        _applied = true;
        DetailQuestion.MaxLines = _expanded ? 0 : QuestionLines;
        QuestionScroller.MaxHeight = maxHeight;
        ToggleQuestionButton.IsVisible = _needsToggle;
        ToggleQuestionButton.Content = _expanded ? UiText.HideWholeQuestion : UiText.ShowWholeQuestion;
    }
}
