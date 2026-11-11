using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Views.Pages;

/// <summary>设置 › 工作区: the view moves the focus for the inline rename and the new-workspace form; nothing else.</summary>
public sealed partial class WorkspacesView : UserControl
{
    private WorkspacesViewModel? _page;
    private bool _shownOnce;

    public WorkspacesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Watch(DataContext as WorkspacesViewModel);
    }

    private void Watch(WorkspacesViewModel? page)
    {
        if (ReferenceEquals(page, _page))
        {
            return;
        }

        if (_page is not null)
        {
            _page.RenameStarted -= OnRenameStarted;
            _page.RenameEnded -= OnRenameEnded;
            _page.CreateStarted -= OnCreateStarted;
            _page.SuggestionPicked -= OnSuggestionPicked;
            _page.SuggestionStateChanged -= OnSuggestionStateChanged;
        }

        _page = page;
        _shownOnce = false;
        if (page is not null)
        {
            page.RenameStarted += OnRenameStarted;
            page.RenameEnded += OnRenameEnded;
            page.CreateStarted += OnCreateStarted;
            page.SuggestionPicked += OnSuggestionPicked;
            page.SuggestionStateChanged += OnSuggestionStateChanged;
        }
    }

    /// <summary>Shown again (back from another page or section): the counts are read again, the old ones stay meanwhile.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_shownOnce)
        {
            _page?.Refresh();
        }

        _shownOnce = true;
    }

    /// <summary>Leaving the page stops any request for suggested names: a late answer would show where no one is looking.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _page?.StopAllSuggestions();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>A picked name is in the box: the focus goes back there, the caret after the name, ready for Enter or more typing.</summary>
    private void OnSuggestionPicked(object? sender, WorkspaceRowViewModel row) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (row.IsRenaming && RowPart<TextBox>(row, "WorkspaceRenameBox") is { } box)
                {
                    box.Focus(NavigationMethod.Pointer);
                    box.ClearSelection();
                    box.CaretIndex = box.Text?.Length ?? 0;
                }
            },
            DispatcherPriority.Background);

    /// <summary>
    /// The keyboard follows the suggestions when it was on them (建议名称 or 换一批, 停止, a name, 重试), or was dropped
    /// because the button it was on turned unavailable: to 停止 while asking, the box once names arrive (the first is
    /// offered there; the first chip when 更多建议 is open), 重试 or 前往设置 when there are none, and back to the button when
    /// stopped. Someone typing in the box keeps typing.
    /// </summary>
    private void OnSuggestionStateChanged(object? sender, WorkspaceRowViewModel row)
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        var button = RowPart<Button>(row, "SuggestNamesButton");
        var panel = RowPart<Control>(row, "SuggestionPanel");
        var follow = focused is null
            || (button is not null && (ReferenceEquals(button, focused) || button.IsVisualAncestorOf(focused)))
            || (panel is not null && panel.IsVisualAncestorOf(focused));
        if (!follow)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (!row.IsRenaming)
                {
                    return;
                }

                Control? target = row.SuggestionState switch
                {
                    NameSuggestionState.Generating => RowPart<Button>(row, "StopSuggestingButton"),
                    // New names are offered in the box (Tab takes one), or, with 更多建议 open, as chips: the names' own
                    // data context is the name, not the row, so they are found inside the row's list.
                    NameSuggestionState.Shown when row.ShowsChips => RowPart<ItemsControl>(row, "SuggestionChips")?.GetVisualDescendants().OfType<Button>().FirstOrDefault(),
                    NameSuggestionState.Shown => RowPart<TextBox>(row, "WorkspaceRenameBox"),
                    NameSuggestionState.Failed => new[] { RowPart<Button>(row, "RetrySuggestButton"), RowPart<Button>(row, "SuggestSettingsButton") }
                        .FirstOrDefault(b => b is { IsEffectivelyVisible: true }),
                    _ => RowPart<Button>(row, "SuggestNamesButton") is { IsEffectivelyEnabled: true } again ? again : null,
                };
                (target ?? RowPart<TextBox>(row, "WorkspaceRenameBox"))?.Focus(NavigationMethod.Tab);
            },
            DispatcherPriority.Background);
    }

    /// <summary>Esc anywhere in a row being renamed: stops a request for names first, then a second Esc ends the rename.</summary>
    private void OnRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || (sender as Control)?.DataContext is not WorkspaceRowViewModel { IsRenaming: true } row)
        {
            return;
        }

        if (row.IsSuggesting)
        {
            row.StopSuggestingNamesCommand.Execute(null);
        }
        else
        {
            row.CancelRenameCommand.Execute(null);
        }

        e.Handled = true;
    }

    private void OnCreateStarted(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => NewWorkspaceNameOnPage.Focus(NavigationMethod.Tab), DispatcherPriority.Background);

    /// <summary>The box takes the focus with the old name selected: typing replaces it.</summary>
    private void OnRenameStarted(object? sender, WorkspaceRowViewModel row) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (row.IsRenaming && RowPart<TextBox>(row, "WorkspaceRenameBox") is { } box)
                {
                    // Focused as by Tab, the box selects its whole text.
                    box.Focus(NavigationMethod.Tab);
                }
            },
            DispatcherPriority.Background);

    /// <summary>
    /// Back to the row's 改名, so the keyboard user goes on from where they were (only if the box, the rename's actions
    /// or the suggested names under the box had the focus: all of them disappear with the rename).
    /// </summary>
    private void OnRenameEnded(object? sender, WorkspaceRowViewModel row)
    {
        var box = RowPart<TextBox>(row, "WorkspaceRenameBox");
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        var hadFocus = box is { IsKeyboardFocusWithin: true }
            || (focused is not null && RowPart<Control>(row, "WorkspaceRenameActions") is { } actions && actions.IsVisualAncestorOf(focused))
            || (focused is not null && RowPart<Control>(row, "SuggestionPanel") is { } suggestions && suggestions.IsVisualAncestorOf(focused))
            || (focused is not null && RowPart<Control>(row, "GhostNamesHintLine") is { } hint && hint.IsVisualAncestorOf(focused));
        if (hadFocus)
        {
            Dispatcher.UIThread.Post(() => RowPart<Button>(row, "RenameWorkspaceButton")?.Focus(NavigationMethod.Tab), DispatcherPriority.Background);
        }
    }

    /// <summary>Enter saves and Esc cancels; both stop here.</summary>
    private void OnRenameBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if ((sender as Control)?.DataContext is not WorkspaceRowViewModel row || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            row.CommitRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // A running request for names is stopped first; the rename ends on the next Esc.
            if (row.IsSuggesting)
            {
                row.StopSuggestingNamesCommand.Execute(null);
            }
            else
            {
                row.CancelRenameCommand.Execute(null);
            }

            e.Handled = true;
        }
    }

    /// <summary>Enter creates, Esc cancels and returns to 新建工作区….</summary>
    private void OnNewWorkspaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (_page is null || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (_page.NewWorkspace.SubmitCommand.CanExecute(null))
            {
                _page.NewWorkspace.SubmitCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _page.CancelCreateCommand.Execute(null);
            Dispatcher.UIThread.Post(() => NewWorkspaceOnPage.Focus(NavigationMethod.Tab), DispatcherPriority.Background);
            e.Handled = true;
        }
    }

    private T? RowPart<T>(WorkspaceRowViewModel row, string name)
        where T : Control =>
        WorkspaceRows.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name && ReferenceEquals(c.DataContext, row));
}
