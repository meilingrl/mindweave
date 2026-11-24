using Mindweave.Core.Reading;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Navigation;

public enum NavigationIntent
{
    /// <summary>Ordinary navigation; recorded in the target section's back history.</summary>
    Open,

    /// <summary>
    /// From a branch to the passage it came from. Follows the branch relation, never the back history, and always
    /// opens a new page so that back returns to the branch.
    /// </summary>
    ReturnToSource,
}

/// <summary>
/// Uniform navigation parameter: target section, optional target content, intent, and the viewer slot to show it
/// in (the current slot when omitted).
/// </summary>
public sealed record NavigationRequest(MainSection Section, ContentRoute? Content = null, NavigationIntent Intent = NavigationIntent.Open, ViewerSlotKey? Slot = null)
{
    /// <summary>
    /// The page whose own work, now done, asks for this: when it is no longer the page shown, the reader went elsewhere
    /// meanwhile and nothing opens over that.
    /// </summary>
    public PageViewModel? From { get; init; }
}
