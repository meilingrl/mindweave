using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Resources;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>A page with an attachment viewer; the shell shows it over the window.</summary>
public interface IAttachmentViewerHost
{
    AttachmentViewer AttachmentViewer { get; }
}

/// <summary>
/// The read-only viewer of one attachment (requirements §10.4.3, architecture §15.9): its extracted text to read, select and
/// copy, or its image. Nothing is created from it in phase 1 (owner's Q5 decision: no branching from the viewer).
/// </summary>
public sealed partial class AttachmentViewer(IResources? resources) : ViewModelBase
{
    private int _opening;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Info { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    public partial string? Text { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    public partial byte[]? Image { get; private set; }

    /// <summary>The original is missing, or the text was cut at the storage bound.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; private set; }

    public bool HasText => Text is not null;

    public bool HasImage => Image is not null;

    public bool HasNote => Note is not null;

    public async Task OpenAsync(AttachmentChip chip)
    {
        ArgumentNullException.ThrowIfNull(chip);
        var ticket = Interlocked.Increment(ref _opening);
        Title = chip.Name;
        Info = $"{chip.TypeLabel} · {chip.SizeLabel}";
        Text = chip.Stored?.Text ?? chip.Block?.Text;
        Image = null;
        Note = chip.Stored?.TextTruncated == true ? UiText.TruncatedNote : null;
        IsOpen = true;
        if (resources is null || chip.Resource is not { } id)
        {
            return;
        }

        var stored = chip.Stored ?? await resources.GetAsync(id, CancellationToken.None);
        if (stored is null || ticket != _opening)
        {
            return;
        }

        Text ??= stored.Text;
        var there = resources.HasOriginal(stored.Resource);
        if (stored.TextTruncated)
        {
            Note = UiText.TruncatedNote;
        }

        if (!there)
        {
            Note = chip.IsImage ? UiText.ImageOriginalMissingNote : UiText.OriginalMissingNote;
            return;
        }

        if (chip.IsImage && await resources.OpenOriginalAsync(id, CancellationToken.None) is { } stream)
        {
            await using (stream)
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                if (ticket == _opening)
                {
                    Image = buffer.ToArray();
                }
            }
        }
    }

    [RelayCommand]
    public void Close()
    {
        Interlocked.Increment(ref _opening);
        IsOpen = false;
        Text = null;
        Image = null;
        Note = null;
    }
}
