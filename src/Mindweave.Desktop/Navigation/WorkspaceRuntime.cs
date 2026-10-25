using Mindweave.Core.Workspaces;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Navigation;

/// <summary>
/// Runtime-only state of one workspace: its current section and one content stack per section.
/// Lives as long as the process; survives section and workspace switches; never persisted.
/// </summary>
internal sealed class WorkspaceRuntime(Workspace workspace)
{
    private readonly Dictionary<MainSection, List<PageViewModel>> _stacks = [];

    public Workspace Workspace { get; set; } = workspace;

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
