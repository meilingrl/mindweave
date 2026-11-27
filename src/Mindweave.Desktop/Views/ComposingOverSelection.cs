using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;

namespace Mindweave.Desktop.Views;

/// <summary>
/// An input method that starts composing in a text box with text selected replaces the selection, as typing a letter
/// does: the composing text (pinyin, say) takes the place of the selected text, not a place in front of it.
/// </summary>
/// <remarks>
/// Avalonia 12.1.4 means to do this (<c>TextBox.PresenterPropertyChanged</c>), but subscribes only in
/// <c>OnAttachedToVisualTree</c> when the template is already applied. A box's template is applied later, at its first
/// layout, so in most boxes the selection stayed and the composing text was drawn in front of it, with the selection
/// highlight shifted over the wrong characters (a row's rename box, 2026-11-28). This does it for every text box.
/// </remarks>
public static class ComposingOverSelection
{
    private static int _registered;

    /// <summary>Once per process, however many applications are created (tests create several).</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            TextPresenter.PreeditTextProperty.Changed.AddClassHandler<TextPresenter>(OnPreeditChanged);
        }
    }

    private static void OnPreeditChanged(TextPresenter presenter, AvaloniaPropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.OldValue as string) || e.NewValue is not string { Length: > 0 } composing
            || presenter.TemplatedParent is not TextBox { IsReadOnly: false } box || box.SelectionStart == box.SelectionEnd)
        {
            return;
        }

        // Deleting the selection (one undo step, as typing over it) changes the text, and the presenter drops the
        // composing text when its text changes; it is put back at the caret, where the selection was.
        box.SelectedText = string.Empty;
        presenter.SetCurrentValue(TextPresenter.PreeditTextProperty, composing);
    }
}
