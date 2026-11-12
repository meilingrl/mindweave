using Avalonia.Controls;

namespace Mindweave.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private ViewModels.WorkspaceDeletionViewModel? _deletion;
    private ViewModels.ConversationDeletionViewModel? _conversationDeletion;
    private ViewModels.BackupRestoreViewModel? _backupRestore;
    private ViewModels.BackupDeletionViewModel? _backupDeletion;

    public MainWindow()
    {
        InitializeComponent();
        WorkspaceDeleteName.AddHandler(KeyDownEvent, OnDeleteNameKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Enter in the name box finishes the typed confirmation: it deletes once the name matches and otherwise does
    /// nothing, so the default 取消 applies only with focus elsewhere and never throws the typed name away (round 4 review).
    /// </summary>
    private void OnDeleteNameKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter || e.KeyModifiers != Avalonia.Input.KeyModifiers.None)
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
    /// Shows whichever session is current: an in-app restore (2026-11-12) replaces the services, and with them the root
    /// this window shows and closes through.
    /// </summary>
    internal void Follow(AppSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Replaced += (_, _) => DataContext = session.Root;
    }

    /// <summary>The delete-a-workspace dialog puts the caret in its name box when it opens (Round 4 · topic-delete).</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_deletion is not null)
        {
            _deletion.PropertyChanged -= OnDeletionChanged;
        }

        _deletion = (DataContext as ViewModels.RootViewModel)?.WorkspaceDeletion;
        if (_deletion is not null)
        {
            _deletion.PropertyChanged += OnDeletionChanged;
        }

        if (_conversationDeletion is not null)
        {
            _conversationDeletion.PropertyChanged -= OnConversationDeletionChanged;
        }

        _conversationDeletion = (DataContext as ViewModels.RootViewModel)?.ConversationDeletion;
        if (_conversationDeletion is not null)
        {
            _conversationDeletion.PropertyChanged += OnConversationDeletionChanged;
        }

        if (_backupRestore is not null)
        {
            _backupRestore.PropertyChanged -= OnBackupDialogChanged;
        }

        _backupRestore = (DataContext as ViewModels.RootViewModel)?.BackupRestore;
        if (_backupRestore is not null)
        {
            _backupRestore.PropertyChanged += OnBackupDialogChanged;
        }

        if (_backupDeletion is not null)
        {
            _backupDeletion.PropertyChanged -= OnBackupDialogChanged;
        }

        _backupDeletion = (DataContext as ViewModels.RootViewModel)?.BackupDeletion;
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

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        // Closing the window is one way of quitting: the exit coordinator asks, stops and saves, then shuts down
        // itself; only that final shutdown passes here.
        if (DataContext is ViewModels.RootViewModel root && root.Exit.InterceptClose())
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Called when a second launch asks this instance to come forward. Ignored once the exit is releasing: the window
    /// is closing or closed, and this instance only still holds the single-instance lock while it shuts down.
    /// </summary>
    public void BringToFront()
    {
        if (DataContext is ViewModels.RootViewModel { Exit.Stage: ViewModels.ExitStage.Releasing })
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
    }
}
