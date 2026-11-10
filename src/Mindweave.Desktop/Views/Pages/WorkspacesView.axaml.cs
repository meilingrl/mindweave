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
        }

        _page = page;
        _shownOnce = false;
        if (page is not null)
        {
            page.RenameStarted += OnRenameStarted;
            page.RenameEnded += OnRenameEnded;
            page.CreateStarted += OnCreateStarted;
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

    /// <summary>Back to the row's 改名, so the keyboard user goes on from where they were (only if the box had the focus).</summary>
    private void OnRenameEnded(object? sender, WorkspaceRowViewModel row)
    {
        var box = RowPart<TextBox>(row, "WorkspaceRenameBox");
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        var hadFocus = box is { IsKeyboardFocusWithin: true }
            || (focused is not null && RowPart<Control>(row, "WorkspaceRenameActions") is { } actions && actions.IsVisualAncestorOf(focused));
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
            row.CancelRenameCommand.Execute(null);
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
