using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Startup;

namespace Mindweave.Desktop.ViewModels;

/// <summary>
/// Shown while the database is opened and, if needed, backed up and migrated. When an upgrade begins it says so, and
/// whether a backup is made first (gap-schema-4, D-042).
/// </summary>
public sealed partial class StartupViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string Text { get; private set; } = UiText.StartupOpening;

    /// <summary>The upgrade has begun; called on the UI thread.</summary>
    public void Upgrading(DataUpgradeStarting step) => Text = UiText.StartupUpgrading(step);
}
