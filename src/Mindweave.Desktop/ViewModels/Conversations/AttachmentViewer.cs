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
        Highlight = null;
        SourceUrl = null;
        IsConfirmingOpen = false;
        IsAddressOnly = false;
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
        var there = await resources.HasOriginalAsync(stored.Resource);
        if (ticket != _opening)
        {
            return;
        }

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

    /// <summary>The cited passage as a range of <see cref="Text"/>, highlighted and scrolled to; null when none.</summary>
    [ObservableProperty]
    public partial (int Start, int End)? Highlight { get; private set; }

    /// <summary>A 出处's real address (requirements §10.4.5); null for an attachment or tool result.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSourceUrl), nameof(SourceAddress))]
    public partial Uri? SourceUrl { get; private set; }

    public bool HasSourceUrl => SourceUrl is not null;

    public string? SourceAddress => SourceUrl?.AbsoluteUri;

    /// <summary>在浏览器中打开 was pressed: the full address is shown and must be confirmed before the browser opens it.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingOpen { get; private set; }

    /// <summary>A received web citation has no saved page: the dialog holds only the question whether to open the address.</summary>
    [ObservableProperty]
    public partial bool IsAddressOnly { get; private set; }

    /// <summary>The view opens the confirmed address in the system browser (the launcher belongs to the window).</summary>
    public event EventHandler<Uri>? OpenRequested;

    /// <summary>
    /// Opens a 出处 (requirements §10.4.5): the stored page or search results, the cited passage highlighted, the real address
    /// with 在浏览器中打开 (shown in full before anything opens). One that arrived in a share has only that last step
    /// (docs/design/sharing-import.md §3).
    /// </summary>
    public async Task OpenCitationAsync(Core.Conversations.CitationBlock citation)
    {
        ArgumentNullException.ThrowIfNull(citation);
        var ticket = Interlocked.Increment(ref _opening);
        Title = UiText.CitationLabel(citation.Kind, citation.Title);
        Info = citation.Url is null ? UiText.LocalCitationInfo(citation.Number) : UiText.CitationInfo(citation.Number, citation.FetchedAt);
        Text = null;
        Image = null;
        IsAddressOnly = citation is { Resource: null, Url: not null };
        Note = citation.Url is null || IsAddressOnly ? null : UiText.WebSourceUntrusted;
        Highlight = null;
        SourceUrl = citation.Url;
        IsConfirmingOpen = IsAddressOnly;
        IsOpen = true;
        if (resources is null || citation.Resource is not { } resource || await resources.GetAsync(resource, CancellationToken.None) is not { } stored || ticket != _opening)
        {
            return;
        }

        Text = stored.Text;
        if (stored.Text is { } text && citation.Start >= 0 && citation.End <= text.Length && citation.Start < citation.End)
        {
            Highlight = (citation.Start, citation.End);
        }
    }

    [RelayCommand]
    private void AskToOpen() => IsConfirmingOpen = SourceUrl is not null;

    [RelayCommand]
    private void CancelOpen()
    {
        IsConfirmingOpen = false;
        CloseAddressOnly();
    }

    private void CloseAddressOnly()
    {
        if (IsAddressOnly)
        {
            Close();
        }
    }

    [RelayCommand]
    private void ConfirmOpen()
    {
        if (IsConfirmingOpen && SourceUrl is { } url && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
        {
            OpenRequested?.Invoke(this, url);
        }

        IsConfirmingOpen = false;
        CloseAddressOnly();
    }

    [RelayCommand]
    public void Close()
    {
        Interlocked.Increment(ref _opening);
        IsOpen = false;
        Text = null;
        Image = null;
        Note = null;
        Highlight = null;
        SourceUrl = null;
        IsConfirmingOpen = false;
        IsAddressOnly = false;
    }
}
