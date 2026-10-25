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

/// <summary>Content level: detail pages pushed within the current section, and back.</summary>
public interface IContentNavigator
{
    bool CanGoBack { get; }

    void Push(ContentRoute route);

    void GoBack();
}
