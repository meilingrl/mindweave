using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels;

/// <summary>
/// New answers in the workspaces that are not open (2026-11-10). The sidebar marks conversations of the open workspace
/// only; the workspace picker shows a dot beside each other workspace with a new answer, and its closed box one dot with
/// their names. The open workspace never counts here: its own sidebar already shows its marks.
/// </summary>
public sealed partial class WorkspaceNewsViewModel : ObservableObject
{
    private readonly Workbench _workbench;
    private readonly IConversationMarks _marks;
    private readonly SerialRefresh _load;
    private readonly UiThrottle _reload;

    public WorkspaceNewsViewModel(Workbench workbench, IConversationMarks marks, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        ArgumentNullException.ThrowIfNull(marks);
        _workbench = workbench;
        _marks = marks;
        _load = new SerialRefresh(RefreshAsync);
        _reload = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(100), () => Loaded = _load.Request());
        _marks.Changed += (_, _) => _reload.Request();
        _workbench.Workspaces.CollectionChanged += OnWorkspacesChanged;
        _workbench.PropertyChanged += OnWorkbenchChanged;
        Loaded = _load.Request();
    }

    /// <summary>The workspaces other than the open one with a new answer; replaced, never changed in place, so bindings see it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNews), nameof(Hint))]
    public partial IReadOnlySet<WorkspaceId> Workspaces { get; private set; } = new HashSet<WorkspaceId>();

    public bool HasNews => Workspaces.Count > 0;

    /// <summary>The closed picker's tooltip and automation help: which workspaces have a new answer, in the picker's order.</summary>
    public string? Hint => HasNews
        ? UiText.OtherWorkspacesNews(_workbench.Workspaces.Where(w => Workspaces.Contains(w.Id)).Select(w => w.Name.Value))
        : null;

    /// <summary>Completes when the newest refresh has run (tests).</summary>
    public Task Loaded { get; private set; }

    private void OnWorkspacesChanged(object? sender, NotifyCollectionChangedEventArgs e) => _reload.Request();

    private void OnWorkbenchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Workbench.CurrentWorkspace))
        {
            _reload.Request();
        }
    }

    private async Task RefreshAsync()
    {
        var current = _workbench.CurrentWorkspace?.Id;
        WorkspaceId[] others = [.. _workbench.Workspaces.Select(w => w.Id).Where(id => id != current)];
        IReadOnlySet<WorkspaceId> found;
        try
        {
            found = others.Length == 0 ? new HashSet<WorkspaceId>() : await _marks.WorkspacesWithUnreadAsync(others, CancellationToken.None);
        }
#pragma warning disable CA1031 // A hint is never worth an error: keep the last one shown.
        catch (Exception)
#pragma warning restore CA1031
        {
            return;
        }

        // The open workspace may have changed while reading: it never gets the hint.
        var open = _workbench.CurrentWorkspace?.Id;
        var shown = found.Where(id => id != open).ToHashSet();
        if (!shown.SetEquals(Workspaces))
        {
            Workspaces = shown;
        }
        else
        {
            OnPropertyChanged(nameof(Hint));
        }
    }
}
