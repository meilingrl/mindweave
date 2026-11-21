using Mindweave.Application.Preferences;
using Mindweave.Application.Startup;
using Mindweave.Core.Workspaces;

namespace Mindweave.Desktop.Navigation;

/// <summary>
/// Where the user was, taken in memory just before a language switch replaces the view layer and replayed through the
/// new one (bilingual-ui.md §6.4). It holds identities and typed input only, never a view model or a control.
/// </summary>
/// <param name="Root">What the window showed: the workbench, the guide, the recovery page or the startup page.</param>
/// <param name="Workspaces">The workbench's workspace list, in order.</param>
/// <param name="Active">The workspace shown; null when no workspace is open.</param>
/// <param name="Routes">Each visited workspace's section and page stacks.</param>
/// <param name="Sidebar">The sidebar's width and whether it was collapsed; null before the workbench showed.</param>
public sealed record ViewSnapshot(
    RootPage Root,
    IReadOnlyList<Workspace> Workspaces,
    WorkspaceId? Active,
    IReadOnlyDictionary<WorkspaceId, WorkspaceRoutes> Routes,
    SidebarLayout? Sidebar);

/// <summary>One workspace's place: the section shown and the page stack of each section, section home first.</summary>
public sealed record WorkspaceRoutes(MainSection Section, IReadOnlyDictionary<MainSection, IReadOnlyList<PageSnapshot>> Stacks);

/// <summary>
/// One page of a stack: its route, and what it carries over that lives only in the page (see
/// <see cref="ICarriesViewState"/>). A conversation's <see cref="ConversationRoute.SourceOf"/> is left out, so the
/// replayed page opens at its own reading position instead of jumping to the branch's passage again.
/// </summary>
public sealed record PageSnapshot(ContentRoute Route, object? State);

/// <summary>What the window's root showed when the snapshot was taken.</summary>
public abstract record RootPage
{
    private RootPage()
    {
    }

    /// <summary>The startup page: opening is still under way.</summary>
    public sealed record Starting : RootPage;

    /// <summary>The workbench.</summary>
    public sealed record Workbench : RootPage;

    /// <summary>
    /// The guide or the first-workspace step. <paramref name="Step"/> <see cref="OnboardingStep.Done"/> means the
    /// workspace step alone, without the guide.
    /// </summary>
    public sealed record Onboarding(OnboardingStep Step, string WorkspaceName, object? Model) : RootPage;

    /// <summary>The recovery page, with what it explains.</summary>
    public sealed record Recovery(DatabaseOpenResult Result, IReadOnlyList<BackupInfo> Backups) : RootPage;
}

/// <summary>
/// A page with state that lives only in it, such as unsaved input, an edit mode or a scroll position. A language switch
/// takes it before the old page goes and gives it to the page built in its place. Text a service already keeps (drafts,
/// reading positions, summary drafts) is not carried: the new page reads it from the service.
/// </summary>
public interface ICarriesViewState
{
    /// <summary>What to carry, or null when there is nothing.</summary>
    object? CaptureViewState();

    /// <summary>Takes what the old page carried; called right after the page is built.</summary>
    void RestoreViewState(object state);
}
