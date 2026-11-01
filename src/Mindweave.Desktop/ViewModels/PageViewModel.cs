using Mindweave.Desktop.Navigation;

namespace Mindweave.Desktop.ViewModels;

/// <summary>A page shown in the workbench content area, identified by its route.</summary>
public abstract class PageViewModel(ContentRoute route) : ViewModelBase
{
    public ContentRoute Route { get; } = route;

    public abstract string Title { get; }
}
