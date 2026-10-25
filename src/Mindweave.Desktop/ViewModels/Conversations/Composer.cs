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

    /// <summary>Why the last send was refused; cleared when the next one is accepted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSettingsLink))]
    public partial SendRejection? Rejection { get; private set; }

    public string? Notice => Rejection is { } r ? UiText.SendRejected(r) : null;

    public bool ShowSettingsLink => Rejection == SendRejection.NotConfigured;

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

    private bool CanSend() => !IsBlocked && !string.IsNullOrWhiteSpace(Text);
}
