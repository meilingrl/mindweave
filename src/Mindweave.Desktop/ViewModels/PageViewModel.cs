using Mindweave.Desktop.Navigation;

namespace Mindweave.Desktop.ViewModels;

/// <summary>A page shown in the workbench content area, identified by its route.</summary>
public abstract class PageViewModel(ContentRoute route) : ViewModelBase
{
    public ContentRoute Route { get; } = route;

    public abstract string Title { get; }
}

/// <summary>
/// A page holding user input it saves on its own schedule (for example a throttled draft). The workbench asks it to
/// save now when the page is dropped and when the application exits.
/// </summary>
public interface IPageFlush
{
    /// <summary>Saves pending input now. False when it could not be saved; it stays pending for another try.</summary>
    Task<bool> FlushAsync();
}
