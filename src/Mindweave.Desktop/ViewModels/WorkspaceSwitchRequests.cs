using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Core.Workspaces;

namespace Mindweave.Desktop.ViewModels;

/// <summary>
/// Asks for another workspace to be opened, the way the switcher opens it (an unsaved network layout is asked about
/// first). Pages hold this, not the shell, so the page factory has no cycle.
/// </summary>
public interface IWorkspaceSwitcher
{
    void SwitchTo(Workspace workspace);
}

/// <summary>Hands a switch request from a page to the shell that is listening (2026-11-10).</summary>
public sealed class WorkspaceSwitchRequests : IWorkspaceSwitcher
{
    public event EventHandler<Workspace>? Requested;

    public void SwitchTo(Workspace workspace) => Requested?.Invoke(this, workspace);
}

/// <summary>
/// One row of the switcher's menu (2026-11-10): a workspace, whether it is the one open (a check mark and semibold
/// text, not a fill), and whether it has a new answer (the accent dot). The row's own command chooses it, so the menu,
/// which floats outside the sidebar's tree, needs nothing from its ancestors.
/// </summary>
public sealed partial class WorkspaceChoice(Workspace workspace, bool isCurrent, bool hasNews, ICommand choose) : ObservableObject
{
    public Workspace Workspace { get; } = workspace;

    public bool IsCurrent { get; } = isCurrent;

    public ICommand Choose { get; } = choose;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial bool HasNews { get; internal set; } = hasNews;

    /// <summary>The picker entry's name as before: the workspace's name, and that it has a new answer when it does.</summary>
    public string AutomationName => HasNews ? UiText.WorkspaceWithNews(Workspace.Name.Value) : Workspace.Name.Value;

    /// <summary>Read after the name by a screen reader: the check mark's meaning.</summary>
    public string? CurrentHint => IsCurrent ? UiText.CurrentWorkspaceHint : null;
}
