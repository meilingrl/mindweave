using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>One row of the version history.</summary>
public sealed partial class SummaryVersionItem(SummaryVersion version, bool isCurrent, bool isCandidate, bool isRead) : ObservableObject
{
    public SummaryVersion Version { get; } = version;

    public SummaryVersionId Id => Version.Id;

    public bool IsCurrent { get; } = isCurrent;

    public bool IsCandidate { get; } = isCandidate;

    /// <summary>Saved, non-current versions can become current; the candidate is adopted instead.</summary>
    public bool CanSetCurrent => !IsCurrent && !IsCandidate;

    /// <summary>The candidate is discarded, not deleted as a version.</summary>
    public bool CanSelect => !IsCandidate;

    public bool IsRead { get; } = isRead;

    public bool IsEdited => Version.Origin == SummaryOrigin.Edited;

    /// <summary>A saved version that is neither current nor the candidate.</summary>
    public bool IsHistory => !IsCurrent && !IsCandidate;

    /// <summary>The row in words, for screen readers; the view shows the same parts in columns.</summary>
    public string Label => UiText.VersionLabel(Version, IsCurrent, IsCandidate) + (IsRead ? " · " + UiText.MarkerRead : string.Empty);

    public string Number => UiText.VersionNumber(Version.Seq);

    public string Role => UiText.VersionRole(IsCurrent, IsCandidate);

    public string Origin => UiText.VersionOrigin(Version.Origin);

    public string Coverage => UiText.VersionCoverage(Version);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>The version on screen above; its row is highlighted and has no "view" action.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; set; }
}
