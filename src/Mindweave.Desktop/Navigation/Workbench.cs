using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Storage;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Reading;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Navigation;

/// <summary>
/// Owner of all workbench runtime navigation state: the workspace list, one <see cref="WorkspaceRuntime"/>
/// per visited workspace with its viewer slots, and the active slot's content stacks. Implements the menu and content navigators so
/// view models navigate through contracts rather than touching each other.
/// </summary>
public sealed class Workbench : ObservableObject, IWorkbenchNavigator
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

    /// <summary>The slot pages are shown in. The first version only ever has the primary slot.</summary>
    public ViewerSlotKey CurrentSlot { get; } = ViewerSlotKey.Primary;

    public MainSection CurrentSection => ActiveSlot?.Section ?? MainSection.Explore;

    public PageViewModel? CurrentPage => ActiveSlot?.Top;

    public bool CanGoBack => ActiveSlot is { CurrentStack.Count: > 1 };

    private ViewerSlot? ActiveSlot => _active?.Slot(CurrentSlot);

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
        var restoring = last?.WorkspaceId == target;
        Activate(target, restoring ? MainSectionKeys.FromKey(last!.SectionKey) : null);
        // Last valid page only; the back history before it is intentionally not restored. A page whose content no
        // longer exists says so itself and offers the way back.
        if (restoring && ConversationRoute.FromKey(last!.ContentKey) is { } page)
        {
            Navigate(new NavigationRequest(MainSection.Explore, page));
        }
        else if (restoring && SummaryRoute.FromKey(last!.ContentKey) is { } summary)
        {
            Navigate(new NavigationRequest(MainSection.Network, summary));
        }
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

    /// <summary>
    /// Opens a section and optionally content in it. <see cref="NavigationIntent.ReturnToSource"/> always opens a
    /// new page — it follows the branch relation, not the back history — so back returns to the branch.
    /// </summary>
    public void Navigate(NavigationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (runtime, slot) = RequireActive(request.Slot);
        slot.Section = request.Section;
        EnsureHome(runtime, slot);
        if (request.Content is not null)
        {
            PushCore(runtime, slot, request.Content);
        }

        Changed();
    }

    public void Push(ContentRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        var (runtime, slot) = RequireActive(null);
        PushCore(runtime, slot, route);
        Changed();
    }

    public void Replace(ContentRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        var (runtime, slot) = RequireActive(null);
        var stack = slot.StackOf(slot.Section);
        if (stack.Count > 1)
        {
            var leaving = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            (leaving as IDisposable)?.Dispose();
        }

        PushCore(runtime, slot, route);
        Changed();
    }

    /// <summary>Back within the current section's history. The section home is never popped.</summary>
    public void GoBack()
    {
        var (_, slot) = RequireActive(null);
        var stack = slot.StackOf(slot.Section);
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

        var slot = runtime.Slot(CurrentSlot);
        if (section is { } explicitSection)
        {
            slot.Section = explicitSection;
        }

        _active = runtime;
        EnsureHome(runtime, slot);
        OnPropertyChanged(nameof(CurrentWorkspace));
        Changed();
    }

    private void EnsureHome(WorkspaceRuntime runtime, ViewerSlot slot)
    {
        var stack = slot.StackOf(slot.Section);
        if (stack.Count == 0)
        {
            stack.Add(_pages.Create(ContentRoute.HomeOf(slot.Section), new PageContext(runtime.Workspace, slot.Key, this)));
        }
    }

    private void PushCore(WorkspaceRuntime runtime, ViewerSlot slot, ContentRoute route)
    {
        var stack = slot.StackOf(slot.Section);
        if (stack.Count > 0 && stack[^1].Route == route)
        {
            return;
        }

        stack.Add(_pages.Create(route, new PageContext(runtime.Workspace, slot.Key, this)));
        if (stack.Count > MaxHistory)
        {
            // Keep the section home; drop the oldest detail page.
            var dropped = stack[1];
            stack.RemoveAt(1);
            (dropped as IDisposable)?.Dispose();
        }
    }

    /// <summary>Back history per section is bounded so a long session does not keep every visited page alive.</summary>
    internal const int MaxHistory = 50;

    private (WorkspaceRuntime Runtime, ViewerSlot Slot) RequireActive(ViewerSlotKey? slot)
    {
        var runtime = _active ?? throw new InvalidOperationException("No workspace is active.");
        return (runtime, runtime.Slot(slot ?? CurrentSlot));
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(CurrentSection));
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(CanGoBack));
        if (_active is { } runtime && ActiveSlot is { } slot)
        {
            LastLocationSave = _workspaces.RememberLocationAsync(
                new PageLocation(runtime.Workspace.Id, slot.Section.ToKey(), (slot.Top?.Route as IRestorableRoute)?.Key),
                CancellationToken.None);
        }
    }
}
