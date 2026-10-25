using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Storage;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Navigation;

/// <summary>
/// Owner of all workbench runtime navigation state: the workspace list, one <see cref="WorkspaceRuntime"/>
/// per visited workspace, and the active content stack. Implements the menu and content navigators so
/// view models navigate through contracts rather than touching each other.
/// </summary>
public sealed class Workbench : ObservableObject, IMenuNavigator, IContentNavigator
{
    private readonly Dictionary<WorkspaceId, WorkspaceRuntime> _runtimes = [];
    private readonly IPageFactory _pages;
    private readonly WorkspaceService _workspaces;
    private WorkspaceRuntime? _active;

    public Workbench(IPageFactory pages, WorkspaceService workspaces)
    {
        _pages = pages;
        _workspaces = workspaces;
    }

    public ObservableCollection<Workspace> Workspaces { get; } = [];

    public Workspace? CurrentWorkspace => _active?.Workspace;

    public MainSection CurrentSection => _active?.Section ?? MainSection.Explore;

    public PageViewModel? CurrentPage => _active?.Top;

    public bool CanGoBack => _active is { CurrentStack.Count: > 1 };

    /// <summary>The most recent last-page write; tests await it, the UI never does.</summary>
    internal Task LastLocationSave { get; private set; } = Task.CompletedTask;

    /// <summary>Loads the workspace list and opens the last valid page, or the first workspace's explore home.</summary>
    public void Initialize(IReadOnlyList<Workspace> workspaces, PageLocation? last)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        if (workspaces.Count == 0)
        {
            throw new ArgumentException("The workbench needs at least one workspace.", nameof(workspaces));
        }

        Workspaces.Clear();
        foreach (var workspace in workspaces)
        {
            Workspaces.Add(workspace);
        }

        var target = last is not null && workspaces.Any(w => w.Id == last.WorkspaceId) ? last.WorkspaceId : workspaces[0].Id;
        Activate(target, last?.WorkspaceId == target ? MainSectionKeys.FromKey(last.SectionKey) : null);
    }

    public void AddWorkspace(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Workspaces.Add(workspace);
        Activate(workspace.Id, MainSection.Explore);
    }

    /// <summary>Switches workspace and restores where that workspace was left in this session.</summary>
    public void SwitchWorkspace(WorkspaceId id)
    {
        if (_active?.Workspace.Id == id)
        {
            return;
        }

        Activate(id, section: null);
    }

    public void Navigate(NavigationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runtime = RequireActive();
        runtime.Section = request.Section;
        EnsureHome(runtime);
        if (request.Content is not null)
        {
            PushCore(runtime, request.Content);
        }

        Changed();
    }

    public void Push(ContentRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        PushCore(RequireActive(), route);
        Changed();
    }

    /// <summary>Back within the current section's history. The section home is never popped.</summary>
    public void GoBack()
    {
        var stack = RequireActive().StackOf(RequireActive().Section);
        if (stack.Count <= 1)
        {
            return;
        }

        var leaving = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        (leaving as IDisposable)?.Dispose();
        Changed();
    }

    private void Activate(WorkspaceId id, MainSection? section)
    {
        var workspace = Workspaces.First(w => w.Id == id);
        if (!_runtimes.TryGetValue(id, out var runtime))
        {
            runtime = new WorkspaceRuntime(workspace);
            _runtimes[id] = runtime;
        }

        if (section is { } explicitSection)
        {
            runtime.Section = explicitSection;
        }

        _active = runtime;
        EnsureHome(runtime);
        OnPropertyChanged(nameof(CurrentWorkspace));
        Changed();
    }

    private void EnsureHome(WorkspaceRuntime runtime)
    {
        var stack = runtime.StackOf(runtime.Section);
        if (stack.Count == 0)
        {
            stack.Add(_pages.Create(ContentRoute.HomeOf(runtime.Section), runtime.Workspace, this));
        }
    }

    private void PushCore(WorkspaceRuntime runtime, ContentRoute route)
    {
        var stack = runtime.StackOf(runtime.Section);
        if (stack.Count > 0 && stack[^1].Route == route)
        {
            return;
        }

        stack.Add(_pages.Create(route, runtime.Workspace, this));
    }

    private WorkspaceRuntime RequireActive() =>
        _active ?? throw new InvalidOperationException("No workspace is active.");

    private void Changed()
    {
        OnPropertyChanged(nameof(CurrentSection));
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(CanGoBack));
        if (_active is { } runtime)
        {
            // Only the section is restorable in S0; content pages gain stable keys with conversations (S1).
            LastLocationSave = _workspaces.RememberLocationAsync(
                new PageLocation(runtime.Workspace.Id, runtime.Section.ToKey(), ContentKey: null),
                CancellationToken.None);
        }
    }
}
