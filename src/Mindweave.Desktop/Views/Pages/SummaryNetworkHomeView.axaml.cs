using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Mindweave.Desktop.Text;

namespace Mindweave.Desktop.Views.Pages;

public sealed partial class SummaryNetworkHomeView : UserControl
{
    private const int QuestionLines = 4;
    private string? _question;
    private bool _expanded;

    public SummaryNetworkHomeView()
    {
        InitializeComponent();
        DetailQuestion.LayoutUpdated += (_, _) => RefreshQuestion();
        ToggleQuestionButton.Click += OnToggleQuestion;
    }

    private void OnToggleQuestion(object? sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        RefreshQuestion();
    }

    /// <summary>
    /// Keeps the question to four lines until 展开. The toggle shows only when the whole question needs more lines than that;
    /// another session's question starts collapsed again.
    /// </summary>
    private void RefreshQuestion()
    {
        var text = DetailQuestion.Text;
        if (text != _question)
        {
            _question = text;
            _expanded = false;
        }

        DetailQuestion.MaxLines = _expanded ? 0 : QuestionLines;
        var width = DetailQuestion.Bounds.Width;
        var needsToggle = false;
        if (!string.IsNullOrEmpty(text) && width > 0)
        {
            var whole = new TextBlock
            {
                Text = text,
                TextWrapping = DetailQuestion.TextWrapping,
                FontSize = DetailQuestion.FontSize,
                FontFamily = DetailQuestion.FontFamily,
                FontWeight = DetailQuestion.FontWeight,
            };
            whole.Measure(new Size(width, double.PositiveInfinity));
            needsToggle = whole.TextLayout.TextLines.Count > QuestionLines;
        }

        ToggleQuestionButton.IsVisible = needsToggle;
        ToggleQuestionButton.Content = _expanded ? UiText.HideWholeQuestion : UiText.ShowWholeQuestion;
    }
}
