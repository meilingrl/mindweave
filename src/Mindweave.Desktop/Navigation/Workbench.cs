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
public sealed class Workbench : ObservableObject, IWorkbenchNavigator, IWorkspaceDirectory
{
    private readonly Dictionary<WorkspaceId, WorkspaceRuntime> _runtimes = [];
    private readonly IPageFactory _pages;
    private readonly IWorkspaces _workspaces;
    private WorkspaceRuntime? _active;
    private bool _released;

    public Workbench(IPageFactory pages, IWorkspaces workspaces)
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

    /// <summary>The workspaces that have pages alive in this process; tests check a deleted one is not among them.</summary>
    internal IReadOnlyCollection<WorkspaceId> OpenWorkspaces => _runtimes.Keys;

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
            Navigate(new NavigationRequest(MainSection.SummaryNetwork, summary));
        }
        else if (restoring && last!.ContentKey == ArchivedRoute.RouteKey)
        {
            Navigate(new NavigationRequest(MainSection.Explore, ArchivedRoute.Instance));
        }
    }

    public void AddWorkspace(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (_released)
        {
            return;
        }

        Workspaces.Add(workspace);
        Activate(workspace.Id, MainSection.Explore);
    }

    /// <summary>Switches workspace and restores where that workspace was left in this session.</summary>
    public void SwitchWorkspace(WorkspaceId id)
    {
        if (_released || _active?.Workspace.Id == id)
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
        if (_released || (request.From is { } from && !ReferenceEquals(from, CurrentPage)))
        {
            return;
        }

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
        if (_released)
        {
            return;
        }

        var (runtime, slot) = RequireActive(null);
        PushCore(runtime, slot, route);
        Changed();
    }

    public void Replace(PageViewModel page, ContentRoute route)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(route);
        if (_released)
        {
            return;
        }

        foreach (var runtime in _runtimes.Values)
        {
            foreach (var slot in runtime.Slots)
            {
                // Index 0 is the section home, which is never replaced.
                if (slot.Stacks.FirstOrDefault(s => s.IndexOf(page) > 0) is { } stack)
                {
                    stack[stack.IndexOf(page)] = _pages.Create(route, new PageContext(runtime.Workspace, slot.Key, this, this));
                    Release(page);
                    Changed();
                    return;
                }
            }
        }
    }

    public void Forget(Func<ContentRoute, bool> match, ContentRoute? fallback = null) => Forget(match, _ => fallback);

    public void Forget(Func<ContentRoute, bool> match, Func<ContentRoute, ContentRoute?> fallbackFor)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(fallbackFor);
        if (_released)
        {
            return;
        }

        var shown = CurrentPage;
        var fallback = shown is not null && match(shown.Route) ? fallbackFor(shown.Route) : null;
        var shownForgotten = false;
        foreach (var stack in _runtimes.Values.SelectMany(r => r.Slots).SelectMany(s => s.Stacks))
        {
            // Index 0 is the section home, which never matches content that can disappear.
            for (var i = stack.Count - 1; i >= 1; i--)
            {
                if (match(stack[i].Route))
                {
                    var page = stack[i];
                    shownForgotten |= ReferenceEquals(page, shown);
                    stack.RemoveAt(i);
                    Release(page);
                }
            }
        }

        if (shownForgotten && _active is { } runtime && ActiveSlot is { } slot)
        {
            // A fallback opens in Explore; without one the reader stays in the section they were in (review 2026-11-14).
            if (fallback is not null)
            {
                slot.Section = MainSection.Explore;
            }

            EnsureHome(runtime, slot);
            // Without a fallback the reader lands on the page before it: the forgotten pages are gone from every stack
            // already, and the rest of the history stays (low finding gap-lifecycle-6: it used to be emptied too).
            if (fallback is not null)
            {
                PushCore(runtime, slot, fallback);
            }
        }

        Changed();
    }

    /// <summary>
    /// A deleted workspace leaves the workbench (S7 round 4): it goes from the list, and every page it had, in every slot
    /// and section, is removed and disposed, so no back history points into it. If it was the active one,
    /// <paramref name="next"/> opens where it was left in this session (its explore home otherwise); with no
    /// <paramref name="next"/> nothing is active any more and the caller shows the first-workspace flow.
    /// </summary>
    public void RemoveWorkspace(WorkspaceId removed, WorkspaceId? next)
    {
        if (_released)
        {
            return;
        }

        if (_runtimes.Remove(removed, out var runtime))
        {
            foreach (var page in runtime.AllPages().ToList())
            {
                Release(page);
            }
        }

        if (Workspaces.FirstOrDefault(w => w.Id == removed) is { } listed)
        {
            Workspaces.Remove(listed);
        }

        if (_active?.Workspace.Id != removed)
        {
            return;
        }

        _active = null;
        var target = next is { } id && Workspaces.Any(w => w.Id == id) ? id : Workspaces.FirstOrDefault()?.Id;
        if (target is { } open)
        {
            Activate(open, section: null);
            return;
        }

        OnPropertyChanged(nameof(CurrentWorkspace));
        Changed();
    }

    /// <summary>
    /// A renamed workspace (2026-11-10) takes its place in the list (a replacement, so the switcher and the new-answer
    /// hints follow), its runtime holds the new one, and its pages that show the name are told.
    /// </summary>
    public void RenameWorkspace(Workspace renamed)
    {
        ArgumentNullException.ThrowIfNull(renamed);
        var index = Workspaces.ToList().FindIndex(w => w.Id == renamed.Id);
        if (index < 0)
        {
            return;
        }

        Workspaces[index] = renamed;
        if (_runtimes.TryGetValue(renamed.Id, out var runtime))
        {
            runtime.Workspace = renamed;
            foreach (var page in runtime.AllPages().OfType<IShowsWorkspaceName>())
            {
                page.WorkspaceRenamed(renamed);
            }
        }

        if (_active?.Workspace.Id == renamed.Id)
        {
            OnPropertyChanged(nameof(CurrentWorkspace));
        }
    }

    /// <summary>Back within the current section's history. The section home is never popped.</summary>
    public void GoBack()
    {
        if (_released)
        {
            return;
        }

        var (_, slot) = RequireActive(null);
        var stack = slot.StackOf(slot.Section);
        if (stack.Count <= 1)
        {
            return;
        }

        var leaving = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        Release(leaving);
        Changed();
    }

    /// <summary>
    /// Exit step 4 for navigation state: the last location is written once more. Page input is not held here: drafts
    /// are owned by their application services, which the exit flushes (architecture review F2).
    /// </summary>
    public async Task FlushAsync()
    {
        if (_active is { } runtime && ActiveSlot is { } slot)
        {
            await _workspaces.RememberLocationAsync(
                new PageLocation(runtime.Workspace.Id, slot.Section.ToKey(), (slot.Top?.Route as IRestorableRoute)?.Key),
                CancellationToken.None);
        }
    }

    /// <summary>
    /// The view ends (an in-app restore, 2026-11-12, or a language switch): every page of every workspace leaves the
    /// stacks and is disposed. The window already shows the next view, so nothing is navigated to; navigation that comes
    /// later, from work an old page started (a send that finishes), is ignored (review of merge 2, M2). Every page is
    /// released even if one fails to let go; the first failure is thrown afterwards.
    /// </summary>
    public void ReleaseAll()
    {
        _released = true;
        var pages = _runtimes.Values.SelectMany(r => r.AllPages()).ToList();
        _runtimes.Clear();
        _active = null;
        List<Exception>? failures = null;
        foreach (var page in pages)
        {
            try
            {
                Release(page);
            }
#pragma warning disable CA1031 // The others still go; the failures are thrown together below.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("Some pages failed to let go of their view.", failures);
        }
    }

    /// <summary>The pages alive in this workbench, in every workspace, slot and section.</summary>
    internal IEnumerable<PageViewModel> AllPages() => _runtimes.Values.SelectMany(r => r.AllPages());

    /// <summary>
    /// Where the user is, for a language switch (bilingual-ui.md §6.4): the workspace list, the active workspace, and
    /// each visited workspace's section and stacks, with what each page carries. The root page and the sidebar are
    /// left for the caller to fill in.
    /// </summary>
    public ViewSnapshot Snapshot()
    {
        var routes = new Dictionary<WorkspaceId, WorkspaceRoutes>();
        foreach (var (id, runtime) in _runtimes)
        {
            var slot = runtime.Slot(CurrentSlot);
            var stacks = Enum.GetValues<MainSection>()
                .Select(section => (Section: section, Stack: slot.StackOf(section)))
                .Where(s => s.Stack.Count > 0)
                .ToDictionary(
                    s => s.Section,
                    s => (IReadOnlyList<PageSnapshot>)[.. s.Stack.Select(page => new PageSnapshot(
                        page.Route is ConversationRoute conversation ? conversation with { SourceOf = null } : page.Route,
                        (page as ICarriesViewState)?.CaptureViewState()))]);
            routes[id] = new WorkspaceRoutes(slot.Section, stacks);
        }

        return new ViewSnapshot(new RootPage.Shell(), [.. Workspaces], _active?.Workspace.Id, routes, Sidebar: null);
    }

    /// <summary>
    /// Builds the pages of <paramref name="snapshot"/> in this (new, empty) workbench, each with what its old page
    /// carried, and shows the workspace that was active. It is the first thing a new view's workbench does, in place of
    /// <see cref="Initialize"/>.
    /// </summary>
    public void Restore(ViewSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Workspaces.Count == 0)
        {
            throw new ArgumentException("The workbench needs at least one workspace.", nameof(snapshot));
        }

        Workspaces.Clear();
        foreach (var workspace in snapshot.Workspaces)
        {
            Workspaces.Add(workspace);
        }

        foreach (var (id, routes) in snapshot.Routes)
        {
            if (Workspaces.FirstOrDefault(w => w.Id == id) is not { } workspace)
            {
                continue;
            }

            var runtime = new WorkspaceRuntime(workspace);
            _runtimes[id] = runtime;
            var slot = runtime.Slot(CurrentSlot);
            slot.Section = routes.Section;
            foreach (var (section, pages) in routes.Stacks)
            {
                var stack = slot.StackOf(section);
                foreach (var saved in pages)
                {
                    // Replayed, not opened: the page starts nothing that opening it would (review of merge 2, M4).
                    var page = _pages.Create(saved.Route, new PageContext(runtime.Workspace, slot.Key, this, this, Replayed: true));
                    try
                    {
                        if (saved.State is { } state && page is ICarriesViewState carrier)
                        {
                            carrier.RestoreViewState(state);
                        }
                    }
                    catch
                    {
                        Release(page);
                        throw;
                    }

                    stack.Add(page);
                }
            }
        }

        var active = snapshot.Active is { } a && _runtimes.ContainsKey(a) ? a : Workspaces[0].Id;
        Activate(active, section: null);
    }

    /// <summary>A page leaves the stacks and is disposed; what the user typed is already with its owner.</summary>
    private static void Release(PageViewModel page) => (page as IDisposable)?.Dispose();


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
            stack.Add(_pages.Create(ContentRoute.HomeOf(slot.Section), new PageContext(runtime.Workspace, slot.Key, this, this)));
        }
    }

    private void PushCore(WorkspaceRuntime runtime, ViewerSlot slot, ContentRoute route)
    {
        var stack = slot.StackOf(slot.Section);
        if (stack.Count > 0 && stack[^1].Route == route)
        {
            return;
        }

        stack.Add(_pages.Create(route, new PageContext(runtime.Workspace, slot.Key, this, this)));
        if (stack.Count > MaxHistory)
        {
            // Keep the section home; drop the oldest detail page.
            var dropped = stack[1];
            stack.RemoveAt(1);
            Release(dropped);
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
