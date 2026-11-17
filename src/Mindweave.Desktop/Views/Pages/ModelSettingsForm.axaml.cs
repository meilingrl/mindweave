using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Views.Pages;

/// <summary>
/// The model connection's fields (settings and first-run onboarding). The keyboard and pointer handling lives here
/// because it is about the controls, not the settings: the model field opens, steps through and accepts the fetched
/// suggestions (the filtering itself is the view model's). The list behaves like a drop-down: the arrow toggles it,
/// Escape closes it, and it closes when focus leaves the field.
/// </summary>
public sealed partial class ModelSettingsForm : UserControl
{
    /// <summary>Whether the connection name and context capacity are shown (settings: yes; onboarding: no).</summary>
    public static readonly StyledProperty<bool> ShowsDetailsProperty =
        AvaloniaProperty.Register<ModelSettingsForm, bool>(nameof(ShowsDetails), defaultValue: true);

    private TopLevel? _topLevel;

    /// <summary>Whether the list was open when the press on the arrow began (light dismiss closes it before the click).</summary>
    private bool _listOpenAtTogglePress;

    public ModelSettingsForm()
    {
        InitializeComponent();
        ModelBox.AddHandler(KeyDownEvent, OnModelKeyDown, RoutingStrategies.Tunnel);
        ModelBox.AddHandler(PointerReleasedEvent, OnModelBoxReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        ModelBox.GotFocus += OnModelBoxFocused;
        ModelBox.LostFocus += OnModelBoxLostFocus;
        ModelList.AddHandler(PointerReleasedEvent, OnSuggestionReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        ModelPopup.Opened += OnModelListOpened;
    }

    public bool ShowsDetails
    {
        get => GetValue(ShowsDetailsProperty);
        set => SetValue(ShowsDetailsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowsDetailsProperty)
        {
            DetailRows.IsVisible = change.GetNewValue<bool>();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Registered before the popup's own light-dismiss handler (added each time it opens), so this sees the state first.
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(PointerPressedEvent, OnWindowPointerPressed);
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && IsOnToggle(source))
        {
            _listOpenAtTogglePress = Model?.IsModelListOpen == true;
        }
    }

    private bool IsOnToggle(Visual source) => source == ModelListToggle || ModelListToggle.IsVisualAncestorOf(source);

    private ModelSettingsViewModel? Model => DataContext as ModelSettingsViewModel;

    private void OnModelListToggle(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        if (model.IsModelListOpen || _listOpenAtTogglePress)
        {
            _listOpenAtTogglePress = false;
            model.IsModelListOpen = false;
        }
        else
        {
            model.OpenModelList();
            ModelBox.Focus();
        }
    }

    private void OnModelBoxReleased(object? sender, PointerReleasedEventArgs e)
    {
        // The arrow inside the field decides for itself.
        if (Model is { IsModelListOpen: false } model && !(e.Source is Visual source && IsOnToggle(source)))
        {
            model.OpenModelList();
        }
    }

    private void OnModelBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        // Tabbing or clicking elsewhere closes the list so it no longer covers the rows below; a press inside the list
        // itself keeps it (the choice is made on release).
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        var insideList = focused is not null && ModelPopup.Child is { } child && (focused == child || child.IsVisualAncestorOf(focused));
        if (!insideList && Model is { IsModelListOpen: true } model)
        {
            model.IsModelListOpen = false;
        }
    }

    private void OnModelBoxFocused(object? sender, FocusChangedEventArgs e)
    {
        // Tabbing into the field shows the list; a click is handled on release (so it also works when already focused).
        if (e.NavigationMethod == NavigationMethod.Tab && Model is { IsModelListOpen: false } model)
        {
            model.OpenModelList();
        }
    }

    private void OnModelListOpened(object? sender, EventArgs e)
    {
        // Opened by 获取列表 (focus on that button): the field takes focus, so Escape, the arrow keys and Tab act on the list.
        if (!ModelBox.IsKeyboardFocusWithin)
        {
            ModelBox.Focus();
        }

        // The model already in the field is highlighted, so the arrow keys continue from it.
        ModelList.SelectedItem = Model is { } model && model.ModelSuggestions.Contains(model.Model) ? model.Model : null;
        if (ModelList.SelectedIndex >= 0)
        {
            ModelList.ScrollIntoView(ModelList.SelectedIndex);
        }
    }

    private void OnModelKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down when !model.IsModelListOpen:
                model.OpenModelList();
                e.Handled = model.IsModelListOpen;
                break;
            case Key.Down or Key.Up:
                var count = model.ModelSuggestions.Count;
                if (count > 0)
                {
                    var index = ModelList.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                    ModelList.SelectedIndex = Math.Clamp(index, 0, count - 1);
                    ModelList.ScrollIntoView(ModelList.SelectedIndex);
                }

                e.Handled = true;
                break;
            case Key.Enter when model.IsModelListOpen && ModelList.SelectedItem is string chosen:
                Choose(model, chosen);
                e.Handled = true;
                break;
            case Key.Escape when model.IsModelListOpen:
                model.IsModelListOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void OnSuggestionReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (Model is { } model && e.Source is Visual source
            && (source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>()) is { Content: string chosen })
        {
            Choose(model, chosen);
            e.Handled = true;
        }
    }

    private void Choose(ModelSettingsViewModel model, string chosen)
    {
        model.ChooseModelCommand.Execute(chosen);
        ModelBox.CaretIndex = ModelBox.Text?.Length ?? 0;
    }
}
