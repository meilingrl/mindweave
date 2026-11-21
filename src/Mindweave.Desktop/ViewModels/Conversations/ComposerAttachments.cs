using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Models;
using Mindweave.Application.Resources;
using Mindweave.Application.Storage;
using Mindweave.Core.Models;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>
/// What a composer needs for attachments: the resource service, the model settings (can the model see images?), and the
/// draft its attachments belong to (null until the page knows it, e.g. before a conversation's workspace is loaded).
/// </summary>
public sealed record ComposerAttachmentServices(IResources Resources, IModelSettings Models, Func<DraftSlot?> Draft);

/// <summary>The composer's attachments (requirements §10.4.3): "+", drop, paste, long paste, chips, viewer.</summary>
public sealed partial class Composer
{
    private readonly ComposerAttachmentServices? _attachments;
    private AttachmentChip? _lastConversion;
    private string? _textAtConversion;

    public ObservableCollection<AttachmentChip> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>An attachment is still being stored; a language switch waits for it (bilingual-ui.md §6.5).</summary>
    public bool IsStoringAttachments => Attachments.Any(c => c.IsWorking);

    /// <summary>This composer takes attachments ("+", drop, paste).</summary>
    public bool CanAttach => _attachments is not null;

    public AttachmentViewer Viewer { get; }

    /// <summary>The active model declares image input; otherwise 图片 is greyed out with <see cref="ImageUnavailableReason"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageUnavailableReason))]
    public partial bool CanAttachImages { get; private set; }

    public string? ImageUnavailableReason => CanAttachImages ? null : UiText.ImagesUnavailable;

    /// <summary>A short message about the last attach attempt (an image refused, too many attachments); cleared by the next.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachmentNotice))]
    public partial string? AttachmentNotice { get; private set; }

    public bool HasAttachmentNotice => AttachmentNotice is not null;

    /// <summary>The conversation holds images the active model cannot see (the page sets it): a line says they go as text.</summary>
    [ObservableProperty]
    public partial bool HistoryImagesUnseen { get; set; }

    /// <summary>Raised when 转为文字 put text into the input: where the caret goes (after the inserted text).</summary>
    public event EventHandler<int>? TextInserted;

    /// <summary>Reads whether the active model can see images; again whenever the model changes.</summary>
    public async Task RefreshCapabilitiesAsync()
    {
        if (_attachments is null)
        {
            return;
        }

        CanAttachImages = await _attachments.Models.ActiveModelHasAsync(ModelCapability.ImageInput, CancellationToken.None);
    }

    /// <summary>Shows the draft's stored attachments (after a restart, or when the page opens).</summary>
    public async Task LoadDraftAttachmentsAsync()
    {
        if (_attachments?.Draft() is not { } draft)
        {
            return;
        }

        var stored = await _attachments.Resources.GetDraftAsync(draft, CancellationToken.None);

        // Chips added while this read ran stay; a stored attachment is shown once, before them, in the draft's order.
        var at = 0;
        foreach (var resource in stored)
        {
            if (Attachments.Any(c => c.Resource == resource.Resource.Id))
            {
                continue;
            }

            var chip = AttachmentChip.For(resource, _attachments.Resources.HasOriginal(resource.Resource), RemoveChip, OpenChip, ConvertChip);
            Attachments.Insert(at++, chip);
            await LoadThumbnailAsync(chip);
        }
    }

    /// <summary>
    /// A paste into the input: a long text (requirements §10.4.3, owner's Q1: about 2,000 estimated tokens or more) becomes a
    /// 粘贴的文字 attachment and returns true; anything shorter returns false and is pasted as usual.
    /// </summary>
    public bool TryTakePaste(string text, int caret)
    {
        if (_attachments is null || string.IsNullOrEmpty(text) || !ResourceLimits.IsLongPaste(text) || !RoomForOneMore())
        {
            return false;
        }

        var chip = new AttachmentChip(UiText.PastedTextName, UiText.AttachmentType(Core.Resources.MediaTypes.PlainText, paste: true), false, true, RemoveChip, OpenChip, ConvertChip, RetryChipAsync)
        {
            Source = text,
            InsertAt = Math.Clamp(caret, 0, Text.Length),
        };
        _lastConversion = chip;
        _textAtConversion = Text;
        Attachments.Add(chip);
        _ = StoreAsync(chip);
        return true;
    }

    /// <summary>
    /// Undo right after a long paste became an attachment (⌘Z / Ctrl+Z, requirements §10.4.3): the text goes back into the
    /// input where it was pasted. Returns false when the last change was something else, so the input undoes as usual.
    /// </summary>
    public bool UndoConversion()
    {
        if (_lastConversion is not { } chip || _textAtConversion != Text || !Attachments.Contains(chip))
        {
            _lastConversion = null;
            return false;
        }

        ConvertChip(chip);
        return true;
    }

    /// <summary>Files chosen with "+", dropped or pasted. Images among them are refused when the model cannot see images.</summary>
    public async Task AttachFilesAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        AttachmentNotice = null;
        var work = new List<Task>();
        foreach (var path in paths)
        {
            var isImage = FileTypes.MediaTypeOf(Path.GetFileName(path)) is { } type && Core.Resources.MediaTypes.IsImage(type);
            if (isImage && !CanAttachImages)
            {
                AttachmentNotice = UiText.PastedImageRefused;
                continue;
            }

            if (!RoomForOneMore())
            {
                break;
            }

            var chip = new AttachmentChip(Path.GetFileName(path), UiText.AttachmentType(FileTypes.MediaTypeOf(Path.GetFileName(path)) ?? string.Empty, paste: false), isImage, false, RemoveChip, OpenChip, null, RetryChipAsync)
            {
                Source = path,
            };
            Attachments.Add(chip);
            work.Add(StoreAsync(chip));
        }

        await Task.WhenAll(work);
    }

    /// <summary>An image pasted from the clipboard (PNG bytes), refused with a reason when the model cannot see images.</summary>
    public async Task AttachImageAsync(byte[] bytes, string? name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        AttachmentNotice = null;
        if (!CanAttachImages)
        {
            AttachmentNotice = UiText.PastedImageRefused;
            return;
        }

        if (!RoomForOneMore())
        {
            return;
        }

        var chip = new AttachmentChip(name ?? UiText.PastedImageName, UiText.AttachmentType(Core.Resources.MediaTypes.Png, paste: false), true, false, RemoveChip, OpenChip, null, RetryChipAsync)
        {
            Source = bytes,
        };
        Attachments.Add(chip);
        await StoreAsync(chip);
    }

    private bool RoomForOneMore()
    {
        if (Attachments.Count < ResourceLimits.MaxAttachmentsPerMessage)
        {
            return true;
        }

        AttachmentNotice = UiText.TooManyAttachmentsNotice;
        return false;
    }

    private async Task StoreAsync(AttachmentChip chip)
    {
        if (_attachments is null)
        {
            return;
        }

        if (_attachments.Draft() is not { } draft)
        {
            chip.Fail(UiText.AttachmentSaveFailed, retryable: true);
            return;
        }

        ResourceCreateResult result;
        try
        {
            result = chip.Source switch
            {
                string text when chip.IsPaste => await _attachments.Resources.AttachPasteAsync(draft, text, CancellationToken.None),
                string path => await _attachments.Resources.AttachFileAsync(draft, path, CancellationToken.None),
                byte[] bytes => await _attachments.Resources.AttachImageAsync(draft, bytes, chip.Name, CancellationToken.None),
                _ => new ResourceCreateResult(ResourceCreateStatus.Unsupported),
            };
        }
        catch (StorageWriteException)
        {
            // A full disk: the chip says so and can be retried or removed; the input's text stays (§10.4.3).
            chip.Fail(UiText.AttachmentSaveFailed, retryable: true);
            return;
        }

        if (!Attachments.Contains(chip))
        {
            // Removed while it was being stored: the stored copy goes too.
            if (result.Resource is { } orphan)
            {
                await DeleteQuietlyAsync(orphan.Resource.Id);
            }

            return;
        }

        if (result.Resource is { } stored)
        {
            chip.Succeed(stored, originalThere: true);
            await LoadThumbnailAsync(chip);
        }
        else
        {
            chip.Fail(UiText.AttachmentProblem(result.Status, result.Detail, chip.IsImage), retryable: result.Status == ResourceCreateStatus.FileUnavailable);
        }

        SendCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadThumbnailAsync(AttachmentChip chip)
    {
        if (!chip.IsImage || chip.Resource is not { } id || _attachments is null)
        {
            return;
        }

        if (chip.Source is byte[] bytes)
        {
            chip.Thumbnail = bytes;
            return;
        }

        if (await _attachments.Resources.OpenOriginalAsync(id, CancellationToken.None) is { } stream)
        {
            await using (stream)
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                chip.Thumbnail = buffer.ToArray();
            }
        }
    }

    private void RemoveChip(AttachmentChip chip)
    {
        Attachments.Remove(chip);
        if (_lastConversion == chip)
        {
            _lastConversion = null;
        }

        if (chip.Resource is { } id)
        {
            _ = DeleteQuietlyAsync(id);
        }
    }

    /// <summary>转为文字: the paste goes back into the input where it was pasted, and the attachment is gone.</summary>
    private void ConvertChip(AttachmentChip chip)
    {
        var text = chip.Stored?.Text ?? chip.Source as string;
        if (text is null)
        {
            return;
        }

        var at = Math.Clamp(chip.InsertAt, 0, Text.Length);
        RemoveChip(chip);
        Text = Text.Insert(at, text);
        TextInserted?.Invoke(this, at + text.Length);
    }

    private void OpenChip(AttachmentChip chip) => _ = Viewer.OpenAsync(chip);

    /// <summary>Opens a sent question's attachment in the same viewer.</summary>
    public void Open(AttachmentChip chip)
    {
        ArgumentNullException.ThrowIfNull(chip);
        OpenChip(chip);
    }

    private Task RetryChipAsync(AttachmentChip chip)
    {
        chip.Restart();
        SendCommand.NotifyCanExecuteChanged();
        return StoreAsync(chip);
    }

    private async Task DeleteQuietlyAsync(Core.Resources.ResourceId id)
    {
        try
        {
            await _attachments!.Resources.DeleteUnattachedAsync(id, CancellationToken.None);
        }
#pragma warning disable CA1031 // A leftover unsent resource is removed by the startup clean-up.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
