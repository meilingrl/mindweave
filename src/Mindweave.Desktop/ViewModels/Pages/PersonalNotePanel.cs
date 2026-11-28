using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Preferences;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// 设置 › 个人说明 (requirements §10.9): one text about the user and how they want answers, sent with every question and every
/// summary. It is saved as it is typed and applies to the next request; the box takes no more than
/// <see cref="PersonalNoteService.MaxLength"/> characters, and the count beside it says how many are used.
/// </summary>
public sealed partial class PersonalNotePanel : ViewModelBase
{
    private readonly IPersonalNote _note;
    private bool _loading = true;

    public PersonalNotePanel(IPersonalNote note)
    {
        _note = note;
        Loaded = LoadAsync();
    }

    public static int MaxLength => PersonalNoteService.MaxLength;

    /// <summary>Completes when the stored note is shown.</summary>
    public Task Loaded { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(IsFull))]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>How many characters are used, of how many.</summary>
    public string CountText => IsFull ? UiText.PersonalNoteFull(MaxLength) : UiText.PersonalNoteCount(Text.Length, MaxLength);

    /// <summary>Nothing more can be typed.</summary>
    public bool IsFull => Text.Length >= MaxLength;

    /// <summary>The latest save, for tests: completes once it is written or reported.</summary>
    internal Task Saving { get; private set; } = Task.CompletedTask;

    /// <summary>Why the note could not be saved; null otherwise.</summary>
    [ObservableProperty]
    public partial string? Status { get; private set; }

    /// <summary>Reads the stored note again (another workspace's settings page may have changed it).</summary>
    public void Refresh() => Loaded = LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            Text = await _note.GetAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // A note that cannot be read shows empty; the page goes on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(ex);
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnTextChanged(string value)
    {
        if (!_loading)
        {
            Saving = SaveAsync(value ?? string.Empty);
        }
    }

    private async Task SaveAsync(string value)
    {
        try
        {
            // The box never holds more; a longer text reaching here would be a mistake in the page, and is not cut.
            await _note.SetAsync(value, CancellationToken.None);
            Status = null;
        }
#pragma warning disable CA1031 // A preference that could not be written is said so on the page; the app goes on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(ex);
            Status = UiText.PersonalNoteSaveFailed;
        }
    }
}
