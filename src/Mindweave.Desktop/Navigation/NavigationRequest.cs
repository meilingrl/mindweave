namespace Mindweave.Desktop.Navigation;

public enum NavigationIntent
{
    /// <summary>Ordinary navigation; recorded in the target section's back history.</summary>
    Open,
}

/// <summary>Uniform navigation parameter: target section, optional target content, and intent.</summary>
public sealed record NavigationRequest(MainSection Section, ContentRoute? Content = null, NavigationIntent Intent = NavigationIntent.Open);
