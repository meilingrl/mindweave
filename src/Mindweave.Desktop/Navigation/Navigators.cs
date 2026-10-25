using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Navigation;

/// <summary>Root level: startup, recovery, onboarding or the workbench.</summary>
public interface IRootNavigator
{
    ViewModelBase? Current { get; }

    void Show(ViewModelBase page);
}

/// <summary>Menu level: switching between the workbench's main sections.</summary>
public interface IMenuNavigator
{
    MainSection CurrentSection { get; }

    void Navigate(NavigationRequest request);
}

/// <summary>Both workbench levels, as handed to pages that may open other sections.</summary>
public interface IWorkbenchNavigator : IMenuNavigator, IContentNavigator;

/// <summary>Content level: detail pages pushed within the current section, and back.</summary>
public interface IContentNavigator
{
    bool CanGoBack { get; }

    void Push(ContentRoute route);

    /// <summary>Replaces the current page without growing history (e.g. a new conversation once it exists).</summary>
    void Replace(ContentRoute route);

    void GoBack();
}
