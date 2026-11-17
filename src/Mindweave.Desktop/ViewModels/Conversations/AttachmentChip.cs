using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;

namespace Mindweave.Desktop.ViewModels.Conversations;

public enum AttachmentChipState
{
    /// <summary>Being read and stored; a question with it cannot be sent yet.</summary>
    Working,

    Ready,

    /// <summary>Could not be added (<see cref="AttachmentChip.Problem"/> says why); a question with it cannot be sent.</summary>
    Failed,
}

/// <summary>
/// One attachment as a chip (requirements §10.4.3): icon, name, type and size, remove; click to open the viewer. In the
/// composer it may be working, ready or failed; above a sent question it is always ready and cannot be removed.
/// </summary>
public sealed partial class AttachmentChip : ViewModelBase
{
    private readonly Action<AttachmentChip>? _remove;
    private readonly Action<AttachmentChip>? _open;
    private readonly Action<AttachmentChip>? _convert;
    private readonly Func<AttachmentChip, Task>? _retry;

    internal AttachmentChip(
        string name,
        string typeLabel,
        bool isImage,
        bool isPaste,
        Action<AttachmentChip>? remove,
        Action<AttachmentChip>? open,
        Action<AttachmentChip>? convert = null,
        Func<AttachmentChip, Task>? retry = null)
    {
        Name = name;
        TypeLabel = typeLabel;
        IsImage = isImage;
        IsPaste = isPaste;
        _remove = remove;
        _open = open;
        _convert = convert;
        _retry = retry;
    }

    /// <summary>A chip for a stored attachment: of a draft (removable) or of a sent question (<paramref name="remove"/> null).</summary>
    internal static AttachmentChip For(StoredResource stored, bool originalThere, Action<AttachmentChip>? remove, Action<AttachmentChip>? open, Action<AttachmentChip>? convert = null)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var r = stored.Resource;
        var paste = r.Origin == ResourceOrigin.Paste;
        var chip = new AttachmentChip(r.Name ?? ContentBlocks.PastedTextName, UiText.AttachmentType(r.MediaType, paste), r.IsImage, paste, remove, open, paste ? convert : null)
        {
            Stored = stored,
            Resource = r.Id,
            MediaType = r.MediaType,
            SizeLabel = stored.Text is { } text && paste ? UiText.AttachmentSize(text.Length) : UiText.ByteSize(r.Size),
            State = AttachmentChipState.Ready,
            IsOriginalMissing = !originalThere,
        };
        return chip;
    }

    /// <summary>A chip for an attachment block of a sent question.</summary>
    internal static AttachmentChip ForBlock(AttachmentBlock block, Action<AttachmentChip>? open)
    {
        ArgumentNullException.ThrowIfNull(block);
        var paste = block.Name is null && !MediaTypes.IsImage(block.MediaType);
        return new AttachmentChip(block.DisplayName, UiText.AttachmentType(block.MediaType, paste), MediaTypes.IsImage(block.MediaType), paste, null, open)
        {
            Block = block,
            Resource = block.Resource,
            MediaType = block.MediaType,
            SizeLabel = paste && block.Text is { } text ? UiText.AttachmentSize(text.Length) : UiText.ByteSize(block.Size),
            State = AttachmentChipState.Ready,
        };
    }

    public string Name { get; }

    public string TypeLabel { get; }

    public bool IsImage { get; }

    public bool IsPaste { get; }

    public bool IsFile => !IsPaste && !IsImage;

    public string MediaType { get; private init; } = string.Empty;

    /// <summary>The stored resource once there is one.</summary>
    public ResourceId? Resource { get; private set; }

    internal StoredResource? Stored { get; private set; }

    internal AttachmentBlock? Block { get; private init; }

    /// <summary>What a failed chip retries with: the paste's text, the file's path or the image's bytes.</summary>
    internal object? Source { get; init; }

    /// <summary>Where a long paste was in the input, so 转为文字 puts it back there.</summary>
    internal int InsertAt { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking), nameof(IsFailed), nameof(IsReady), nameof(Detail), nameof(CanConvert))]
    public partial AttachmentChipState State { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    public partial string SizeLabel { get; private set; } = string.Empty;

    /// <summary>Why it could not be added.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    public partial string? Problem { get; private set; }

    /// <summary>The failure can be retried (saving failed); a refused file cannot.</summary>
    [ObservableProperty]
    public partial bool CanRetry { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    public partial bool IsOriginalMissing { get; private set; }

    /// <summary>A small picture of an image attachment, as encoded bytes (the view decodes it).</summary>
    [ObservableProperty]
    public partial byte[]? Thumbnail { get; set; }

    public bool IsWorking => State == AttachmentChipState.Working;

    public bool IsFailed => State == AttachmentChipState.Failed;

    public bool IsReady => State == AttachmentChipState.Ready;

    public bool CanRemove => _remove is not null;

    public bool CanConvert => _convert is not null && IsReady && IsPaste;

    /// <summary>The line under the name: type and size, or what is going on.</summary>
    public string Detail => State switch
    {
        AttachmentChipState.Working => UiText.Extracting,
        AttachmentChipState.Failed => Problem ?? string.Empty,
        _ => IsOriginalMissing ? $"{TypeLabel} · {UiText.OriginalMissing}" : $"{TypeLabel} · {SizeLabel}",
    };

    /// <summary>The name a screen reader says, with the detail.</summary>
    public string AccessibleName => $"附件：{Name}";

    internal void Succeed(StoredResource stored, bool originalThere)
    {
        Stored = stored;
        Resource = stored.Resource.Id;
        SizeLabel = stored.Text is { } text && IsPaste ? UiText.AttachmentSize(text.Length) : UiText.ByteSize(stored.Resource.Size);
        IsOriginalMissing = !originalThere;
        Problem = null;
        CanRetry = false;
        State = AttachmentChipState.Ready;
    }

    internal void Fail(string problem, bool retryable)
    {
        Problem = problem;
        CanRetry = retryable;
        State = AttachmentChipState.Failed;
    }

    internal void Restart()
    {
        Problem = null;
        CanRetry = false;
        State = AttachmentChipState.Working;
    }

    [RelayCommand]
    private void Remove() => _remove?.Invoke(this);

    [RelayCommand]
    private void Open()
    {
        if (IsReady)
        {
            _open?.Invoke(this);
        }
    }

    [RelayCommand]
    private void ConvertToText() => _convert?.Invoke(this);

    [RelayCommand]
    private Task RetryAsync() => _retry?.Invoke(this) ?? Task.CompletedTask;
}
