using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mindweave.Desktop.ViewModels.Conversations;

namespace Mindweave.Desktop.Views.Conversations;

public sealed partial class NewConversationView : UserControl
{
    private NewConversationViewModel? _page;
    private bool _byKeyboard;

    public NewConversationView()
    {
        InitializeComponent();
        ComposerHost.SettingsRequested += (_, _) => (DataContext as NewConversationViewModel)?.OpenSettingsCommand.Execute(null);

        // The list is keyboard-selectable (§10.3.1): Up and Down move between the questions, as in a list.
        RecommendedQuestions.AddHandler(KeyDownEvent, OnQuestionsKeyDown, RoutingStrategies.Tunnel);
        RecommendedQuestions.AddHandler(PointerPressedEvent, (_, _) => _byKeyboard = false, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (ComposerHost.FindControl<TextBox>("Input") is { } input)
        {
            input.Focus();

            // A question put there by 开始探索 (§10.3.2) is ready to edit or send: the caret after it.
            input.CaretIndex = input.Text?.Length ?? 0;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_page is not null)
        {
            _page.Ignored -= OnIgnored;
        }

        _page = DataContext as NewConversationViewModel;
        if (_page is not null)
        {
            _page.Ignored += OnIgnored;
        }

        base.OnDataContextChanged(e);
    }

    private List<Button> QuestionButtons() =>
        [.. RecommendedQuestions.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "RecommendedQuestionButton")];

    private void OnQuestionsKeyDown(object? sender, KeyEventArgs e)
    {
        _byKeyboard = true;
        if (e.Key is not (Key.Up or Key.Down) || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        var buttons = QuestionButtons();
        var current = buttons.FindIndex(b => b.IsKeyboardFocusWithin
            || (b.GetVisualParent() is { } row && row.GetVisualDescendants().OfType<Button>().Any(x => x.IsFocused)));
        if (current < 0)
        {
            return;
        }

        var next = e.Key == Key.Down ? current + 1 : current - 1;
        if (next >= 0 && next < buttons.Count)
        {
            buttons[next].Focus(NavigationMethod.Directional);
        }

        e.Handled = true;
    }

    /// <summary>不感兴趣 removed a row: the focus goes to the question now in its place, the one before, or 换一批.</summary>
    private void OnIgnored(object? sender, int index)
    {
        // The focus was on the removed row's cross (or nowhere after it went); a click elsewhere keeps its own focus.
        var method = _byKeyboard ? NavigationMethod.Tab : NavigationMethod.Pointer;
        Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused && TopLevel.GetTopLevel(focused) is not null && !this.IsVisualAncestorOf(focused))
            {
                return;
            }

            var buttons = QuestionButtons();
            if (buttons.Count > 0)
            {
                buttons[Math.Min(index, buttons.Count - 1)].Focus(method);
            }
            else
            {
                RefreshRecommendationsButton.Focus(method);
            }
        });
    }
}
