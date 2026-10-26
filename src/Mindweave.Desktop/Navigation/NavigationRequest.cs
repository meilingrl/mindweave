using Mindweave.Core.Reading;

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
public sealed record NavigationRequest(MainSection Section, ContentRoute? Content = null, NavigationIntent Intent = NavigationIntent.Open, ViewerSlotKey? Slot = null);
