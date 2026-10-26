using Mindweave.Core.Reading;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Navigation;

/// <summary>
/// Runtime-only state of one workspace: its viewer slots. Lives as long as the process; survives section and
/// workspace switches; never persisted. The first version has exactly one slot (evolution seam E1).
/// </summary>
internal sealed class WorkspaceRuntime(Workspace workspace)
{
    private readonly Dictionary<ViewerSlotKey, ViewerSlot> _slots = [];

    public Workspace Workspace { get; set; } = workspace;

    public ViewerSlot Slot(ViewerSlotKey key)
    {
        if (!_slots.TryGetValue(key, out var slot))
        {
            slot = new ViewerSlot(key);
            _slots[key] = slot;
        }

        return slot;
    }
}

/// <summary>
/// One place content is shown in: its current section and one content stack per section. Reading positions and
/// drafts are kept per slot, so a second side-by-side slot can show the same conversation at another place.
/// </summary>
internal sealed class ViewerSlot(ViewerSlotKey key)
{
    private readonly Dictionary<MainSection, List<PageViewModel>> _stacks = [];

    public ViewerSlotKey Key { get; } = key;

    public MainSection Section { get; set; } = MainSection.Explore;

    public IReadOnlyList<PageViewModel> CurrentStack => StackOf(Section);

    public PageViewModel? Top => CurrentStack.Count == 0 ? null : CurrentStack[^1];

    public List<PageViewModel> StackOf(MainSection section)
    {
        if (!_stacks.TryGetValue(section, out var stack))
        {
            stack = [];
            _stacks[section] = stack;
        }

        return stack;
    }
}
