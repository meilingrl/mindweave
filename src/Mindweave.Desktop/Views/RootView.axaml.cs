using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Mindweave.Desktop.Views;

/// <summary>
/// The window's content (bilingual-ui.md §6.7): the root page and the prompts over it, with the prompts' keyboard
/// handling. A language switch replaces it with a new one, so all of its text is read again in the new language.
/// </summary>
public sealed partial class RootView : UserControl
{
    private ViewModels.WorkspaceDeletionViewModel? _deletion;
    private ViewModels.ConversationDeletionViewModel? _conversationDeletion;
    private ViewModels.BackupRestoreViewModel? _backupRestore;
    private ViewModels.BackupDeletionViewModel? _backupDeletion;
    private TopLevel? _topLevel;

    private IInputElement? _focusBeforePrompt;

    public RootView()
    {
        InitializeComponent();
        WorkspaceDeleteName.AddHandler(KeyDownEvent, OnDeleteNameKeyDown, RoutingStrategies.Tunnel);

        // Prompts are modal for the keyboard too (health check 2026-11-12, item 11).
        foreach (var scrim in Scrims)
        {
            scrim.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty)
                {
                    OnPromptsChanged();
                }
            };
        }
    }

    /// <summary>
    /// A key aimed at anything in the window goes through the prompt check, the window itself included (nothing
    /// focused), so the handlers sit on the window while this view is in it.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnPromptKeyDown, RoutingStrategies.Tunnel);

        // Typed text and IME commits are their own events: they must not reach a box behind the prompt either.
        _topLevel?.AddHandler(TextInputEvent, OnPromptTextInput, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(KeyDownEvent, OnPromptKeyDown);
        _topLevel?.RemoveHandler(TextInputEvent, OnPromptTextInput);
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>The prompts, in drawing order: a later one is drawn over an earlier one.</summary>
    private IEnumerable<Border> Scrims => ((Panel)Content!).Children.OfType<Border>().Where(b => b.Classes.Contains("dialog-scrim"));

    /// <summary>The prompt drawn on top, if any is open.</summary>
    private Border? TopPrompt => Scrims.LastOrDefault(s => s.IsVisible);

    private IInputElement? Focused => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

    /// <summary>
    /// A prompt opened or closed. The top prompt takes the focus unless it already has it (its own default button; a
    /// dialog that focuses something else of its own, such as a name box, does so afterwards). When the last one
    /// closes, the focus goes back where it was before the first opened. Tab cycles inside a dialog (Styles/Dialogs.axaml).
    /// </summary>
    private void OnPromptsChanged()
    {
        var focused = Focused;
        var topmost = TopPrompt;

        // A prompt beneath the top one takes no input: its default and cancel buttons would otherwise answer Enter and
        // Esc before the top one's (the window finds them in drawing order, not top first).
        foreach (var scrim in Scrims)
        {
            scrim.IsEnabled = !scrim.IsVisible || ReferenceEquals(scrim, topmost);
        }

        if (topmost is not { } top)
        {
            if (_focusBeforePrompt is Control { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } previous && previous.IsAttachedToVisualTree())
            {
                var restore = previous;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => restore.Focus());
            }

            _focusBeforePrompt = null;
            return;
        }

        if (_focusBeforePrompt is null && !Scrims.Any(s => IsInside(focused, s)))
        {
            _focusBeforePrompt = focused;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => FocusPrompt(top));
    }

    private void FocusPrompt(Border prompt)
    {
        if (!prompt.IsVisible || !ReferenceEquals(prompt, TopPrompt) || IsInside(Focused, prompt))
        {
            return;
        }

        var controls = prompt.GetVisualDescendants().OfType<Control>().Where(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled).ToList();
        (controls.OfType<Button>().FirstOrDefault(b => b.IsDefault) ?? controls.FirstOrDefault())?.Focus();
    }

    /// <summary>While a prompt is open, a key aimed at anything outside it (the page, a prompt beneath) goes to the prompt instead.</summary>
    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (TopPrompt is { } top && !IsInside(e.Source as IInputElement, top))
        {
            e.Handled = true;
            FocusPrompt(top);
        }
    }

    private void OnPromptTextInput(object? sender, TextInputEventArgs e)
    {
        if (TopPrompt is { } top && !IsInside(e.Source as IInputElement, top))
        {
            e.Handled = true;
            FocusPrompt(top);
        }
    }

    private static bool IsInside(IInputElement? element, Visual container) =>
        element is Visual visual && (ReferenceEquals(visual, container) || visual.GetVisualAncestors().Contains(container));

    /// <summary>
    /// Enter in the name box finishes the typed confirmation: it deletes once the name matches and otherwise does
    /// nothing, so the default 取消 applies only with focus elsewhere and never throws the typed name away (round 4 review).
    /// </summary>
    private void OnDeleteNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        e.Handled = true;
        if (_deletion?.DeleteCommand is { } delete && delete.CanExecute(null))
        {
            delete.Execute(null);
        }
    }

    /// <summary>
    /// The dialogs that take the keyboard when they open are followed. The view takes the window's root view model
    /// through the window, and loses it (so it stops listening) when it leaves the window.
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        var root = DataContext as ViewModels.RootViewModel;
        if (_deletion is not null)
        {
            _deletion.PropertyChanged -= OnDeletionChanged;
        }

        _deletion = root?.WorkspaceDeletion;
        if (_deletion is not null)
        {
            _deletion.PropertyChanged += OnDeletionChanged;
        }

        if (_conversationDeletion is not null)
        {
            _conversationDeletion.PropertyChanged -= OnConversationDeletionChanged;
        }

        _conversationDeletion = root?.ConversationDeletion;
        if (_conversationDeletion is not null)
        {
            _conversationDeletion.PropertyChanged += OnConversationDeletionChanged;
        }

        if (_backupRestore is not null)
        {
            _backupRestore.PropertyChanged -= OnBackupDialogChanged;
        }

        _backupRestore = root?.BackupRestore;
        if (_backupRestore is not null)
        {
            _backupRestore.PropertyChanged += OnBackupDialogChanged;
        }

        if (_backupDeletion is not null)
        {
            _backupDeletion.PropertyChanged -= OnBackupDialogChanged;
        }

        _backupDeletion = root?.BackupDeletion;
        if (_backupDeletion is not null)
        {
            _backupDeletion.PropertyChanged += OnBackupDialogChanged;
        }
    }

    /// <summary>The backup dialogs take the keyboard when they open: 取消 has the focus, Enter and Esc cancel (2026-11-12).</summary>
    private void OnBackupDialogChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewModels.BackupRestoreViewModel.IsOpen))
        {
            return;
        }

        if (sender is ViewModels.BackupRestoreViewModel { IsOpen: true })
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => BackupRestoreCancel.Focus());
        }
        else if (sender is ViewModels.BackupDeletionViewModel { IsOpen: true })
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => BackupDeleteCancel.Focus());
        }
    }

    /// <summary>The conversation delete dialog takes the keyboard when it opens: 取消 has the focus, Enter and Esc cancel.</summary>
    private void OnConversationDeletionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.ConversationDeletionViewModel.IsOpen) && _conversationDeletion?.IsOpen == true)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ConversationDeleteCancel.Focus());
        }
    }

    private void OnDeletionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.WorkspaceDeletionViewModel.IsOpen) && _deletion?.IsOpen == true)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => WorkspaceDeleteName.Focus());
        }
    }
}
