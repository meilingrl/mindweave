using System.Windows.Input;
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
    [NotifyPropertyChangedFor(nameof(Notice), nameof(ShowNotice))]
    public partial bool IsStoragePaused { get; set; }

    /// <summary>An answer is being written: the composer offers stop where send is (the conversation page sets it).</summary>
    [ObservableProperty]
    public partial bool IsAnswerRunning { get; set; }

    /// <summary>
    /// Recommended questions offered in the empty input as a placeholder (新探索 only, requirements §10.3.1): the first is
    /// drawn dimly, Tab puts it in. Null elsewhere.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<string>? GhostSuggestions { get; set; }

    /// <summary>The view puts the focus in the input, the caret at the end (after <see cref="Fill"/>).</summary>
    public event EventHandler? FillRequested;

    /// <summary>Puts <paramref name="text"/> in the input, not sent, and has the view focus it.</summary>
    public void Fill(string text)
    {
        Text = text;
        FillRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops the running answer; null on a page that never runs one (a new conversation).</summary>
    public ICommand? StopCommand { get; init; }

    /// <summary>
    /// The hosting page already explains a storage pause in its own notice, so the composer does not repeat it
    /// (S7: the same notice only once). A page without its own notice (a new conversation) leaves this false.
    /// </summary>
    public bool HostShowsStoragePause { get; init; }

    /// <summary>Why the last send was refused; cleared when the next one is accepted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSettingsLink), nameof(ShowNotice))]
    public partial SendRejection? Rejection { get; private set; }

    public string? Notice => IsStoragePaused ? UiText.SendRejected(SendRejection.StorageUnavailable) : Rejection is { } r ? UiText.SendRejected(r) : null;

    /// <summary>Whether the composer shows <see cref="Notice"/>: not when it only repeats the page's storage notice.</summary>
    public bool ShowNotice => Notice is not null && !(HostShowsStoragePause && IsStoragePaused);

    /// <summary>Refusals the reader can fix in settings: no model yet, or a model whose context is too small.</summary>
    public bool ShowSettingsLink => Rejection is SendRejection.NotConfigured or SendRejection.QuestionTooLargeForModel or SendRejection.HistoryTooLongForModel;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var sent = Text;
        var outcome = await _send(sent);
        Rejection = outcome.Rejection;
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(ShowNotice));
        if (outcome.Accepted)
        {
            // The question is stored; only now is the input cleared. A refused question stays editable. Text typed while
            // the send was under way is the next question, and stays (low finding ui-vm-10).
            if (Text == sent)
            {
                Text = string.Empty;
            }
        }
    }

    /// <summary>Shows the outcome of an action taken elsewhere on the page (e.g. retry).</summary>
    public void Show(SendRejection? rejection)
    {
        Rejection = rejection;
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(ShowNotice));
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
