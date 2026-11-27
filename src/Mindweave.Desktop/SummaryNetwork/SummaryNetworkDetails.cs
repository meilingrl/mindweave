using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Preferences;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>
/// The summary network page's details beside the canvas, for every workspace alike: their width and whether they are
/// folded away. Restored when the database opens, kept in the preference store when changed.
/// </summary>
public sealed partial class SummaryNetworkDetails(ISummaryNetworkDetailsLayout store) : ObservableObject
{
    private double _width = SummaryNetworkDetailsLayout.DefaultWidth;

    /// <summary>Always within <see cref="SummaryNetworkDetailsLayout"/>'s bounds.</summary>
    public double Width
    {
        get => _width;
        set => SetProperty(ref _width, SummaryNetworkDetailsLayout.ClampWidth(value));
    }

    [ObservableProperty]
    public partial bool IsCollapsed { get; private set; }

    /// <summary>Completes when the last change has been written (or failed); tests await it.</summary>
    internal Task Saving { get; private set; } = Task.CompletedTask;

    public async Task RestoreAsync()
    {
        try
        {
            var layout = await store.GetAsync(CancellationToken.None);
            Width = layout.Width;
            IsCollapsed = layout.IsCollapsed;
        }
#pragma warning disable CA1031 // The layout is cosmetic: unreadable means the default, as for the sidebar.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
        }
    }

    public void Collapse(bool collapsed)
    {
        IsCollapsed = collapsed;
        Save();
    }

    /// <summary>Keeps the layout; called once when a resize ends, not at every pointer move.</summary>
    public void Save()
    {
        // One write after another, so the last change made is the one that stays.
        var layout = new SummaryNetworkDetailsLayout(Width, IsCollapsed);
        var previous = Saving;
        Saving = SaveAsync();

        async Task SaveAsync()
        {
            await previous;
            try
            {
                await store.SetAsync(layout, CancellationToken.None);
            }
#pragma warning disable CA1031 // A lost layout is not worth an error: it stays as it is for this run.
            catch (Exception swallowed)
#pragma warning restore CA1031
            {
                DesktopLog.Swallowed(swallowed);
            }
        }
    }
}
