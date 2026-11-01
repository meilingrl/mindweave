using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Views.Pages;

/// <summary>
/// Settings. Two pieces of keyboard and pointer handling live here because they are about the controls, not the
/// settings: the arrow keys move along the appearance segments, and the model field opens, steps through and
/// accepts the fetched suggestions (the filtering itself is the view model's).
/// </summary>
public sealed partial class SettingsHomeView : UserControl
{
    public SettingsHomeView()
    {
        InitializeComponent();
        AppearancePicker.AddHandler(KeyDownEvent, OnAppearanceKeyDown, RoutingStrategies.Tunnel);
        ModelBox.AddHandler(KeyDownEvent, OnModelKeyDown, RoutingStrategies.Tunnel);
        ModelBox.AddHandler(PointerReleasedEvent, OnModelBoxReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        ModelBox.GotFocus += OnModelBoxFocused;
        ModelList.AddHandler(PointerReleasedEvent, OnSuggestionReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        ModelPopup.Opened += OnModelListOpened;
    }

    private ModelSettingsViewModel? Model => (DataContext as SettingsHomeViewModel)?.Model;

    private void OnAppearanceKeyDown(object? sender, KeyEventArgs e)
    {
        var step = e.Key switch
        {
            Key.Left or Key.Up => -1,
            Key.Right or Key.Down => 1,
            _ => 0,
        };
        if (step == 0)
        {
            return;
        }

        var segments = AppearancePicker.GetVisualDescendants().OfType<RadioButton>().ToList();
        var current = segments.FindIndex(s => s.IsFocused);
        if (current < 0)
        {
            current = segments.FindIndex(s => s.IsChecked == true);
        }

        var next = Math.Clamp(current + step, 0, segments.Count - 1);
        segments[next].IsChecked = true;
        segments[next].Focus(NavigationMethod.Directional);
        e.Handled = true;
    }

    private void OnModelListToggle(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        if (model.IsModelListOpen)
        {
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
        if (Model is { IsModelListOpen: false } model)
        {
            model.OpenModelList();
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
        if (Model is { } model && e.Source is Avalonia.Visual source
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
