using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Preferences;

namespace Mindweave.Desktop.Services;

/// <summary>
/// The text width in effect, shared by the conversation pages (dragging the text's edge) and 设置 › 外观. Restored when the
/// database opens, kept in the preference store when changed.
/// </summary>
public sealed class TextWidth(ITextWidth? store = null) : ObservableObject
{
    private double? _pixels = TextWidthService.Default;

    /// <summary>The widest the text may be; null: as wide as the page.</summary>
    public double? Pixels
    {
        get => _pixels;
        set => SetProperty(ref _pixels, TextWidthService.Clamp(value));
    }

    /// <summary>Completes when the last change has been written (or failed); tests await it.</summary>
    internal Task Saving { get; private set; } = Task.CompletedTask;

    public async Task RestoreAsync()
    {
        if (store is null)
        {
            return;
        }

        try
        {
            Pixels = await store.GetAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // The width is cosmetic: unreadable means the default, as for the sidebar.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
        }
    }

    /// <summary>Keeps the width; called once when a drag ends, not at every pointer move.</summary>
    public void Save()
    {
        if (store is null)
        {
            return;
        }

        // One write after another, so the last change made is the one that stays.
        var width = Pixels;
        var previous = Saving;
        Saving = SaveAsync();

        async Task SaveAsync()
        {
            await previous;
            try
            {
                await store.SetAsync(width, CancellationToken.None);
            }
#pragma warning disable CA1031 // A lost width is not worth an error: it stays as it is for this run.
            catch (Exception swallowed)
#pragma warning restore CA1031
            {
                DesktopLog.Swallowed(swallowed);
            }
        }
    }
}
