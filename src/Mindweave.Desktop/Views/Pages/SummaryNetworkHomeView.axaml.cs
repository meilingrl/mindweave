using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
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

    public SummaryNetworkHomeView()
    {
        InitializeComponent();
        DetailQuestion.LayoutUpdated += (_, _) => RefreshQuestion();
        ToggleQuestionButton.Click += OnToggleQuestion;
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
        var height = DetailScroller.Bounds.Height;
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
