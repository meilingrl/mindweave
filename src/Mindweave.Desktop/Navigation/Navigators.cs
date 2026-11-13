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
public interface IWorkbenchNavigator : IMenuNavigator, IContentNavigator
{
    /// <summary>
    /// Removes and disposes every page whose route matches, in every workspace, slot and section (content that no
    /// longer exists, e.g. a deleted conversation). If the page being shown was one of them, shows
    /// <paramref name="fallback"/> in the explore section, or the page before it in its own section when it is null.
    /// </summary>
    void Forget(Func<ContentRoute, bool> match, ContentRoute? fallback = null);

    /// <summary>
    /// As <see cref="Forget(Func{ContentRoute, bool}, ContentRoute?)"/>, with the fallback worked out from the route of
    /// the page that was shown (a conversation deleted with its parent's branch lands on that parent).
    /// </summary>
    void Forget(Func<ContentRoute, bool> match, Func<ContentRoute, ContentRoute?> fallbackFor);
}

/// <summary>Content level: detail pages pushed within the current section, and back.</summary>
public interface IContentNavigator
{
    bool CanGoBack { get; }

    void Push(ContentRoute route);

    /// <summary>Replaces the current page without growing history (e.g. a new conversation once it exists).</summary>
    void Replace(ContentRoute route);

    void GoBack();
}
