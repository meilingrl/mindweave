using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Conversations;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>Question input shared by the new-conversation and conversation pages.</summary>
public sealed partial class Composer : ViewModelBase
{
    private readonly Func<string, Task<SendOutcome>> _send;

    public Composer(Func<string, Task<SendOutcome>> send) => _send = send;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool IsBlocked { get; set; }

    /// <summary>Core storage is failing (§9.3): sending is paused and says why; the text stays editable.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(Notice))]
    public partial bool IsStoragePaused { get; set; }

    /// <summary>Why the last send was refused; cleared when the next one is accepted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSettingsLink))]
    public partial SendRejection? Rejection { get; private set; }

    public string? Notice => IsStoragePaused ? UiText.SendRejected(SendRejection.StorageUnavailable) : Rejection is { } r ? UiText.SendRejected(r) : null;

    /// <summary>Refusals the reader can fix in settings: no model yet, or a model whose context is too small.</summary>
    public bool ShowSettingsLink => Rejection is SendRejection.NotConfigured or SendRejection.QuestionTooLargeForModel or SendRejection.HistoryTooLongForModel;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var outcome = await _send(Text);
        Rejection = outcome.Rejection;
        OnPropertyChanged(nameof(Notice));
        if (outcome.Accepted)
        {
            // The question is stored; only now is the input cleared. A refused question stays editable.
            Text = string.Empty;
        }
    }

    /// <summary>Shows the outcome of an action taken elsewhere on the page (e.g. retry).</summary>
    public void Show(SendRejection? rejection)
    {
        Rejection = rejection;
        OnPropertyChanged(nameof(Notice));
    }

    private bool CanSend() => !IsBlocked && !IsStoragePaused && !string.IsNullOrWhiteSpace(Text);

    partial void OnIsStoragePausedChanged(bool value)
    {
        // A refusal for the pause that just ended is stale.
        if (!value && Rejection == SendRejection.StorageUnavailable)
        {
            Rejection = null;
        }
    }
}
