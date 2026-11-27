using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.VisualTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// An input method that starts composing in a box whose text is selected replaces the selection, as typing does
/// (2026-11-28: renaming a row, the whole old name selected, pinyin was drawn in front of it and the old name stayed).
/// </summary>
public class ComposingOverSelectionTests
{
    [AvaloniaFact]
    public void Composing_over_the_selected_text_replaces_it()
    {
        var (window, box) = Show("英文进度报告翻译为中文");
        box.Focus(NavigationMethod.Tab);
        Assert.Equal(box.Text, box.SelectedText);

        var client = Client(box);
        client.SetPreeditText("P");
        window.UpdateLayout();
        Assert.Equal(string.Empty, box.Text);
        Assert.Equal(0, box.SelectionStart);
        Assert.Equal(0, box.SelectionEnd);
        Assert.Equal("P", Presenter(box).PreeditText);

        client.SetPreeditText("Prompt");
        client.SetPreeditText(null);
        window.KeyTextInput("Prompt");
        Assert.Equal("Prompt", box.Text);
    }

    [AvaloniaFact]
    public void Composing_over_part_of_the_text_keeps_the_rest()
    {
        var (window, box) = Show("火车的历史");
        box.Focus(NavigationMethod.Pointer);
        box.SelectionStart = 0;
        box.SelectionEnd = 2;

        Client(box).SetPreeditText("qiche");
        window.UpdateLayout();
        Assert.Equal("的历史", box.Text);
        Assert.Equal("qiche", Presenter(box).PreeditText);
        Assert.Equal(0, box.CaretIndex);
    }

    [AvaloniaFact]
    public void Composing_with_nothing_selected_leaves_the_text()
    {
        var (window, box) = Show("火车");
        box.Focus(NavigationMethod.Pointer);
        box.CaretIndex = 2;

        Client(box).SetPreeditText("shi");
        window.UpdateLayout();
        Assert.Equal("火车", box.Text);
        Assert.Equal("shi", Presenter(box).PreeditText);
    }

    [AvaloniaFact]
    public void A_read_only_box_keeps_its_text()
    {
        var (window, box) = Show("火车");
        box.IsReadOnly = true;
        box.Focus(NavigationMethod.Tab);
        box.SelectAll();

        // A read-only box offers no input method client; the composing text is set as a client would set it.
        Presenter(box).SetCurrentValue(TextPresenter.PreeditTextProperty, "shi");
        window.UpdateLayout();
        Assert.Equal("火车", box.Text);
    }

    private static (Window Window, TextBox Box) Show(string text)
    {
        var box = new TextBox { Text = text, Width = 200 };
        var window = new Window { Width = 300, Height = 100, Content = box };
        window.Show();
        window.UpdateLayout();
        return (window, box);
    }

    /// <summary>The client the platform's input method talks to, asked for as the platform asks for it.</summary>
    private static TextInputMethodClient Client(TextBox box)
    {
        var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
        box.RaiseEvent(request);
        return Assert.IsAssignableFrom<TextInputMethodClient>(request.Client);
    }

    private static TextPresenter Presenter(TextBox box) => box.GetVisualDescendants().OfType<TextPresenter>().Single();
}
