using Mindweave.Core.Reading;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Navigation;

/// <summary>Where a page lives: its workspace, the viewer slot showing it, and the navigator it may use.</summary>
public sealed record PageContext(Workspace Workspace, ViewerSlotKey Slot, IWorkbenchNavigator Navigator);

public interface IPageFactory
{
    PageViewModel Create(ContentRoute route, PageContext context);
}
